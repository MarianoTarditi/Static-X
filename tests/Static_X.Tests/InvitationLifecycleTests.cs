using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BLL.DTOs.Activations;
using DAL;
using Entity;
using BLL.Services.Interfaces;
using DAL.Repositories.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Static_X.Tests;

public sealed partial class AccountHttpTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvitationLifecycle_GlobalRoutes_RejectAnonymousAndTenantWithoutCaching(bool authenticated)
    {
        await using var app = await TestApp.CreateAsync(activation: true);
        var gym = await app.CreateGymAsync();
        using var actor = app.Client();
        if (authenticated)
        {
            await app.CreateAccountAsync(gym, "tenant@example.invalid", "CorrectPassword1!", true, AccountRole.GymAdmin);
            Assert.Equal(HttpStatusCode.OK, (await app.LoginAsync(actor, gym, "tenant@example.invalid", "CorrectPassword1!")).StatusCode);
        }
        using var listed = await actor.GetAsync($"/api/gyms/{gym}/administrators/invitations");
        Assert.Equal(authenticated ? HttpStatusCode.Forbidden : HttpStatusCode.Unauthorized, listed.StatusCode);
        Assert.True(listed.Headers.CacheControl?.NoStore);
        foreach (var suffix in new[] { "invitation", "invitation/cancel" })
        {
            using var response = await app.PostAsync(actor, $"/api/gyms/{gym}/administrators/unknown/{suffix}", null);
            Assert.Equal(listed.StatusCode, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
        }
    }

    [Fact]
    public async Task InvitationLifecycle_SentHistoryAndPagination_RemainPendingAndCannotBypassCooldown()
    {
        var clock = new InvitationClock();
        await using var app = await TestApp.CreateAsync(activation: true, clock: clock);
        var gym = await app.CreateGymAsync();
        using var actor = await InvitationActorAsync(app, gym);
        foreach (var key in new[] { "one", "two", "three" })
            Assert.Equal(HttpStatusCode.OK, (await app.PostAsync(actor, InvitationsRoute, new InviteAccountRequest(key, key + "@example.invalid", AccountRole.Trainer))).StatusCode);
        string[] ids;
        using (var scope = app.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StaticXDbContext>();
            ids = await db.Users.Where(u => u.Role == AccountRole.Trainer).OrderBy(u => u.Id).Select(u => u.Id).ToArrayAsync();
            await db.ActivationOutbox.ExecuteUpdateAsync(s => s.SetProperty(o => o.SentAt, clock.Now.UtcDateTime)
                .SetProperty(o => o.ProtectedToken, string.Empty).SetProperty(o => o.NextAttemptAt, (DateTime?)null));
        }
        var page = await actor.GetFromJsonAsync<InvitationListResponse>(InvitationsRoute + "?role=2&offset=1&pageSize=1");
        Assert.Equal(3, page!.TotalCount);
        Assert.Equal(ids[1], Assert.Single(page.Items).AccountId);
        Assert.Equal("Pending", page.Items[0].Status);
        var empty = await actor.GetFromJsonAsync<InvitationListResponse>(InvitationsRoute + "?role=2&offset=3&pageSize=1");
        Assert.Equal(3, empty!.TotalCount);
        Assert.Empty(empty.Items);
        Assert.Equal(HttpStatusCode.Conflict, (await app.PostAsync(actor, $"{InvitationsRoute}/{ids[0]}/resend?role=2", null)).StatusCode);
        foreach (var query in new[] { "offset=-1", "offset=100001", "pageSize=0", "pageSize=101", "pageSize=invalid" })
            Assert.Equal(HttpStatusCode.BadRequest, (await actor.GetAsync(InvitationsRoute + "?role=2&" + query)).StatusCode);
    }

    [Fact]
    public async Task InvitationLifecycle_ConcurrentResends_QueueOnlyOneReplacement()
    {
        var clock = new InvitationClock();
        await using var app = await TestApp.CreateAsync(activation: true, clock: clock, recoveryConcurrency: true);
        var gym = await app.CreateGymAsync();
        using var first = await InvitationActorAsync(app, gym);
        using var second = app.Client();
        Assert.Equal(HttpStatusCode.OK, (await app.LoginAsync(second, gym, "actor@example.invalid", "CorrectPassword1!")).StatusCode);
        using var invite = await app.PostAsync(first, InvitationsRoute, new InviteAccountRequest("first", "recipient@example.invalid", AccountRole.Trainer));
        var id = (await invite.Content.ReadFromJsonAsync<AccountInvitationResponse>())!.AccountId;
        clock.Now = clock.Now.AddHours(1);
        var route = $"{InvitationsRoute}/{id}/resend?role=2";
        var results = await Task.WhenAll(app.PostAsync(first, route, null), app.PostAsync(second, route, null));
        Assert.Single(results, r => r.StatusCode == HttpStatusCode.NoContent);
        Assert.Single(results, r => r.StatusCode == HttpStatusCode.Conflict);
        foreach (var response in results) response.Dispose();
        using var scope = app.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StaticXDbContext>();
        Assert.Equal(2, await db.ActivationOutbox.CountAsync());
        Assert.Single(await db.ActivationInvitations.Where(i => i.RevokedAt == null).ToListAsync());
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("resend")]
    public async Task InvitationLifecycle_CommitFailure_RollsBackRevocationAndPreservesOriginalLink(string action)
    {
        var clock = new InvitationClock();
        var interceptor = new TenantCommitFailure();
        await using var app = await TestApp.CreateAsync(activation: true, clock: clock, tenantCommitInterceptor: interceptor);
        var gym = await app.CreateGymAsync();
        using var actor = await InvitationActorAsync(app, gym);
        using var invite = await app.PostAsync(actor, InvitationsRoute, new InviteAccountRequest("first", "recipient@example.invalid", AccountRole.Trainer));
        var id = (await invite.Content.ReadFromJsonAsync<AccountInvitationResponse>())!.AccountId;
        var token = await InvitationTokenAsync(app, id);
        clock.Now = clock.Now.AddHours(1);
        interceptor.Fail = true;
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await app.PostAsync(actor, $"{InvitationsRoute}/{id}/{action}?role=2", null)).StatusCode);
        interceptor.Fail = false;
        Assert.Equal(token, await InvitationTokenAsync(app, id));
        using var scope = app.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StaticXDbContext>();
        Assert.Single(await db.ActivationOutbox.ToListAsync());
        Assert.Null((await db.ActivationInvitations.SingleAsync()).RevokedAt);
        using var anonymous = app.Client();
        Assert.Equal(HttpStatusCode.OK, (await app.PostAsync(anonymous, "/api/account/activation", new { token, password = "CorrectPassword1!" })).StatusCode);
    }

    [Theory]
    [InlineData(AccountRole.Student, AccountRole.Student)]
    [InlineData(AccountRole.SuperAdmin, AccountRole.Student)]
    [InlineData(AccountRole.GymAdmin, AccountRole.GymAdmin)]
    [InlineData(AccountRole.Trainer, AccountRole.Trainer)]
    [InlineData(AccountRole.SuperAdmin, AccountRole.SuperAdmin)]
    public async Task InvitationLifecycle_ServiceIndependentlyRejectsUnauthorizedRolePairs(AccountRole actor, AccountRole target)
    {
        await using var app = await TestApp.CreateAsync(activation: true);
        using var scope = app.Factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IActivationService>();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ListInvitationsAsync(1, actor, target, new InvitationListRequest()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.CancelInvitationAsync(1, actor, target, "opaque"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ResendInvitationAsync(1, actor, target, "opaque"));
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<StaticXDbContext>().ActivationOutbox.ToListAsync());
    }

    [Theory]
    [InlineData("password")]
    [InlineData("confirmed")]
    [InlineData("wrong-role")]
    [InlineData("link")]
    [InlineData("disabled")]
    [InlineData("inactive")]
    public async Task InvitationLifecycle_ChangedBindingOrCredentials_ResendDoesNotAdoptOrRestoreAccess(string change)
    {
        var clock = new InvitationClock();
        await using var app = await TestApp.CreateAsync(activation: true, clock: clock);
        var gym = await app.CreateGymAsync();
        var student = await InvitationStudentAsync(app, gym);
        using var actor = await InvitationActorAsync(app, gym);
        using var invite = await app.PostAsync(actor, InvitationsRoute, new InviteAccountRequest("first", "recipient@example.invalid", AccountRole.Student, student));
        var id = (await invite.Content.ReadFromJsonAsync<AccountInvitationResponse>())!.AccountId;
        clock.Now = clock.Now.AddHours(1);
        using (var scope = app.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StaticXDbContext>();
            var user = await db.Users.SingleAsync(u => u.Id == id);
            if (change == "password") user.PasswordHash = "historical-password";
            if (change == "confirmed") user.EmailConfirmed = true;
            if (change == "wrong-role") user.Role = AccountRole.Trainer;
            if (change == "disabled") user.IsEnabled = false;
            if (change == "link") (await db.Alumnos.SingleAsync()).AccountId = null;
            if (change == "inactive") (await db.Alumnos.SingleAsync()).IsActive = false;
            await db.SaveChangesAsync();
        }
        Assert.Equal(HttpStatusCode.NotFound, (await app.PostAsync(actor, $"{InvitationsRoute}/{id}/resend?role=3", null)).StatusCode);
        using var verify = app.Factory.Services.CreateScope();
        Assert.Single(await verify.ServiceProvider.GetRequiredService<StaticXDbContext>().ActivationOutbox.ToListAsync());
    }

    [Theory]
    [InlineData(AccountRole.GymAdmin)]
    [InlineData(AccountRole.Trainer)]
    [InlineData(AccountRole.Student)]
    public async Task InvitationLifecycle_ThreeRoles_CancelFreshIssueExpireResendAndActivatePreserveOwnership(AccountRole role)
    {
        var clock = new InvitationClock();
        var start = clock.Now;
        await using var app = await TestApp.CreateAsync(activation: true, clock: clock);
        int gym;
        string id;
        int? student = null;
        HttpClient actor;
        if (role == AccountRole.GymAdmin)
        {
            (gym, _) = await app.ProvisionAsync();
            await app.CreateGlobalAsync("global@example.invalid", "CorrectPassword1!");
            actor = app.Client();
            Assert.Equal(HttpStatusCode.OK, (await app.LoginAsync(actor, null, "global@example.invalid", "CorrectPassword1!")).StatusCode);
            using var scope = app.Factory.Services.CreateScope();
            id = await scope.ServiceProvider.GetRequiredService<StaticXDbContext>().Users.Where(u => u.GymId == gym).Select(u => u.Id).SingleAsync();
        }
        else
        {
            gym = await app.CreateGymAsync();
            if (role == AccountRole.Student) student = await InvitationStudentAsync(app, gym);
            actor = await InvitationActorAsync(app, gym);
            using var issued = await app.PostAsync(actor, InvitationsRoute, new InviteAccountRequest("original", "recipient@example.invalid", role, student));
            Assert.Equal(HttpStatusCode.OK, issued.StatusCode);
            id = (await issued.Content.ReadFromJsonAsync<AccountInvitationResponse>())!.AccountId;
        }
        using (actor)
        {
            var list = role == AccountRole.GymAdmin ? $"/api/gyms/{gym}/administrators/invitations" : InvitationsRoute + "?role=" + (int)role;
            var resend = role == AccountRole.GymAdmin ? $"/api/gyms/{gym}/administrators/{id}/invitation" : $"{InvitationsRoute}/{id}/resend?role={(int)role}";
            var cancel = role == AccountRole.GymAdmin ? resend + "/cancel" : $"{InvitationsRoute}/{id}/cancel?role={(int)role}";
            var oldToken = await InvitationTokenAsync(app, id);
            async Task AssertState(string expected, DateTimeOffset queued)
            {
                using var response = await actor.GetAsync(list);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.True(response.Headers.CacheControl?.NoStore);
                var json = await response.Content.ReadFromJsonAsync<JsonElement>();
                var item = Assert.Single(json.GetProperty("items").EnumerateArray());
                Assert.Equal(new[] { "accountId", "email", "expiresAt", "nextResendAt", "queuedAt", "role", "status" }, item.EnumerateObject().Select(p => p.Name).Order());
                Assert.Equal(1, json.GetProperty("totalCount").GetInt32());
                Assert.Equal(id, item.GetProperty("accountId").GetString());
                Assert.Equal(expected, item.GetProperty("status").GetString());
                Assert.Equal(queued, item.GetProperty("queuedAt").GetDateTimeOffset());
                Assert.Equal(queued.AddHours(1), item.GetProperty("nextResendAt").GetDateTimeOffset());
            }
            await AssertState("Pending", start);
            string originalAccount;
            using (var scope = app.Factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<StaticXDbContext>();
                originalAccount = JsonSerializer.Serialize(await db.Users.AsNoTracking().SingleAsync(u => u.Id == id));
            }
            using var deliveryScope = app.Factory.Services.CreateScope();
            var deliveries = deliveryScope.ServiceProvider.GetRequiredService<IActivationDeliveryRepository>();
            var lease = await deliveries.LeaseDueAsync(clock.Now, default);
            Assert.NotNull(lease);
            Assert.Equal(HttpStatusCode.BadRequest, (await actor.PostAsync(cancel, null)).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await app.PostAsync(actor, cancel, null)).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await app.PostAsync(actor, cancel, null)).StatusCode);
            await AssertState("Cancelled", start);
            Assert.False(await deliveries.IsCurrentAsync(lease!, clock.Now, default));
            await deliveries.CompleteAsync(lease!, clock.Now, false, default);
            Assert.Null(await deliveries.LeaseDueAsync(clock.Now, default));
            using var anonymous = app.Client();
            Assert.Equal(HttpStatusCode.BadRequest, (await app.PostAsync(anonymous, "/api/account/activation", new { token = oldToken, password = "CorrectPassword1!" })).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await app.PostAsync(actor, resend, null)).StatusCode);
            if (role != AccountRole.GymAdmin)
            {
                Assert.Equal(HttpStatusCode.OK, (await app.PostAsync(actor, InvitationsRoute,
                    new InviteAccountRequest("original", "recipient@example.invalid", role, student))).StatusCode);
                Assert.Equal(HttpStatusCode.Conflict, (await app.PostAsync(actor, InvitationsRoute,
                    new InviteAccountRequest("fresh", "recipient@example.invalid", role, student))).StatusCode);
                await AssertState("Cancelled", start);
            }
            using (var verify = app.Factory.Services.CreateScope())
            {
                var db = verify.ServiceProvider.GetRequiredService<StaticXDbContext>();
                Assert.True(originalAccount == JsonSerializer.Serialize(await db.Users.AsNoTracking().SingleAsync(u => u.Id == id)), "Cancellation must not change account data.");
                var job = await db.ActivationOutbox.SingleAsync();
                Assert.Empty(job.ProtectedToken);
                Assert.Null(job.NextAttemptAt);
                Assert.Null(job.LeaseOwner);
                Assert.Null(job.LeaseExpiresAt);
                Assert.Equal(start.UtcDateTime, (await db.ActivationInvitations.SingleAsync()).RevokedAt);
                if (student is not null)
                {
                    var ficha = await db.Alumnos.SingleAsync();
                    Assert.Equal((id, true, "contact@example.invalid"), (ficha.AccountId, ficha.IsActive, ficha.Email));
                }
            }
            clock.Now = start.AddHours(1);
            var fresh = role == AccountRole.GymAdmin ? await app.PostAsync(actor, resend, null) :
                await app.PostAsync(actor, InvitationsRoute, new InviteAccountRequest("fresh", "recipient@example.invalid", role, student));
            using (fresh) Assert.Equal(role == AccountRole.GymAdmin ? HttpStatusCode.NoContent : HttpStatusCode.OK, fresh.StatusCode);
            await AssertState("Pending", clock.Now);
            Assert.NotEqual(oldToken, await InvitationTokenAsync(app, id));
            clock.Now = start.AddHours(25);
            await AssertState("Expired", start.AddHours(1));
            Assert.Equal(HttpStatusCode.NoContent, (await app.PostAsync(actor, resend, null)).StatusCode);
            var current = await InvitationTokenAsync(app, id);
            await AssertState("Pending", clock.Now);
            using var activated = await app.PostAsync(anonymous, "/api/account/activation", new { token = current, password = "CorrectPassword1!" });
            Assert.Equal(HttpStatusCode.OK, activated.StatusCode);
            Assert.False(activated.Headers.Contains("Set-Cookie"));
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/account/me")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await app.PostAsync(actor, cancel, null)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await app.PostAsync(actor, resend, null)).StatusCode);
            var empty = await actor.GetFromJsonAsync<InvitationListResponse>(list);
            Assert.Empty(empty!.Items);
        }
    }

    [Theory]
    [InlineData(3599, false, 409)]
    [InlineData(3600, false, 204)]
    [InlineData(3601, false, 204)]
    [InlineData(3599, true, 409)]
    [InlineData(3600, true, 200)]
    [InlineData(3601, true, 200)]
    public async Task InvitationLifecycle_TenantExplicitAndLegacyResend_ShareCooldown(int seconds, bool legacy, int status)
    {
        var clock = new InvitationClock();
        await using var app = await TestApp.CreateAsync(activation: true, clock: clock);
        var gym = await app.CreateGymAsync();
        using var actor = await InvitationActorAsync(app, gym);
        using var issued = await app.PostAsync(actor, InvitationsRoute, new InviteAccountRequest("first", "recipient@example.invalid", AccountRole.Trainer));
        var id = (await issued.Content.ReadFromJsonAsync<AccountInvitationResponse>())!.AccountId;
        var old = await InvitationTokenAsync(app, id);
        clock.Now = clock.Now.AddSeconds(seconds);
        using var response = legacy ? await app.PostAsync(actor, InvitationsRoute, new InviteAccountRequest("second", "recipient@example.invalid", AccountRole.Trainer)) :
            await app.PostAsync(actor, $"{InvitationsRoute}/{id}/resend?role=2", null);
        Assert.Equal((HttpStatusCode)status, response.StatusCode);
        Assert.Equal(status == 409, old == await InvitationTokenAsync(app, id));
        using var scope = app.Factory.Services.CreateScope();
        Assert.Equal(status == 409 ? 1 : 2, await scope.ServiceProvider.GetRequiredService<StaticXDbContext>().ActivationOutbox.CountAsync());
    }

    [Theory]
    [InlineData(AccountRole.GymAdmin, AccountRole.Trainer, true)]
    [InlineData(AccountRole.GymAdmin, AccountRole.Student, true)]
    [InlineData(AccountRole.Trainer, AccountRole.Student, true)]
    [InlineData(AccountRole.Trainer, AccountRole.Trainer, false)]
    [InlineData(AccountRole.Student, AccountRole.Student, false)]
    [InlineData(AccountRole.SuperAdmin, AccountRole.Student, false)]
    public async Task InvitationLifecycle_TenantActions_EnforceRoleMatrixAndForeignTargets(AccountRole actorRole, AccountRole role, bool allowed)
    {
        var clock = new InvitationClock();
        await using var app = await TestApp.CreateAsync(activation: true, clock: clock);
        var gym = await app.CreateGymAsync();
        var other = await app.CreateGymAsync();
        using var creator = await InvitationActorAsync(app, other);
        var student = role == AccountRole.Student ? (int?)await InvitationStudentAsync(app, other) : null;
        using var invite = await app.PostAsync(creator, InvitationsRoute, new InviteAccountRequest("foreign", "recipient@example.invalid", role, student));
        var id = (await invite.Content.ReadFromJsonAsync<AccountInvitationResponse>())!.AccountId;
        if (actorRole == AccountRole.SuperAdmin) await app.CreateGlobalAsync("caller@example.invalid", "CorrectPassword1!");
        else await app.CreateAccountAsync(gym, "caller@example.invalid", "CorrectPassword1!", true, actorRole);
        using var actor = app.Client();
        Assert.Equal(HttpStatusCode.OK, (await app.LoginAsync(actor, actorRole == AccountRole.SuperAdmin ? null : gym, "caller@example.invalid", "CorrectPassword1!")).StatusCode);
        using var listed = await actor.GetAsync(InvitationsRoute + "?role=" + (int)role);
        Assert.Equal(allowed ? HttpStatusCode.OK : HttpStatusCode.Forbidden, listed.StatusCode);
        if (allowed) Assert.Empty((await listed.Content.ReadFromJsonAsync<InvitationListResponse>())!.Items);
        foreach (var action in new[] { "cancel", "resend" })
        {
            var foreign = await app.PostAsync(actor, $"{InvitationsRoute}/{id}/{action}?role={(int)role}", null);
            var missing = await app.PostAsync(actor, $"{InvitationsRoute}/unknown/{action}?role={(int)role}", null);
            using (foreign)
            using (missing)
            {
                Assert.Equal(allowed ? HttpStatusCode.NotFound : HttpStatusCode.Forbidden, foreign.StatusCode);
                Assert.Equal(foreign.StatusCode, missing.StatusCode);
                if (allowed) Assert.Equal(await foreign.Content.ReadAsStringAsync(), await missing.Content.ReadAsStringAsync());
            }
        }
        using var scope = app.Factory.Services.CreateScope();
        Assert.Null((await scope.ServiceProvider.GetRequiredService<StaticXDbContext>().ActivationInvitations.SingleAsync()).RevokedAt);
    }

    [Theory]
    [InlineData("resend")]
    [InlineData("activate")]
    public async Task InvitationLifecycle_ConcurrentCancelAndWriter_HasSerializedOutcome(string operation)
    {
        var clock = new InvitationClock();
        await using var app = await TestApp.CreateAsync(activation: true, clock: clock, recoveryConcurrency: true);
        var gym = await app.CreateGymAsync();
        using var actor = await InvitationActorAsync(app, gym);
        using var invite = await app.PostAsync(actor, InvitationsRoute, new InviteAccountRequest("first", "recipient@example.invalid", AccountRole.Trainer));
        var id = (await invite.Content.ReadFromJsonAsync<AccountInvitationResponse>())!.AccountId;
        var token = await InvitationTokenAsync(app, id);
        clock.Now = clock.Now.AddHours(1);
        using var writer = app.Client();
        if (operation == "resend") Assert.Equal(HttpStatusCode.OK, (await app.LoginAsync(writer, gym, "actor@example.invalid", "CorrectPassword1!")).StatusCode);
        var results = await Task.WhenAll(app.PostAsync(actor, $"{InvitationsRoute}/{id}/cancel?role=2", null),
            operation == "resend" ? app.PostAsync(writer, $"{InvitationsRoute}/{id}/resend?role=2", null) :
                app.PostAsync(writer, "/api/account/activation", new { token, password = "CorrectPassword1!" }));
        using var scope = app.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StaticXDbContext>();
        var user = await db.Users.SingleAsync(u => u.Id == id);
        if (operation == "activate")
        {
            Assert.Equal(results[1].StatusCode == HttpStatusCode.OK, user.EmailConfirmed);
            Assert.Equal(user.EmailConfirmed ? HttpStatusCode.NotFound : HttpStatusCode.NoContent, results[0].StatusCode);
            Assert.Equal(user.EmailConfirmed ? HttpStatusCode.OK : HttpStatusCode.BadRequest, results[1].StatusCode);
        }
        else
        {
            Assert.All(results, r => Assert.Equal(HttpStatusCode.NoContent, r.StatusCode));
            Assert.Equal(2, await db.ActivationInvitations.CountAsync());
            Assert.Null(user.PasswordHash);
            Assert.False(user.EmailConfirmed);
        }
        Assert.Equal(HttpStatusCode.BadRequest, (await app.PostAsync(app.Client(), "/api/account/activation", new { token, password = "OtherPassword1!" })).StatusCode);
        foreach (var response in results) response.Dispose();
    }

    [Theory]
    [InlineData(3599, 409)]
    [InlineData(3600, 204)]
    [InlineData(3601, 204)]
    public async Task InvitationLifecycle_LegacyAdministratorResend_EnforcesQueueTimeBoundary(int seconds, int status)
    {
        var clock = new InvitationClock();
        await using var app = await TestApp.CreateAsync(activation: true, clock: clock);
        var (gym, _) = await app.ProvisionAsync();
        await app.CreateGlobalAsync("global@example.invalid", "CorrectPassword1!");
        using var actor = app.Client();
        Assert.Equal(HttpStatusCode.OK, (await app.LoginAsync(actor, null, "global@example.invalid", "CorrectPassword1!")).StatusCode);
        string id;
        using (var scope = app.Factory.Services.CreateScope())
            id = await scope.ServiceProvider.GetRequiredService<StaticXDbContext>().Users.Where(u => u.GymId == gym).Select(u => u.Id).SingleAsync();
        clock.Now = clock.Now.AddSeconds(seconds);
        using var response = await app.PostAsync(actor, $"/api/gyms/{gym}/administrators/{id}/invitation", null);
        Assert.Equal((HttpStatusCode)status, response.StatusCode);
        using var verify = app.Factory.Services.CreateScope();
        var db = verify.ServiceProvider.GetRequiredService<StaticXDbContext>();
        Assert.Equal(status == 204 ? 2 : 1, await db.ActivationOutbox.CountAsync());
    }

    private sealed class InvitationClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
