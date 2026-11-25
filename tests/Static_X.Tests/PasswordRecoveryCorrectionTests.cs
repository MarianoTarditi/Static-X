using System.Net;
using System.Threading.Channels;
using BLL;
using DAL;
using DAL.Repositories.Interfaces;
using DAL.Repositories.Models;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Static_X.API.Integrations;
using Xunit;

namespace Static_X.Tests;

public sealed partial class AccountHttpTests
{
    [Theory]
    [InlineData("Activation:KeyRingPath")]
    [InlineData("Resend:FromAddress")]
    [InlineData("Resend:ApiKey")]
    [InlineData("PasswordRecovery:FrontendUrl")]
    public async Task RecoveryWorker_MissingDeliveryConfigurationStillErasesExpiredAndStaleSecretsWithoutLeasing(string missing)
    {
        var sender = new RecoverySender();
        await using var app = await TestApp.CreateAsync(activation: true, recoverySender: sender);
        var expired = await app.CreateGlobalAsync("expired@example.invalid", "CorrectPassword1!");
        var stale = await app.CreateGlobalAsync("stale@example.invalid", "CorrectPassword1!");
        var valid = await app.CreateGlobalAsync("valid@example.invalid", "CorrectPassword1!");
        using var client = app.Client();
        foreach (var email in new[] { "expired@example.invalid", "stale@example.invalid", "valid@example.invalid" })
        {
            using var response = await app.PostAsync(client, "/api/account/forgot-password", new { email });
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        }
        string validPayload;
        using (var setup = app.Factory.Services.CreateScope())
        {
            var db = setup.ServiceProvider.GetRequiredService<StaticXDbContext>();
            await db.PasswordRecoveryOutbox.Where(o => o.AccountId == expired).ExecuteUpdateAsync(s => s.SetProperty(o => o.ExpiresAt, DateTime.UtcNow.AddMinutes(-1)));
            await db.Users.Where(u => u.Id == stale).ExecuteUpdateAsync(s => s.SetProperty(u => u.SecurityStamp, Guid.NewGuid().ToString()));
            validPayload = await db.PasswordRecoveryOutbox.Where(o => o.AccountId == valid).Select(o => o.ProtectedPayload).SingleAsync();
        }
        // These are isolated in-process fixture settings; no deployed configuration or transport is used.
        var configuration = app.Factory.Services.GetRequiredService<IConfiguration>();
        configuration[missing] = "";
        foreach (var pair in new Dictionary<string, string>
        {
            ["Resend:ApiKey"] = "re_fake_never_sent", ["Resend:FromAddress"] = "sender@example.invalid",
            ["PasswordRecovery:FrontendUrl"] = "https://frontend.example.invalid/reset"
        })
            if (pair.Key != missing) configuration[pair.Key] = pair.Value;
        var clock = new RecoveryIterationClock();
        using var worker = new PasswordRecoveryOutboxWorker(app.Factory.Services.GetRequiredService<IServiceScopeFactory>(), clock,
            NullLogger<PasswordRecoveryOutboxWorker>.Instance);
        await worker.StartAsync(default);
        Assert.Equal(TimeSpan.FromSeconds(30), await clock.Delays.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        await worker.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
        using var check = app.Factory.Services.CreateScope();
        var rows = await check.ServiceProvider.GetRequiredService<StaticXDbContext>().PasswordRecoveryOutbox.AsNoTracking().ToListAsync();
        Assert.All(rows.Where(o => o.AccountId != valid), row => { Assert.Empty(row.ProtectedPayload); Assert.Null(row.NextAttemptAt); });
        Assert.True(validPayload == rows.Single(o => o.AccountId == valid).ProtectedPayload, "Valid pending payload must remain unchanged.");
        Assert.All(rows, row => { Assert.Null(row.LeaseOwner); Assert.Null(row.LeaseExpiresAt); Assert.Equal(0, row.AttemptCount); });
        Assert.Equal(0, sender.Calls);
    }

    [Fact]
    public async Task RecoveryDispatcher_UnknownAcceptanceFreezesSenderRecipientAndLinkAcrossValidConfigurationAndEmailChanges()
    {
        var clock = new RecoveryClock();
        await using var app = await TestApp.CreateAsync(activation: true, clock: clock);
        var id = await app.CreateGlobalAsync("original@example.invalid", "CorrectPassword1!");
        using var client = app.Client();
        using var queued = await app.PostAsync(client, "/api/account/forgot-password", new { email = "original@example.invalid" });
        Assert.Equal(HttpStatusCode.Accepted, queued.StatusCode);
        var sender = new RecoverySender { UnknownAcceptance = true };
        var configuration = RecoveryConfiguration(app);
        string? firstSnapshot = null;
        sender.BeforeSend = () =>
        {
            using var check = app.Factory.Services.CreateScope();
            firstSnapshot = check.ServiceProvider.GetRequiredService<StaticXDbContext>().PasswordRecoveryOutbox.AsNoTracking().Single().ProtectedPayload;
        };
        Assert.True(await DispatchConfiguredRecoveryAsync(app, sender, configuration));
        Assert.True(firstSnapshot?.StartsWith(PasswordRecoveryDelivery.SnapshotPrefix, StringComparison.Ordinal), "Freeze must commit before the first external send.");
        string persisted;
        using (var first = app.Factory.Services.CreateScope())
        {
            var db = first.ServiceProvider.GetRequiredService<StaticXDbContext>();
            persisted = (await db.PasswordRecoveryOutbox.SingleAsync()).ProtectedPayload;
            Assert.True(firstSnapshot == persisted, "Unknown acceptance must retain the first durable snapshot.");
            // Simulate an external email update without rotating the stamp, specifically exercising retry identity.
            await db.Users.Where(u => u.Id == id).ExecuteUpdateAsync(s => s.SetProperty(u => u.Email, "changed@example.invalid")
                .SetProperty(u => u.NormalizedEmail, "CHANGED@EXAMPLE.INVALID"));
        }
        configuration["Resend:FromAddress"] = "new-sender@example.invalid";
        configuration["PasswordRecovery:FrontendUrl"] = "https://new-frontend.example.invalid/reset";
        var snapshotStableDuringRetry = false;
        sender.BeforeSend = () =>
        {
            using var check = app.Factory.Services.CreateScope();
            var row = check.ServiceProvider.GetRequiredService<StaticXDbContext>().PasswordRecoveryOutbox.AsNoTracking().Single();
            snapshotStableDuringRetry = persisted == row.ProtectedPayload;
        };
        sender.UnknownAcceptance = false;
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.True(await DispatchConfiguredRecoveryAsync(app, sender, configuration));
        Assert.Equal(2, sender.Calls);
        Assert.True(snapshotStableDuringRetry, "The outbound snapshot must remain durably unchanged before every retry.");
        Assert.Equal("sender@example.invalid", sender.Attempts[0].From);
        Assert.Equal("original@example.invalid", sender.Attempts[0].To);
        Assert.True(sender.Attempts[0] == sender.Attempts[1], "Unknown-acceptance retries must keep the exact original key, sender, recipient and link.");
        using var reset = await app.PostAsync(client, "/api/account/reset-password", new { token = sender.Token, newPassword = "NewPassword2!" });
        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
        using var login = await app.LoginAsync(client, null, "changed@example.invalid", "NewPassword2!");
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        using var final = app.Factory.Services.CreateScope();
        Assert.Empty((await final.ServiceProvider.GetRequiredService<StaticXDbContext>().PasswordRecoveryOutbox.SingleAsync()).ProtectedPayload);
    }

    private static async Task<bool> DispatchConfiguredRecoveryAsync(TestApp app, RecoverySender sender, IConfiguration configuration)
    {
        await using var scope = app.Factory.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        return await new PasswordRecoveryDispatcher(services.GetRequiredService<IAccountRepository>(),
            services.GetRequiredService<IDataProtectionProvider>(), configuration, services.GetRequiredService<TimeProvider>(), sender).DispatchOneAsync();
    }

    [Fact]
    public async Task RecoveryRepository_FreezeRequiresCurrentOwnerAndCannotReplaceAnAlreadyFrozenSnapshot()
    {
        await using var app = await TestApp.CreateAsync(activation: true);
        await app.CreateGlobalAsync("member@example.invalid", "CorrectPassword1!");
        using var client = app.Client();
        using var queued = await app.PostAsync(client, "/api/account/forgot-password", new { email = "member@example.invalid" });
        Assert.Equal(HttpStatusCode.Accepted, queued.StatusCode);
        using var scope = app.Factory.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IAccountRepository>();
        var delivery = await repository.LeaseDueAsync(DateTimeOffset.UtcNow, default);
        Assert.NotNull(delivery);
        var protector = scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector("Static-X.PasswordRecoveryMessage.v1", delivery.RequestId.ToString("N"));
        var first = PasswordRecoveryDelivery.SnapshotPrefix + protector.Protect("first fake snapshot");
        var second = PasswordRecoveryDelivery.SnapshotPrefix + protector.Protect("replacement fake snapshot");
        var wrongOwner = new PasswordRecoveryDelivery(delivery.RequestId, Guid.NewGuid(), delivery.Email, delivery.ProtectedPayload);
        Assert.False(await repository.FreezeDeliveryAsync(wrongOwner, first, DateTimeOffset.UtcNow, default));
        Assert.True(await repository.FreezeDeliveryAsync(delivery, first, DateTimeOffset.UtcNow, default));
        Assert.False(await repository.FreezeDeliveryAsync(delivery, second, DateTimeOffset.UtcNow, default));
        var sameOwnerWithFrozenPayload = new PasswordRecoveryDelivery(delivery.RequestId, delivery.LeaseOwner, delivery.Email, first);
        Assert.False(await repository.FreezeDeliveryAsync(sameOwnerWithFrozenPayload, second, DateTimeOffset.UtcNow, default));
        var row = await scope.ServiceProvider.GetRequiredService<StaticXDbContext>().PasswordRecoveryOutbox.AsNoTracking().SingleAsync();
        Assert.True(first == row.ProtectedPayload, "Only the first owner-conditional snapshot may persist.");
        Assert.Equal(delivery.LeaseOwner, row.LeaseOwner);
        Assert.Equal(0, row.AttemptCount);
    }

    [Fact]
    public async Task RecoveryDispatcher_FrozenUnknownAttemptIsErasedWithoutResendingAfterSecurityStampChanges()
    {
        var clock = new RecoveryClock();
        await using var app = await TestApp.CreateAsync(activation: true, clock: clock);
        var id = await app.CreateGlobalAsync("member@example.invalid", "CorrectPassword1!");
        using var client = app.Client();
        using var queued = await app.PostAsync(client, "/api/account/forgot-password", new { email = "member@example.invalid" });
        Assert.Equal(HttpStatusCode.Accepted, queued.StatusCode);
        var sender = new RecoverySender { UnknownAcceptance = true };
        Assert.True(await DispatchRecoveryAsync(app, sender));
        using (var change = app.Factory.Services.CreateScope())
            await change.ServiceProvider.GetRequiredService<StaticXDbContext>().Users.Where(u => u.Id == id)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.SecurityStamp, Guid.NewGuid().ToString()));
        clock.Advance(TimeSpan.FromMinutes(1));
        sender.UnknownAcceptance = false;
        Assert.False(await DispatchRecoveryAsync(app, sender));
        Assert.Equal(1, sender.Calls);
        using var check = app.Factory.Services.CreateScope();
        var row = await check.ServiceProvider.GetRequiredService<StaticXDbContext>().PasswordRecoveryOutbox.SingleAsync();
        Assert.Empty(row.ProtectedPayload);
        Assert.NotNull(row.RevokedAt);
        Assert.Null(row.NextAttemptAt);
    }

    [Fact]
    public async Task RecoveryCleanup_ProcessesAtMostOneHundredCandidatesAndHonorsCancellation()
    {
        await using var app = await TestApp.CreateAsync(activation: true);
        await app.CreateGlobalAsync("member@example.invalid", "CorrectPassword1!");
        using var client = app.Client();
        using var queued = await app.PostAsync(client, "/api/account/forgot-password", new { email = "member@example.invalid" });
        Assert.Equal(HttpStatusCode.Accepted, queued.StatusCode);
        using var scope = app.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StaticXDbContext>();
        var pending = await db.PasswordRecoveryOutbox.AsNoTracking().SingleAsync();
        var expiredAt = DateTime.UtcNow.AddMinutes(-1);
        for (var i = 0; i < 101; i++)
            db.PasswordRecoveryOutbox.Add(new PasswordRecoveryOutbox
            {
                Id = Guid.NewGuid(), AccountId = pending.AccountId, GymId = pending.GymId, Role = pending.Role,
                StampBinding = pending.StampBinding, CreatedAt = pending.CreatedAt, ExpiresAt = expiredAt,
                ProtectedPayload = pending.ProtectedPayload, NextAttemptAt = pending.CreatedAt
            });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var repository = scope.ServiceProvider.GetRequiredService<IAccountRepository>();
        await repository.CleanupAsync(DateTimeOffset.UtcNow, default);
        Assert.Equal(100, await db.PasswordRecoveryOutbox.CountAsync(o => o.ProtectedPayload == ""));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repository.CleanupAsync(DateTimeOffset.UtcNow, cancellation.Token));
        Assert.Equal(100, await db.PasswordRecoveryOutbox.CountAsync(o => o.ProtectedPayload == ""));
        await repository.CleanupAsync(DateTimeOffset.UtcNow, default);
        Assert.Equal(101, await db.PasswordRecoveryOutbox.CountAsync(o => o.ProtectedPayload == ""));
        var unchanged = await db.PasswordRecoveryOutbox.AsNoTracking().SingleAsync(o => o.Id == pending.Id);
        Assert.True(pending.ProtectedPayload == unchanged.ProtectedPayload, "A valid pending request must not be frozen, leased or erased by cleanup.");
        Assert.Null(unchanged.LeaseOwner);
        Assert.Equal(102, await db.PasswordRecoveryOutbox.CountAsync());
    }

    private sealed class RecoveryIterationClock : TimeProvider
    {
        public Channel<TimeSpan> Delays { get; } = Channel.CreateUnbounded<TimeSpan>();
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        { Delays.Writer.TryWrite(dueTime); return new InertTimer(); }
        private sealed class InertTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => false;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
