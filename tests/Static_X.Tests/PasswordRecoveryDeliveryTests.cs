using System.Net;
using BLL;
using BLL.Services.Interfaces;
using DAL;
using DAL.Repositories.Interfaces;
using DAL.Repositories.Models;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Static_X.Tests;

public sealed partial class AccountHttpTests
{
    [Theory]
    [InlineData("missing-frontend")]
    [InlineData("http")]
    [InlineData("userinfo")]
    [InlineData("query")]
    [InlineData("fragment")]
    [InlineData("missing-provider")]
    [InlineData("invalid-from")]
    [InlineData("missing-ring")]
    public async Task RecoveryDispatcher_UnsafeConfigurationBlocksBeforeLeasingOrSending(string scenario)
    {
        await using var app = await TestApp.CreateAsync(activation: true);
        await app.CreateGlobalAsync("member@example.invalid", "CorrectPassword1!");
        using var client = app.Client();
        using var queued = await app.PostAsync(client, "/api/account/forgot-password", new { email = "member@example.invalid" });
        Assert.Equal(HttpStatusCode.Accepted, queued.StatusCode);
        using var scope = app.Factory.Services.CreateScope();
        var configuration = RecoveryConfiguration(app);
        if (scenario == "missing-provider") configuration["Resend:ApiKey"] = "";
        else if (scenario == "invalid-from") configuration["Resend:FromAddress"] = "Sender <sender@example.invalid>";
        else if (scenario == "missing-ring") configuration["Activation:KeyRingPath"] = "";
        else configuration["PasswordRecovery:FrontendUrl"] = scenario switch
        {
            "http" => "http://frontend.example.invalid/reset",
            "userinfo" => "https://user@frontend.example.invalid/reset",
            "query" => "https://frontend.example.invalid/reset?unsafe=true",
            "fragment" => "https://frontend.example.invalid/reset#unsafe",
            _ => ""
        };
        var sender = new RecoverySender();
        var dispatcher = new PasswordRecoveryDispatcher(scope.ServiceProvider.GetRequiredService<IAccountRepository>(),
            scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>(), configuration, TimeProvider.System, sender);
        await Assert.ThrowsAsync<InvalidOperationException>(() => dispatcher.DispatchOneAsync());
        var row = await scope.ServiceProvider.GetRequiredService<StaticXDbContext>().PasswordRecoveryOutbox.SingleAsync();
        Assert.Null(row.LeaseOwner);
        Assert.Null(row.LeaseExpiresAt);
        Assert.Equal(0, row.AttemptCount);
        Assert.NotEmpty(row.ProtectedPayload);
        Assert.Equal(0, sender.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecoveryDispatcher_RetryAcrossScopesKeepsExactProviderKeyAndPayloadUntilAccepted(bool unknown)
    {
        var clock = new RecoveryClock();
        await using var app = await TestApp.CreateAsync(activation: true, clock: clock);
        await app.CreateGlobalAsync("member@example.invalid", "CorrectPassword1!");
        using var client = app.Client();
        using var queued = await app.PostAsync(client, "/api/account/forgot-password", new { email = "member@example.invalid" });
        Assert.Equal(HttpStatusCode.Accepted, queued.StatusCode);
        var sender = new RecoverySender { Accepted = false, UnknownAcceptance = unknown };
        Assert.True(await DispatchRecoveryAsync(app, sender));
        Guid id;
        string protectedPayload;
        using (var first = app.Factory.Services.CreateScope())
        {
            var row = await first.ServiceProvider.GetRequiredService<StaticXDbContext>().PasswordRecoveryOutbox.SingleAsync();
            id = row.Id; protectedPayload = row.ProtectedPayload;
            Assert.NotEmpty(protectedPayload);
            Assert.Null(row.SentAt);
            Assert.Null(row.LeaseOwner);
            Assert.Equal(1, row.AttemptCount);
            Assert.Equal(clock.GetUtcNow().UtcDateTime.AddMinutes(1), row.NextAttemptAt);
        }
        Assert.False(await DispatchRecoveryAsync(app, sender));
        Assert.Equal(1, sender.Calls);
        clock.Advance(TimeSpan.FromMinutes(1));
        sender.Accepted = true; sender.UnknownAcceptance = false;
        Assert.True(await DispatchRecoveryAsync(app, sender));
        Assert.Equal(2, sender.Calls);
        Assert.Equal("password-recovery/" + id.ToString("N"), sender.Attempts[0].Key);
        Assert.True(sender.Attempts[0] == sender.Attempts[1], "Retry must use the identical provider key and bearer URL.");
        using var final = app.Factory.Services.CreateScope();
        var sent = await final.ServiceProvider.GetRequiredService<StaticXDbContext>().PasswordRecoveryOutbox.SingleAsync();
        Assert.NotNull(sent.SentAt);
        Assert.Equal(string.Empty, sent.ProtectedPayload);
        Assert.Null(sent.NextAttemptAt);
        Assert.Equal(2, sent.AttemptCount);
        Assert.False(await DispatchRecoveryAsync(app, sender));
        // Clearing the delivery secret must not prevent redemption of the separately protected public envelope.
        using var reset = await app.PostAsync(client, "/api/account/reset-password", new { token = sender.Token, newPassword = "NewPassword2!" });
        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
    }

    [Fact]
    public async Task RecoveryRepository_LeaseOwnershipExpiryAndCompletionCannotReleaseAnotherWorker()
    {
        var clock = new RecoveryClock();
        await using var app = await TestApp.CreateAsync(activation: true, clock: clock);
        await app.CreateGlobalAsync("member@example.invalid", "CorrectPassword1!");
        using var client = app.Client();
        using var queued = await app.PostAsync(client, "/api/account/forgot-password", new { email = "member@example.invalid" });
        Assert.Equal(HttpStatusCode.Accepted, queued.StatusCode);
        using var first = app.Factory.Services.CreateScope();
        var firstRepo = first.ServiceProvider.GetRequiredService<IAccountRepository>();
        var lease1 = await firstRepo.LeaseDueAsync(clock.GetUtcNow(), default);
        Assert.NotNull(lease1);
        using var second = app.Factory.Services.CreateScope();
        var secondRepo = second.ServiceProvider.GetRequiredService<IAccountRepository>();
        Assert.Null(await secondRepo.LeaseDueAsync(clock.GetUtcNow(), default));
        clock.Advance(TimeSpan.FromMinutes(5));
        var lease2 = await secondRepo.LeaseDueAsync(clock.GetUtcNow(), default);
        Assert.NotNull(lease2);
        Assert.NotEqual(lease1.LeaseOwner, lease2.LeaseOwner);
        Assert.False(await firstRepo.IsCurrentAsync(lease1, clock.GetUtcNow(), default));
        await firstRepo.CompleteAsync(lease1, clock.GetUtcNow(), true, default);
        var row = await second.ServiceProvider.GetRequiredService<StaticXDbContext>().PasswordRecoveryOutbox.AsNoTracking().SingleAsync();
        Assert.Equal(lease2.LeaseOwner, row.LeaseOwner);
        Assert.Null(row.SentAt);
        Assert.NotEmpty(row.ProtectedPayload);
        Assert.True(await secondRepo.IsCurrentAsync(lease2, clock.GetUtcNow(), default));
        await secondRepo.CompleteAsync(lease2, clock.GetUtcNow(), true, default);
        row = await second.ServiceProvider.GetRequiredService<StaticXDbContext>().PasswordRecoveryOutbox.AsNoTracking().SingleAsync();
        Assert.NotNull(row.SentAt);
        Assert.Equal(string.Empty, row.ProtectedPayload);
    }

    [Fact]
    public async Task RecoveryDispatcher_CancellationLeavesDurableLeaseAndRetriesSamePayloadAfterExpiry()
    {
        var clock = new RecoveryClock();
        await using var app = await TestApp.CreateAsync(activation: true, clock: clock);
        await app.CreateGlobalAsync("member@example.invalid", "CorrectPassword1!");
        using var client = app.Client();
        using var queued = await app.PostAsync(client, "/api/account/forgot-password", new { email = "member@example.invalid" });
        Assert.Equal(HttpStatusCode.Accepted, queued.StatusCode);
        using var cancellation = new CancellationTokenSource();
        var sender = new RecoverySender { BeforeSend = cancellation.Cancel };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DispatchRecoveryAsync(app, sender, cancellation.Token));
        using (var check = app.Factory.Services.CreateScope())
        {
            var row = await check.ServiceProvider.GetRequiredService<StaticXDbContext>().PasswordRecoveryOutbox.SingleAsync();
            Assert.NotNull(row.LeaseOwner);
            Assert.Null(row.SentAt);
            Assert.NotEmpty(row.ProtectedPayload);
            Assert.Equal(0, row.AttemptCount);
        }
        sender.BeforeSend = null;
        Assert.False(await DispatchRecoveryAsync(app, sender));
        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.True(await DispatchRecoveryAsync(app, sender));
        Assert.Equal(2, sender.Calls);
        Assert.True(sender.Attempts[0] == sender.Attempts[1], "Cancelled/unknown attempts must retain the same provider key and payload.");
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("password-changed")]
    [InlineData("unconfirmed")]
    public async Task RecoveryRepository_ExpiredOrStalePendingRequestsEraseSecretsAndNeverSend(string scenario)
    {
        var clock = new RecoveryClock();
        await using var app = await TestApp.CreateAsync(activation: true, clock: clock);
        var id = await app.CreateGlobalAsync("member@example.invalid", "CorrectPassword1!");
        using var client = app.Client();
        using var queued = await app.PostAsync(client, "/api/account/forgot-password", new { email = "member@example.invalid" });
        Assert.Equal(HttpStatusCode.Accepted, queued.StatusCode);
        using (var scope = app.Factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<GymAccount>>();
            var user = (await users.FindByIdAsync(id))!;
            if (scenario == "expired") clock.Advance(TimeSpan.FromMinutes(30));
            else if (scenario == "password-changed") Assert.True((await users.ChangePasswordAsync(user, "CorrectPassword1!", "ChangedPassword3!")).Succeeded);
            else { user.EmailConfirmed = false; Assert.True((await users.UpdateAsync(user)).Succeeded); }
        }
        var sender = new RecoverySender();
        Assert.False(await DispatchRecoveryAsync(app, sender));
        Assert.Equal(0, sender.Calls);
        using var check = app.Factory.Services.CreateScope();
        var row = await check.ServiceProvider.GetRequiredService<StaticXDbContext>().PasswordRecoveryOutbox.AsNoTracking().SingleAsync();
        Assert.Equal(string.Empty, row.ProtectedPayload);
        Assert.Null(row.NextAttemptAt);
        Assert.False(await DispatchRecoveryAsync(app, sender));
    }

    [Fact]
    public async Task RecoveryRepository_ResetAfterLeasePreventsSendAndCompletionCannotResurrectSecret()
    {
        await using var app = await TestApp.CreateAsync(activation: true);
        await app.CreateGlobalAsync("member@example.invalid", "CorrectPassword1!");
        using var client = app.Client();
        var token = await RecoveryLinkAsync(app, client, "member@example.invalid", null);
        // A second request can be leased while the first already-sent token is redeemed.
        using var queued = await app.PostAsync(client, "/api/account/forgot-password", new { email = "member@example.invalid" });
        Assert.Equal(HttpStatusCode.Accepted, queued.StatusCode);
        using var delivery = app.Factory.Services.CreateScope();
        var repository = delivery.ServiceProvider.GetRequiredService<IAccountRepository>();
        var lease = await repository.LeaseDueAsync(DateTimeOffset.UtcNow, default);
        Assert.NotNull(lease);
        using var reset = await app.PostAsync(client, "/api/account/reset-password", new { token, newPassword = "NewPassword2!" });
        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
        Assert.False(await repository.IsCurrentAsync(lease, DateTimeOffset.UtcNow, default));
        await repository.CompleteAsync(lease, DateTimeOffset.UtcNow, false, default);
        var rows = await delivery.ServiceProvider.GetRequiredService<StaticXDbContext>().PasswordRecoveryOutbox.AsNoTracking().ToListAsync();
        Assert.All(rows, row => { Assert.Empty(row.ProtectedPayload); Assert.Null(row.NextAttemptAt); Assert.Null(row.LeaseOwner); });
    }

    [Fact]
    public async Task RecoveryRepository_IdentityWriteFailureRollsBackClaimAndCredentials()
    {
        await using var app = await TestApp.CreateAsync(activation: true);
        await app.CreateGlobalAsync("member@example.invalid", "CorrectPassword1!");
        using var client = app.Client();
        var token = await RecoveryLinkAsync(app, client, "member@example.invalid", null);
        var before = await app.LogoutStateAsync();
        using (var setup = app.Factory.Services.CreateScope())
            await setup.ServiceProvider.GetRequiredService<StaticXDbContext>().Database.ExecuteSqlRawAsync(
                "CREATE TRIGGER RecoveryTestRejectUpdate BEFORE UPDATE ON AspNetUsers BEGIN SELECT RAISE(ABORT, 'test write rejected'); END;");
        using var failed = await app.PostAsync(client, "/api/account/reset-password", new { token, newPassword = "NewPassword2!" });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        Assert.True(before == await app.LogoutStateAsync(), "Failed Identity writes must roll back credentials and request claim.");
        using (var check = app.Factory.Services.CreateScope())
        {
            var db = check.ServiceProvider.GetRequiredService<StaticXDbContext>();
            Assert.Null((await db.PasswordRecoveryOutbox.SingleAsync()).ConsumedAt);
            await db.Database.ExecuteSqlRawAsync("DROP TRIGGER RecoveryTestRejectUpdate;");
        }
        using var retry = await app.PostAsync(client, "/api/account/reset-password", new { token, newPassword = "NewPassword2!" });
        Assert.Equal(HttpStatusCode.NoContent, retry.StatusCode);
    }

    [Fact]
    public void RecoveryProvider_IsDedicatedThirtyMinutesAndDoesNotChangeOtherProviders()
    {
        using var factory = new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder
            .UseSetting("ConnectionStrings:Static-X", "Server=unused;Database=unused;Integrated Security=true")
            .UseSetting("Activation:KeyRingPath", ""));
        var services = factory.Services;
        Assert.Equal(TimeSpan.FromMinutes(30), services.GetRequiredService<IOptions<PasswordResetTokenProviderOptions>>().Value.TokenLifespan);
        Assert.Equal(TimeSpan.FromDays(1), services.GetRequiredService<IOptions<DataProtectionTokenProviderOptions>>().Value.TokenLifespan);
        var identity = services.GetRequiredService<IOptions<IdentityOptions>>().Value;
        Assert.Equal(PasswordResetTokenProvider.ProviderName, identity.Tokens.PasswordResetTokenProvider);
        Assert.Equal(typeof(PasswordResetTokenProvider), identity.Tokens.ProviderMap[PasswordResetTokenProvider.ProviderName].ProviderType);
        Assert.Single(identity.Tokens.ProviderMap);
    }

    [Fact]
    public void RecoveryMigration_OnlyAddsOutboxWithAccountScopeAndOfflineSqlWithoutSeedsOrCredentialChanges()
    {
        using var db = new StaticXDbContext(new DbContextOptionsBuilder<StaticXDbContext>()
            .UseSqlServer("Server=unused.invalid;Database=offline;Integrated Security=true").Options);
        var assembly = db.GetService<IMigrationsAssembly>();
        const string id = "20261109221729_AddPasswordRecoveryOutbox";
        var migration = assembly.CreateMigration(assembly.Migrations[id], db.Database.ProviderName!);
        var table = Assert.Single(migration.UpOperations.OfType<CreateTableOperation>());
        Assert.Equal("PasswordRecoveryOutbox", table.Name);
        Assert.Equal(4, migration.UpOperations.Count);
        Assert.Equal("PasswordRecoveryOutbox", Assert.IsType<DropTableOperation>(Assert.Single(migration.DownOperations)).Name);
        Assert.Contains(table.Columns, c => c.Name == "GymId" && c.IsNullable);
        Assert.Contains(table.Columns, c => c.Name == "StampBinding" && c.MaxLength == 64);
        Assert.DoesNotContain(table.Columns, c => c.Name.Contains("Password") || c.Name == "Token");
        Assert.Equal(new[] { "AspNetUsers", "Gimnasios" }, table.ForeignKeys.Select(f => f.PrincipalTable).Order());
        Assert.False(db.Database.HasPendingModelChanges());
        var sql = db.GetService<IMigrator>().GenerateScript("20261028230341_AddGlobalSuperAdmin", id);
        Assert.Contains("CREATE TABLE [PasswordRecoveryOutbox]", sql);
        Assert.DoesNotContain("ALTER TABLE [AspNetUsers]", sql);
        Assert.DoesNotContain("UPDATE [AspNetUsers]", sql);
        Assert.DoesNotContain("DELETE FROM", sql);
    }
}
