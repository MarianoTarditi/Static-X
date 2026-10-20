using BLL;
using DAL;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Static_X.Tests;

public sealed class ActivationTests
{
    [Fact]
    public async Task Provision_IdentityFailure_RollsBackGymAndAllDeliveryRows()
    {
        await using var fixture = await Fixture.CreateAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.ProvisionAsync("request-a", "North", "blocked@example.com"));
        Assert.Equal(0, await fixture.Context.Gimnasios.CountAsync());
        Assert.Equal(0, await fixture.Context.Users.CountAsync());
        Assert.Equal(0, await fixture.Context.ActivationInvitations.CountAsync());
        Assert.Equal(0, await fixture.Context.ActivationOutbox.CountAsync());
        Assert.Equal(0, await fixture.Context.ProvisioningRequests.CountAsync());
    }

    [Fact]
    public async Task Provision_SameRequestKey_ReusesGymAndPendingInvitation()
    {
        await using var fixture = await Fixture.CreateAsync();
        var id = await fixture.Service.ProvisionAsync("request-a", "North", "admin@example.com");
        Assert.Equal(id, await fixture.Service.ProvisionAsync("request-a", "North", "admin@example.com"));
        Assert.Equal(1, await fixture.Context.Gimnasios.CountAsync());
        Assert.Equal(1, await fixture.Context.Users.CountAsync());
        Assert.Equal(1, await fixture.Context.ActivationOutbox.CountAsync());
        Assert.Equal(1, await fixture.Context.ActivationInvitations.CountAsync());
        var user = await fixture.Context.Users.SingleAsync();
        Assert.Null(user.PasswordHash);
        Assert.False(user.EmailConfirmed);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.ProvisionAsync("request-a", "South", "admin@example.com"));
        Assert.Equal(1, await fixture.Context.Gimnasios.CountAsync());
    }

    [Fact]
    public async Task Provision_WithoutConfiguredKeyRing_DoesNotWriteAnyRows()
    {
        await using var fixture = await Fixture.CreateAsync(configureKeyRing: false);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.ProvisionAsync("request-a", "North", "admin@example.com"));
        Assert.Equal(0, await fixture.Context.Gimnasios.CountAsync());
        Assert.Equal(0, await fixture.Context.ActivationOutbox.CountAsync());
    }

    [Fact]
    public async Task Activate_WeakPassword_RollsBackClaimAndAllowsValidRetry()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Service.ProvisionAsync("request-a", "North", "admin@example.com");
        var token = await fixture.GetTokenAsync();
        Assert.False(await fixture.Service.ActivateAsync(token, "weak"));
        Assert.Null((await fixture.Context.ActivationInvitations.AsNoTracking().SingleAsync()).ConsumedAt);
        Assert.Null((await fixture.Context.Users.AsNoTracking().SingleAsync()).PasswordHash);
        Assert.True(await fixture.Service.ActivateAsync(token, "StrongPassword1!"));
    }

    [Fact]
    public async Task Activate_ValidToken_SetsPasswordAndEmailOnceAndErasesDeliverySecret()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Service.ProvisionAsync("request-a", "North", "admin@example.com");
        var token = await fixture.GetTokenAsync();
        Assert.True(await fixture.Service.ActivateAsync(token, "StrongPassword1!"));
        Assert.False(await fixture.Service.ActivateAsync(token, "OtherPassword1!"));
        var user = await fixture.Context.Users.SingleAsync();
        Assert.True(user.EmailConfirmed);
        Assert.True(await fixture.Users.CheckPasswordAsync(user, "StrongPassword1!"));
        Assert.False(await fixture.Users.CheckPasswordAsync(user, "OtherPassword1!"));
        Assert.NotNull((await fixture.Context.ActivationInvitations.AsNoTracking().SingleAsync()).ConsumedAt);
        Assert.Equal(string.Empty, (await fixture.Context.ActivationOutbox.AsNoTracking().SingleAsync()).ProtectedToken);
    }

    [Fact]
    public async Task Activate_ExpiredOrMismatchedAccount_DoesNotSetPasswordOrConsumeToken()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Service.ProvisionAsync("request-a", "North", "admin@example.com");
        var token = await fixture.GetTokenAsync();
        var invitation = await fixture.Context.ActivationInvitations.SingleAsync();
        var originalAccount = invitation.AccountId;
        var trainer = new GymAccount
        {
            Id = Guid.NewGuid().ToString("N"), UserName = Guid.NewGuid().ToString("N"),
            GymId = invitation.GymId, Email = "trainer@example.com", Role = AccountRole.Trainer
        };
        Assert.True((await fixture.Users.CreateAsync(trainer)).Succeeded);
        invitation.AccountId = trainer.Id;
        await fixture.Context.SaveChangesAsync();
        Assert.False(await fixture.Service.ActivateAsync(token, "StrongPassword1!"));
        Assert.Null(invitation.ConsumedAt);
        Assert.Null((await fixture.Users.FindByIdAsync(trainer.Id))?.PasswordHash);
        invitation.AccountId = originalAccount;
        invitation.ExpiresAt = DateTime.UtcNow.AddSeconds(-1);
        await fixture.Context.SaveChangesAsync();
        Assert.False(await fixture.Service.ActivateAsync(token, "StrongPassword1!"));
        Assert.Null((await fixture.Users.FindByIdAsync(originalAccount))?.PasswordHash);
        Assert.Null(invitation.ConsumedAt);
    }

    [Fact]
    public async Task Activate_ConcurrentClaims_CannotBothSetPassword()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Service.ProvisionAsync("request-a", "North", "admin@example.com");
        var token = await fixture.GetTokenAsync();
        using var first = fixture.CreateScope();
        using var second = fixture.CreateScope();
        var firstService = first.ServiceProvider.GetRequiredService<ActivationService>();
        var secondService = second.ServiceProvider.GetRequiredService<ActivationService>();

        // SQLite can reject one simultaneous writer with SQLITE_LOCKED; it must not activate twice.
        async Task<bool> Attempt(ActivationService service, string password)
        {
            try { return await service.ActivateAsync(token, password); }
            catch (SqliteException ex) when (ex.SqliteErrorCode is 5 or 6) { return false; }
        }

        var attempts = await Task.WhenAll(Attempt(firstService, "FirstPassword1!"),
            Attempt(secondService, "SecondPassword1!"));
        Assert.Equal(1, attempts.Count(success => success));
        var user = await fixture.Context.Users.AsNoTracking().SingleAsync();
        Assert.True(user.EmailConfirmed);
        Assert.Equal(1, await fixture.Context.ActivationInvitations.AsNoTracking()
            .CountAsync(i => i.ConsumedAt != null));
        Assert.Equal(1, new[] { "FirstPassword1!", "SecondPassword1!" }
            .Count(password => fixture.Users.PasswordHasher.VerifyHashedPassword(user, user.PasswordHash!, password)
                != PasswordVerificationResult.Failed));
    }

    [Fact]
    public async Task Reissue_RevokesPreviousTokenAndSupersedesPendingDelivery()
    {
        await using var fixture = await Fixture.CreateAsync();
        var gymId = await fixture.Service.ProvisionAsync("request-a", "North", "admin@example.com");
        var oldToken = await fixture.GetTokenAsync();
        var accountId = (await fixture.Context.Users.SingleAsync()).Id;
        Assert.False(await fixture.Service.ReissueAsync(gymId + 1, accountId));
        Assert.True(await fixture.Service.ReissueAsync(gymId, accountId));
        Assert.Equal(gymId, await fixture.Service.ProvisionAsync("request-a", "North", "admin@example.com"));
        Assert.Equal(2, await fixture.Context.ActivationInvitations.CountAsync());
        var old = await fixture.Context.ActivationInvitations.SingleAsync(i => i.RevokedAt != null);
        Assert.NotNull(old.RevokedAt);
        Assert.Equal(string.Empty, (await fixture.Context.ActivationOutbox.AsNoTracking().SingleAsync(o => o.InvitationId == old.Id)).ProtectedToken);
        Assert.False(await fixture.Service.ActivateAsync(oldToken, "StrongPassword1!"));
        var current = await fixture.Context.ActivationInvitations.SingleAsync(i => i.RevokedAt == null);
        var payload = await fixture.Context.ActivationOutbox.SingleAsync(o => o.InvitationId == current.Id);
        var token = fixture.Unprotect(current.Id, payload.ProtectedToken);
        Assert.True(await fixture.Service.ActivateAsync(token, "StrongPassword1!"));
        Assert.False(await fixture.Service.ReissueAsync(gymId, accountId));
    }

    [Fact]
    public async Task Reissue_ExpiredInvitation_RecoversWithoutChangingProvisioningKey()
    {
        await using var fixture = await Fixture.CreateAsync();
        var gymId = await fixture.Service.ProvisionAsync("request-a", "North", "admin@example.com");
        var previous = await fixture.Context.ActivationInvitations.SingleAsync();
        var accountId = previous.AccountId;
        previous.ExpiresAt = DateTime.UtcNow.AddMinutes(-1);
        await fixture.Context.SaveChangesAsync();
        Assert.True(await fixture.Service.ReissueAsync(gymId, accountId));
        Assert.Equal(gymId, await fixture.Service.ProvisionAsync("request-a", "North", "admin@example.com"));
        Assert.Equal(2, await fixture.Context.ActivationInvitations.CountAsync());
        Assert.Equal(1, await fixture.Context.ActivationInvitations.CountAsync(i => i.RevokedAt == null));
    }

    [Fact]
    public async Task Dispatch_CompetingLeasesAndUnknownAcceptance_ReuseJobKey()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Service.ProvisionAsync("request-a", "North", "admin@example.com");
        using var second = fixture.CreateScope();
        var store1 = fixture.CreateScope();
        using (store1)
        {
            var first = store1.ServiceProvider.GetRequiredService<IActivationDeliveryStore>();
            var other = second.ServiceProvider.GetRequiredService<IActivationDeliveryStore>();
            var now = DateTimeOffset.UtcNow;
            var lease = await first.LeaseDueAsync(now, default);
            Assert.NotNull(lease);
            Assert.Null(await other.LeaseDueAsync(now, default));
            await first.CompleteAsync(lease!, now, false, default);
            var job = await fixture.Context.ActivationOutbox.AsNoTracking().SingleAsync();
            Assert.Equal(1, job.AttemptCount);
            Assert.Equal(lease.JobId, job.Id);
            Assert.True(job.NextAttemptAt > now.UtcDateTime);
            var retry = await other.LeaseDueAsync(now.AddMinutes(2), default);
            Assert.NotNull(retry);
            Assert.Equal(lease.JobId, retry!.JobId);
            await other.CompleteAsync(retry, now.AddMinutes(2), true, default);
            job = await fixture.Context.ActivationOutbox.AsNoTracking().SingleAsync();
            Assert.NotNull(job.SentAt);
            Assert.Equal(string.Empty, job.ProtectedToken);
            Assert.Null(await first.LeaseDueAsync(now.AddMinutes(3), default));
        }
    }

    [Fact]
    public async Task Dispatch_ExpiredInvitation_DiscardsSecretWithoutSending()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Service.ProvisionAsync("request-a", "North", "admin@example.com");
        var invitation = await fixture.Context.ActivationInvitations.SingleAsync();
        invitation.ExpiresAt = DateTime.UtcNow.AddMinutes(-1);
        await fixture.Context.SaveChangesAsync();
        Assert.Null(await fixture.DeliveryStore.LeaseDueAsync(DateTimeOffset.UtcNow, default));
        Assert.Equal(string.Empty, (await fixture.Context.ActivationOutbox.AsNoTracking().SingleAsync()).ProtectedToken);
    }

    [Fact]
    public async Task Dispatch_MissingConfiguration_DoesNotLeaseOrSend()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Service.ProvisionAsync("request-a", "North", "admin@example.com");
        var sender = new FakeSender();
        var dispatcher = new ActivationDispatcher(fixture.DeliveryStore, fixture.Protection,
            fixture.Configuration, TimeProvider.System, sender);
        await Assert.ThrowsAsync<InvalidOperationException>(() => dispatcher.DispatchOneAsync());
        Assert.Equal(0, (await fixture.Context.ActivationOutbox.AsNoTracking().SingleAsync()).AttemptCount);
        Assert.Null((await fixture.Context.ActivationOutbox.AsNoTracking().SingleAsync()).LeaseOwner);
        Assert.Equal(0, sender.Calls);
        fixture.Configuration["Resend:ApiKey"] = "re_test_only";
        fixture.Configuration["Resend:FromAddress"] = "sender@example.com";
        fixture.Configuration["Activation:FrontendUrl"] = "https://app.example.com/activate";
        fixture.Configuration["Activation:KeyRingPath"] = null;
        await Assert.ThrowsAsync<InvalidOperationException>(() => dispatcher.DispatchOneAsync());
        Assert.Null((await fixture.Context.ActivationOutbox.AsNoTracking().SingleAsync()).LeaseOwner);
        Assert.Equal(0, sender.Calls);
    }

    [Fact]
    public async Task Dispatch_FakeProviderRetry_UsesSameKeyAndFragmentOnly()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Service.ProvisionAsync("request-a", "North", "admin@example.com");
        fixture.Configuration["Resend:ApiKey"] = "re_test_only";
        fixture.Configuration["Resend:FromAddress"] = "sender@example.com";
        fixture.Configuration["Activation:FrontendUrl"] = "https://app.example.com/activate";
        var sender = new FakeSender();
        var dispatcher = new ActivationDispatcher(fixture.DeliveryStore, fixture.Protection,
            fixture.Configuration, TimeProvider.System, sender);
        Assert.True(await dispatcher.DispatchOneAsync());
        Assert.Equal(1, sender.Calls);
        Assert.StartsWith("activation/", sender.Key);
        Assert.StartsWith("https://app.example.com/activate#activation=", sender.Link);
        Assert.DoesNotContain('?', sender.Link);
        Assert.Equal("admin@example.com", sender.To);
        Assert.NotNull((await fixture.Context.ActivationOutbox.AsNoTracking().SingleAsync()).SentAt);
        Assert.Equal(string.Empty, (await fixture.Context.ActivationOutbox.AsNoTracking().SingleAsync()).ProtectedToken);
    }

    [Fact]
    public async Task Dispatch_FailedProvider_RetriesWithStableIdempotencyKey()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Service.ProvisionAsync("request-a", "North", "admin@example.com");
        fixture.Configuration["Resend:ApiKey"] = "re_test_only";
        fixture.Configuration["Resend:FromAddress"] = "sender@example.com";
        fixture.Configuration["Activation:FrontendUrl"] = "https://app.example.com/activate";
        var clock = new MutableClock { Now = DateTimeOffset.UtcNow };
        var sender = new FakeSender { Accept = false };
        var dispatcher = new ActivationDispatcher(fixture.DeliveryStore, fixture.Protection,
            fixture.Configuration, clock, sender);
        Assert.True(await dispatcher.DispatchOneAsync());
        var key = sender.Key;
        Assert.Null((await fixture.Context.ActivationOutbox.AsNoTracking().SingleAsync()).SentAt);
        clock.Now = clock.Now.AddMinutes(2);
        sender.Accept = true;
        Assert.True(await dispatcher.DispatchOneAsync());
        Assert.Equal(2, sender.Calls);
        Assert.Equal(key, sender.Key);
        Assert.Equal(2, (await fixture.Context.ActivationOutbox.AsNoTracking().SingleAsync()).AttemptCount);
        Assert.NotNull((await fixture.Context.ActivationOutbox.AsNoTracking().SingleAsync()).SentAt);
    }

    [Fact]
    public async Task Dispatch_ExpiredLease_CanBeReclaimedAfterCrash()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Service.ProvisionAsync("request-a", "North", "admin@example.com");
        var now = DateTimeOffset.UtcNow;
        var first = await fixture.DeliveryStore.LeaseDueAsync(now, default);
        Assert.NotNull(first);
        using var otherScope = fixture.CreateScope();
        var other = otherScope.ServiceProvider.GetRequiredService<IActivationDeliveryStore>();
        var reclaimed = await other.LeaseDueAsync(now.AddMinutes(6), default);
        Assert.NotNull(reclaimed);
        Assert.Equal(first!.JobId, reclaimed!.JobId);
        Assert.NotEqual(first.LeaseOwner, reclaimed.LeaseOwner);
        Assert.False(await fixture.DeliveryStore.IsCurrentAsync(first, now.AddMinutes(6), default));
        await fixture.DeliveryStore.CompleteAsync(first, now.AddMinutes(6), true, default);
        Assert.Null((await fixture.Context.ActivationOutbox.AsNoTracking().SingleAsync()).SentAt);
    }

    private sealed class MutableClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; }
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FakeSender : IActivationEmailSender
    {
        public bool Accept { get; set; } = true;
        public int Calls { get; private set; }
        public string Key { get; private set; } = string.Empty;
        public string Link { get; private set; } = string.Empty;
        public string To { get; private set; } = string.Empty;
        public Task<bool> SendAsync(string idempotencyKey, string from, string to, string link, CancellationToken ct)
        {
            Calls++;
            Key = idempotencyKey;
            Link = link;
            To = to;
            return Task.FromResult(Accept);
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        private readonly ServiceProvider provider;
        private readonly IServiceScope scope;
        private readonly string keyDirectory;
        public dbcontext Context { get; }
        public UserManager<GymAccount> Users { get; }
        public ActivationService Service { get; }
        public IActivationDeliveryStore DeliveryStore => scope.ServiceProvider.GetRequiredService<IActivationDeliveryStore>();
        public IConfiguration Configuration => scope.ServiceProvider.GetRequiredService<IConfiguration>();
        public IDataProtectionProvider Protection => protection;
        public string Unprotect(Guid id, string payload) =>
            protection.CreateProtector("Static-X.ActivationDelivery.v1", id.ToString("N")).Unprotect(payload);
        public IServiceScope CreateScope() => provider.CreateScope();
        private readonly IDataProtectionProvider protection;

        private Fixture(SqliteConnection connection, ServiceProvider provider, IServiceScope scope, string keyDirectory)
        {
            this.connection = connection;
            this.provider = provider;
            this.scope = scope;
            this.keyDirectory = keyDirectory;
            Context = scope.ServiceProvider.GetRequiredService<dbcontext>();
            Users = scope.ServiceProvider.GetRequiredService<UserManager<GymAccount>>();
            Service = scope.ServiceProvider.GetRequiredService<ActivationService>();
            protection = scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>();
        }

        public static async Task<Fixture> CreateAsync(bool configureKeyRing = true)
        {
            var path = Path.Combine(Path.GetTempPath(), "static-x-activation-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            var database = "activation-" + Guid.NewGuid().ToString("N");
            var connectionString = $"Data Source={database};Mode=Memory;Cache=Shared";
            var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { ["Activation:KeyRingPath"] = configureKeyRing ? path : null }).Build());
            services.AddSingleton(TimeProvider.System);
            services.AddDataProtection().SetApplicationName("Static-X.Activation")
                .PersistKeysToFileSystem(new DirectoryInfo(path));
            services.AddDbContext<dbcontext>(o => o.UseSqlite(connectionString));
            services.AddIdentityCore<GymAccount>(o => o.User.RequireUniqueEmail = false)
                .AddEntityFrameworkStores<dbcontext>();
            services.AddScoped<IUserValidator<GymAccount>, RejectBlockedEmail>();
            services.AddScoped<IActivationStore, ActivationStore>();
            services.AddScoped<ActivationService>();
            services.AddScoped<IActivationDeliveryStore, ActivationDeliveryStore>();
            var provider = services.BuildServiceProvider();
            var fixture = new Fixture(connection, provider, provider.CreateScope(), path);
            await fixture.Context.Database.EnsureCreatedAsync();
            return fixture;
        }

        public async Task<string> GetTokenAsync()
        {
            var invitation = await Context.ActivationInvitations.SingleAsync();
            var payload = await Context.ActivationOutbox.SingleAsync();
            return protection.CreateProtector("Static-X.ActivationDelivery.v1", invitation.Id.ToString("N"))
                .Unprotect(payload.ProtectedToken);
        }

        public async ValueTask DisposeAsync()
        {
            scope.Dispose();
            await provider.DisposeAsync();
            await connection.DisposeAsync();
            Directory.Delete(keyDirectory, true);
        }
    }

    private sealed class RejectBlockedEmail : IUserValidator<GymAccount>
    {
        public Task<IdentityResult> ValidateAsync(UserManager<GymAccount> manager, GymAccount user) =>
            Task.FromResult(user.Email == "blocked@example.com"
                ? IdentityResult.Failed(new IdentityError { Code = "BlockedEmail" })
                : IdentityResult.Success);
    }
}
