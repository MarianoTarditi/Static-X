using BLL;
using BLL.DTOs.Activations;
using BLL.Interfaces;
using BLL.Services.Interfaces;
using DAL.Repositories.Interfaces;
using DAL.Repositories.Implementation;
using DAL;
using Entity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Static_X.API.Integrations;
using Microsoft.Extensions.Hosting;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;

namespace Static_X.Tests;

public sealed class ActivationTests
{
    [Theory]
    [InlineData("null")]
    [InlineData("key-null")]
    [InlineData("key-blank")]
    [InlineData("key-long")]
    [InlineData("name-null")]
    [InlineData("name-blank")]
    [InlineData("name-long")]
    [InlineData("email-null")]
    [InlineData("email-blank")]
    [InlineData("email-long")]
    [InlineData("email-format")]
    [InlineData("email-display-name")]
    public async Task Provision_InvalidDto_RejectsBeforePersistence(string scenario)
    {
        await using var fixture = await Fixture.CreateAsync();
        var request = scenario == "null" ? null : new ProvisionGymRequest(
            scenario switch { "key-null" => null!, "key-blank" => " ", "key-long" => new string('k', 129), _ => "request-a" },
            scenario switch { "name-null" => null!, "name-blank" => " ", "name-long" => new string('n', 201), _ => "North" },
             scenario switch { "email-null" => null!, "email-blank" => " ", "email-long" => new string('e', 257), "email-format" => "invalid", "email-display-name" => "Admin <admin@example.com>", _ => "admin@example.com" });
        var error = await Record.ExceptionAsync(() => fixture.Service.ProvisionAsync(request!));
        if (scenario == "null") Assert.IsType<ArgumentNullException>(error);
        else Assert.IsType<ArgumentException>(error);
        Assert.Equal(0, await fixture.Context.Gimnasios.CountAsync());
        Assert.Equal(0, await fixture.Context.Users.CountAsync());
        Assert.Equal(0, await fixture.Context.ProvisioningRequests.CountAsync());
        Assert.Equal(0, await fixture.Context.ActivationInvitations.CountAsync());
        Assert.Equal(0, await fixture.Context.ActivationOutbox.CountAsync());
    }

    [Theory]
    [InlineData("null")]
    [InlineData("token-null")]
    [InlineData("token-blank")]
    [InlineData("token-short")]
    [InlineData("token-long")]
    [InlineData("token-nonhex")]
    [InlineData("password-null")]
    [InlineData("password-blank")]
    public async Task Activate_InvalidDto_ReturnsNullWithoutConsumingInvitation(string scenario)
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Service.ProvisionAsync(new ProvisionGymRequest("request-a", "North", "admin@example.com"));
        var token = await fixture.GetTokenAsync();
        var request = scenario == "null" ? null : new ActivateAccountRequest
        {
            Token = scenario switch
            {
                "token-null" => null!, "token-blank" => " ", "token-short" => token[..63],
                "token-long" => token + "A", "token-nonhex" => "Z" + token[1..], _ => token
            },
            Password = scenario == "password-null" ? null! : scenario == "password-blank" ? " " : "StrongPassword1!"
        };
        Assert.Null(await fixture.Service.ActivateAsync(request!));
        Assert.Null((await fixture.Context.ActivationInvitations.AsNoTracking().SingleAsync()).ConsumedAt);
        Assert.Null((await fixture.Context.Users.AsNoTracking().SingleAsync()).PasswordHash);
        Assert.False((await fixture.Context.Users.AsNoTracking().SingleAsync()).EmailConfirmed);
        Assert.NotEmpty((await fixture.Context.ActivationOutbox.AsNoTracking().SingleAsync()).ProtectedToken);
    }

    [Fact]
    public async Task Provision_IdentityFailure_RollsBackGymAndAllDeliveryRows()
    {
        await using var fixture = await Fixture.CreateAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.ProvisionAsync(new ProvisionGymRequest("request-a", "North", "blocked@example.com")));
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
        var id = await fixture.Service.ProvisionAsync(new ProvisionGymRequest("request-a", "North", "admin@example.com"));
        Assert.Equal(id, await fixture.Service.ProvisionAsync(new ProvisionGymRequest("request-a", "North", "admin@example.com")));
        Assert.Equal(1, await fixture.Context.Gimnasios.CountAsync());
        Assert.Equal(1, await fixture.Context.Users.CountAsync());
        Assert.Equal(1, await fixture.Context.ActivationOutbox.CountAsync());
        Assert.Equal(1, await fixture.Context.ActivationInvitations.CountAsync());
        var user = await fixture.Context.Users.SingleAsync();
        Assert.Null(user.PasswordHash);
        Assert.False(user.EmailConfirmed);
        await Assert.ThrowsAsync<BLL.Exceptions.ProvisioningConflictException>(() =>
             fixture.Service.ProvisionAsync(new ProvisionGymRequest("request-a", "South", "admin@example.com")));
        Assert.Equal(1, await fixture.Context.Gimnasios.CountAsync());
    }

    [Fact]
    public async Task Provision_WithoutConfiguredKeyRing_DoesNotWriteAnyRows()
    {
        await using var fixture = await Fixture.CreateAsync(configureKeyRing: false);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.ProvisionAsync(new ProvisionGymRequest("request-a", "North", "admin@example.com")));
        Assert.Equal(0, await fixture.Context.Gimnasios.CountAsync());
        Assert.Equal(0, await fixture.Context.ActivationOutbox.CountAsync());
    }

    [Fact]
    public async Task Activate_WeakPassword_RollsBackClaimAndAllowsValidRetry()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Service.ProvisionAsync(new ProvisionGymRequest("request-a", "North", "admin@example.com"));
        var token = await fixture.GetTokenAsync();
        Assert.Null(await fixture.Service.ActivateAsync(new ActivateAccountRequest { Token = token, Password = "weak" }));
        Assert.Null((await fixture.Context.ActivationInvitations.AsNoTracking().SingleAsync()).ConsumedAt);
        Assert.Null((await fixture.Context.Users.AsNoTracking().SingleAsync()).PasswordHash);
        var response = await fixture.Service.ActivateAsync(new ActivateAccountRequest { Token = token, Password = "StrongPassword1!" });
        Assert.Equal((await fixture.Context.Users.AsNoTracking().SingleAsync()).GymId,
            Assert.IsType<ActivationResponse>(response).GymId);
    }

    [Fact]
    public async Task Activate_ValidToken_SetsPasswordAndEmailOnceAndErasesDeliverySecret()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Service.ProvisionAsync(new ProvisionGymRequest("request-a", "North", "admin@example.com"));
        var token = await fixture.GetTokenAsync();
        var response = await fixture.Service.ActivateAsync(new ActivateAccountRequest { Token = token, Password = "StrongPassword1!" });
        Assert.Null(await fixture.Service.ActivateAsync(new ActivateAccountRequest { Token = token, Password = "OtherPassword1!" }));
        using var persistedScope = fixture.CreateScope();
        var persisted = await persistedScope.ServiceProvider.GetRequiredService<StaticXDbContext>().Users.AsNoTracking().SingleAsync();
        Assert.True(Assert.IsType<ActivationResponse>(response).GymId > 0);
        Assert.Equal(persisted.GymId, response.GymId);
        Assert.True(persisted.EmailConfirmed);
        Assert.True(await fixture.Users.CheckPasswordAsync(persisted, "StrongPassword1!"));
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
        await fixture.Service.ProvisionAsync(new ProvisionGymRequest("request-a", "North", "admin@example.com"));
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
        Assert.Null(await fixture.Service.ActivateAsync(new ActivateAccountRequest { Token = token, Password = "StrongPassword1!" }));
        Assert.Null(invitation.ConsumedAt);
        Assert.Null((await fixture.Users.FindByIdAsync(trainer.Id))?.PasswordHash);
        invitation.AccountId = originalAccount;
        invitation.ExpiresAt = DateTime.UtcNow.AddSeconds(-1);
        await fixture.Context.SaveChangesAsync();
        Assert.Null(await fixture.Service.ActivateAsync(new ActivateAccountRequest { Token = token, Password = "StrongPassword1!" }));
        Assert.Null((await fixture.Users.FindByIdAsync(originalAccount))?.PasswordHash);
        Assert.Null(invitation.ConsumedAt);
    }

    [Fact]
    public async Task Activate_RepositorySuccess_ReturnsCommittedAccountEntity()
    {
        await using var fixture = await Fixture.CreateAsync();
        var gym = await fixture.Service.ProvisionAsync(new ProvisionGymRequest("repository", "North", "admin@example.com"));
        var token = await fixture.GetTokenAsync();
        using var scope = fixture.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IActivationRepository>();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(token)));

        var result = Assert.IsType<Account>(await repository.ActivateAsync(hash, "StrongPassword1!", DateTimeOffset.UtcNow, default));

        using var verification = fixture.CreateScope();
        var persisted = await verification.ServiceProvider.GetRequiredService<StaticXDbContext>().Users.AsNoTracking().SingleAsync();
        Assert.Equal((persisted.Id, gym, "admin@example.com", AccountRole.GymAdmin),
            (result.Id, result.GymId, result.Email, result.Role));
        Assert.True(persisted.EmailConfirmed);
        Assert.NotNull((await verification.ServiceProvider.GetRequiredService<StaticXDbContext>()
            .ActivationInvitations.AsNoTracking().SingleAsync()).ConsumedAt);
    }

    [Fact]
    public async Task Activate_CommitFailure_ReturnsNoSuccessAndRollsBackActivation()
    {
        var interceptor = new FailingCommitInterceptor();
        await using var fixture = await Fixture.CreateAsync(commitInterceptor: interceptor);
        await fixture.Service.ProvisionAsync(new ProvisionGymRequest("commit-failure", "North", "admin@example.com"));
        var token = await fixture.GetTokenAsync();
        interceptor.Fail = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.ActivateAsync(
            new ActivateAccountRequest { Token = token, Password = "StrongPassword1!" }));

        using var verification = fixture.CreateScope();
        var context = verification.ServiceProvider.GetRequiredService<StaticXDbContext>();
        var account = await context.Users.AsNoTracking().SingleAsync();
        Assert.False(account.EmailConfirmed);
        Assert.Null(account.PasswordHash);
        Assert.Null((await context.ActivationInvitations.AsNoTracking().SingleAsync()).ConsumedAt);
        Assert.NotEmpty((await context.ActivationOutbox.AsNoTracking().SingleAsync()).ProtectedToken);
    }

    private sealed class FailingCommitInterceptor : DbTransactionInterceptor
    {
        public bool Fail { get; set; }

        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (Fail) throw new InvalidOperationException("Simulated activation commit failure.");
            return ValueTask.FromResult(result);
        }
    }

    [Fact]
    public async Task Activate_ConcurrentClaims_CannotBothSetPassword()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Service.ProvisionAsync(new ProvisionGymRequest("request-a", "North", "admin@example.com"));
        var token = await fixture.GetTokenAsync();
        using var first = fixture.CreateScope();
        using var second = fixture.CreateScope();
        var firstService = first.ServiceProvider.GetRequiredService<IActivationService>();
        var secondService = second.ServiceProvider.GetRequiredService<IActivationService>();

        // SQLite can reject one simultaneous writer with SQLITE_LOCKED; it must not activate twice.
        async Task<ActivationResponse?> Attempt(IActivationService service, string password)
        {
            try { return await service.ActivateAsync(new ActivateAccountRequest { Token = token, Password = password }); }
            catch (SqliteException ex) when (ex.SqliteErrorCode is 5 or 6) { return null; }
        }

        var attempts = await Task.WhenAll(Attempt(firstService, "FirstPassword1!"),
            Attempt(secondService, "SecondPassword1!"));
        var winner = Assert.Single(attempts.OfType<ActivationResponse>());
        var user = await fixture.Context.Users.AsNoTracking().SingleAsync();
        Assert.Equal(user.GymId, winner.GymId);
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
        var gymId = await fixture.Service.ProvisionAsync(new ProvisionGymRequest("request-a", "North", "admin@example.com"));
        var oldToken = await fixture.GetTokenAsync();
        var accountId = (await fixture.Context.Users.SingleAsync()).Id;
        Assert.False(await fixture.Service.ReissueAsync(gymId + 1, accountId));
        Assert.True(await fixture.Service.ReissueAsync(gymId, accountId));
        Assert.Equal(gymId, await fixture.Service.ProvisionAsync(new ProvisionGymRequest("request-a", "North", "admin@example.com")));
        Assert.Equal(2, await fixture.Context.ActivationInvitations.CountAsync());
        var old = await fixture.Context.ActivationInvitations.SingleAsync(i => i.RevokedAt != null);
        Assert.NotNull(old.RevokedAt);
        Assert.Equal(string.Empty, (await fixture.Context.ActivationOutbox.AsNoTracking().SingleAsync(o => o.InvitationId == old.Id)).ProtectedToken);
        Assert.Null(await fixture.Service.ActivateAsync(new ActivateAccountRequest { Token = oldToken, Password = "StrongPassword1!" }));
        var current = await fixture.Context.ActivationInvitations.SingleAsync(i => i.RevokedAt == null);
        var payload = await fixture.Context.ActivationOutbox.SingleAsync(o => o.InvitationId == current.Id);
        var token = fixture.Unprotect(current.Id, payload.ProtectedToken);
        Assert.Equal(gymId, Assert.IsType<ActivationResponse>(await fixture.Service.ActivateAsync(
            new ActivateAccountRequest { Token = token, Password = "StrongPassword1!" })).GymId);
        Assert.False(await fixture.Service.ReissueAsync(gymId, accountId));
    }

    [Fact]
    public async Task Reissue_ExpiredInvitation_RecoversWithoutChangingProvisioningKey()
    {
        await using var fixture = await Fixture.CreateAsync();
        var gymId = await fixture.Service.ProvisionAsync(new ProvisionGymRequest("request-a", "North", "admin@example.com"));
        var previous = await fixture.Context.ActivationInvitations.SingleAsync();
        var accountId = previous.AccountId;
        previous.ExpiresAt = DateTime.UtcNow.AddMinutes(-1);
        await fixture.Context.SaveChangesAsync();
        Assert.True(await fixture.Service.ReissueAsync(gymId, accountId));
        Assert.Equal(gymId, await fixture.Service.ProvisionAsync(new ProvisionGymRequest("request-a", "North", "admin@example.com")));
        Assert.Equal(2, await fixture.Context.ActivationInvitations.CountAsync());
        Assert.Equal(1, await fixture.Context.ActivationInvitations.CountAsync(i => i.RevokedAt == null));
    }

    [Fact]
    public async Task Dispatch_FailedCompletionAfterReissue_DoesNotRestoreRevokedSecret()
    {
        await using var fixture = await Fixture.CreateAsync();
        var gymId = await fixture.Service.ProvisionAsync(new ProvisionGymRequest("request-a", "North", "admin@example.com"));
        var accountId = (await fixture.Context.Users.AsNoTracking().SingleAsync()).Id;
        using var workerScope = fixture.CreateScope();
        var worker = workerScope.ServiceProvider.GetRequiredService<IActivationDeliveryRepository>();
        var leased = await worker.LeaseDueAsync(DateTimeOffset.UtcNow, default);
        Assert.NotNull(leased);

        Assert.True(await fixture.Service.ReissueAsync(gymId, accountId));
        var oldJob = await fixture.Context.ActivationOutbox.AsNoTracking()
            .SingleAsync(o => o.Id == leased!.JobId);
        Assert.Equal(string.Empty, oldJob.ProtectedToken);
        Assert.Null(oldJob.NextAttemptAt);

        var now = DateTimeOffset.UtcNow;
        await worker.CompleteAsync(leased!, now, false, default);
        oldJob = await fixture.Context.ActivationOutbox.AsNoTracking().SingleAsync(o => o.Id == leased!.JobId);
        Assert.Equal(string.Empty, oldJob.ProtectedToken);
        Assert.Null(oldJob.NextAttemptAt);
        Assert.Null(oldJob.LeaseOwner);

        var next = await worker.LeaseDueAsync(now.AddSeconds(1), default);
        Assert.NotNull(next);
        Assert.NotEqual(leased.JobId, next!.JobId);
        Assert.NotEqual(leased.InvitationId, next.InvitationId);
        Assert.NotEqual(string.Empty, next.ProtectedToken);
    }

    [Fact]
    public async Task Dispatch_CompetingLeasesAndUnknownAcceptance_ReuseJobKey()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Service.ProvisionAsync(new ProvisionGymRequest("request-a", "North", "admin@example.com"));
        using var second = fixture.CreateScope();
        var repository1 = fixture.CreateScope();
        using (repository1)
        {
            var first = repository1.ServiceProvider.GetRequiredService<IActivationDeliveryRepository>();
            var other = second.ServiceProvider.GetRequiredService<IActivationDeliveryRepository>();
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
        await fixture.Service.ProvisionAsync(new ProvisionGymRequest("request-a", "North", "admin@example.com"));
        var invitation = await fixture.Context.ActivationInvitations.SingleAsync();
        invitation.ExpiresAt = DateTime.UtcNow.AddMinutes(-1);
        await fixture.Context.SaveChangesAsync();
        Assert.Null(await fixture.DeliveryRepository.LeaseDueAsync(DateTimeOffset.UtcNow, default));
        Assert.Equal(string.Empty, (await fixture.Context.ActivationOutbox.AsNoTracking().SingleAsync()).ProtectedToken);
    }

    [Fact]
    public async Task Dispatch_MissingConfiguration_DoesNotLeaseOrSend()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Service.ProvisionAsync(new ProvisionGymRequest("request-a", "North", "admin@example.com"));
        var sender = new FakeSender();
        var dispatcher = new ActivationDispatcher(fixture.DeliveryRepository, fixture.Protection,
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
        await fixture.Service.ProvisionAsync(new ProvisionGymRequest("request-a", "North", "admin@example.com"));
        fixture.Configuration["Resend:ApiKey"] = "re_test_only";
        fixture.Configuration["Resend:FromAddress"] = "sender@example.com";
        fixture.Configuration["Activation:FrontendUrl"] = "https://app.example.com/activate";
        var sender = new FakeSender();
        var dispatcher = new ActivationDispatcher(fixture.DeliveryRepository, fixture.Protection,
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
        await fixture.Service.ProvisionAsync(new ProvisionGymRequest("request-a", "North", "admin@example.com"));
        fixture.Configuration["Resend:ApiKey"] = "re_test_only";
        fixture.Configuration["Resend:FromAddress"] = "sender@example.com";
        fixture.Configuration["Activation:FrontendUrl"] = "https://app.example.com/activate";
        var clock = new MutableClock { Now = DateTimeOffset.UtcNow };
        var sender = new FakeSender { Accept = false };
        var dispatcher = new ActivationDispatcher(fixture.DeliveryRepository, fixture.Protection,
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
        await fixture.Service.ProvisionAsync(new ProvisionGymRequest("request-a", "North", "admin@example.com"));
        var now = DateTimeOffset.UtcNow;
        var first = await fixture.DeliveryRepository.LeaseDueAsync(now, default);
        Assert.NotNull(first);
        using var otherScope = fixture.CreateScope();
        var other = otherScope.ServiceProvider.GetRequiredService<IActivationDeliveryRepository>();
        var reclaimed = await other.LeaseDueAsync(now.AddMinutes(6), default);
        Assert.NotNull(reclaimed);
        Assert.Equal(first!.JobId, reclaimed!.JobId);
        Assert.NotEqual(first.LeaseOwner, reclaimed.LeaseOwner);
        Assert.False(await fixture.DeliveryRepository.IsCurrentAsync(first, now.AddMinutes(6), default));
        await fixture.DeliveryRepository.CompleteAsync(first, now.AddMinutes(6), true, default);
        Assert.Null((await fixture.Context.ActivationOutbox.AsNoTracking().SingleAsync()).SentAt);
    }

    private sealed class MutableClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; }
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Theory]
    [InlineData("before")]
    [InlineData("at")]
    [InlineData("after")]
    public async Task Activate_ExpiryBoundary_OnlyStrictlyEarlierTokenCanBeConsumed(string boundary)
    {
        var clock = new MutableClock { Now = DateTimeOffset.UtcNow };
        await using var fixture = await Fixture.CreateAsync(clock: clock);
        await fixture.Service.ProvisionAsync(new ProvisionGymRequest("expiry", "Expiry", "admin@example.com"));
        var token = await fixture.GetTokenAsync();
        var expiry = (await fixture.Context.ActivationInvitations.AsNoTracking().SingleAsync()).ExpiresAt;
        clock.Now = new DateTimeOffset(DateTime.SpecifyKind(expiry, DateTimeKind.Utc)).AddTicks(boundary == "before" ? -1 : boundary == "after" ? 1 : 0);
        var activated = await fixture.Service.ActivateAsync(new ActivateAccountRequest { Token = token, Password = "StrongPassword1!" });
        Assert.Equal(boundary == "before", activated is not null);
        var account = await fixture.Context.Users.AsNoTracking().SingleAsync();
        if (boundary == "before") Assert.Equal(account.GymId, Assert.IsType<ActivationResponse>(activated).GymId);
        Assert.Equal(boundary == "before", account.EmailConfirmed);
        Assert.Equal(boundary == "before", (await fixture.Context.ActivationInvitations.AsNoTracking().SingleAsync()).ConsumedAt != null);
    }

    [Theory]
    [InlineData("gym-zero")]
    [InlineData("gym-negative")]
    [InlineData("account-null")]
    [InlineData("account-blank")]
    [InlineData("account-too-long")]
    public async Task Reissue_InvalidTarget_RejectsWithoutPersistence(string scenario)
    {
        await using var fixture = await Fixture.CreateAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.ReissueAsync(
            scenario == "gym-zero" ? 0 : scenario == "gym-negative" ? -1 : 1,
            scenario == "account-null" ? null! : scenario == "account-blank" ? " " : scenario == "account-too-long" ? new string('a', 451) : "unknown"));
        Assert.Equal(0, await fixture.Context.ActivationInvitations.CountAsync());
        Assert.Equal(0, await fixture.Context.ActivationOutbox.CountAsync());
    }

    [Fact]
    public async Task Reissue_NonAdministratorOrPasswordBearingAccount_DoesNotReplaceInvitation()
    {
        await using var fixture = await Fixture.CreateAsync();
        var gym = await fixture.Service.ProvisionAsync(new ProvisionGymRequest("pending", "Pending", "admin@example.com"));
        var user = await fixture.Context.Users.SingleAsync();
        user.Role = AccountRole.Trainer;
        Assert.True((await fixture.Users.UpdateAsync(user)).Succeeded);
        Assert.False(await fixture.Service.ReissueAsync(gym, user.Id));
        user.Role = AccountRole.GymAdmin;
        Assert.True((await fixture.Users.AddPasswordAsync(user, "StrongPassword1!")).Succeeded);
        Assert.False(await fixture.Service.ReissueAsync(gym, user.Id));
        Assert.Equal(1, await fixture.Context.ActivationInvitations.CountAsync());
        Assert.Equal(0, await fixture.Context.ActivationInvitations.CountAsync(i => i.RevokedAt != null));
        Assert.Equal(1, await fixture.Context.ActivationOutbox.CountAsync());
    }

    [Fact]
    public async Task Worker_RealPendingOutbox_AutomaticallyDispatchesWithFakeProviderAndErasesSecret()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Configuration["Resend:ApiKey"] = "re_test_only";
        fixture.Configuration["Resend:FromAddress"] = "sender@example.com";
        fixture.Configuration["Activation:FrontendUrl"] = "https://app.example.com/activate";
        await fixture.Service.ProvisionAsync(new ProvisionGymRequest("automatic", "Automatic", "admin@example.com"));
        using var scope = fixture.CreateScope();
        var clock = new IterationClock();
        using var worker = new Static_X.API.Integrations.ActivationOutboxWorker(
            scope.ServiceProvider.GetRequiredService<IServiceScopeFactory>(), clock,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Static_X.API.Integrations.ActivationOutboxWorker>.Instance);
        await worker.StartAsync(default);
        Assert.Equal(TimeSpan.FromSeconds(1), await clock.Delayed.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        await worker.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
        var job = await fixture.Context.ActivationOutbox.AsNoTracking().SingleAsync();
        Assert.NotNull(job.SentAt);
        Assert.Empty(job.ProtectedToken);
        Assert.Equal(1, job.AttemptCount);
        var sender = scope.ServiceProvider.GetRequiredService<FakeSender>();
        Assert.Equal(1, sender.Calls);
        Assert.Equal("admin@example.com", sender.To);
        Assert.Equal("activation/" + job.Id.ToString("N"), sender.Key);
    }

    private sealed class IterationClock : TimeProvider
    {
        public TaskCompletionSource<TimeSpan> Delayed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = base.CreateTimer(callback, state, Timeout.InfiniteTimeSpan, period);
            Delayed.TrySetResult(dueTime);
            return timer;
        }
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
        public StaticXDbContext Context { get; }
        public UserManager<GymAccount> Users { get; }
        public IActivationService Service { get; }
        public IActivationDeliveryRepository DeliveryRepository => scope.ServiceProvider.GetRequiredService<IActivationDeliveryRepository>();
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
            Context = scope.ServiceProvider.GetRequiredService<StaticXDbContext>();
            Users = scope.ServiceProvider.GetRequiredService<UserManager<GymAccount>>();
            Service = scope.ServiceProvider.GetRequiredService<IActivationService>();
            protection = scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>();
        }

        public static async Task<Fixture> CreateAsync(bool configureKeyRing = true, TimeProvider? clock = null,
            DbTransactionInterceptor? commitInterceptor = null)
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
            services.AddSingleton(clock ?? TimeProvider.System);
            services.AddDataProtection().SetApplicationName("Static-X.Activation")
                .PersistKeysToFileSystem(new DirectoryInfo(path));
            services.AddDbContext<StaticXDbContext>(o =>
            {
                o.UseSqlite(connectionString);
                if (commitInterceptor is not null) o.AddInterceptors(commitInterceptor);
            });
            services.AddIdentityCore<GymAccount>(o => o.User.RequireUniqueEmail = false)
                .AddEntityFrameworkStores<StaticXDbContext>();
            services.AddScoped<IUserValidator<GymAccount>, RejectBlockedEmail>();
            services.AddScoped<IActivationRepository, ActivationRepository>();
            services.AddScoped<IActivationService, ActivationService>();
            services.AddScoped<IActivationDeliveryRepository, ActivationDeliveryRepository>();
            services.AddScoped<IActivationDispatcher, ActivationDispatcher>();
            services.AddSingleton<FakeSender>();
            services.AddSingleton<IActivationEmailSender>(provider => provider.GetRequiredService<FakeSender>());
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
