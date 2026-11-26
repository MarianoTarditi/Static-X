using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BLL.DTOs.Activations;
using DAL;
using DAL.Repositories.Interfaces;
using Entity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Static_X.Tests;

public sealed partial class AccountHttpTests
{
    [Fact]
    public async Task Trainers_ListIncludesAllStatesWithExactSafeFieldsAndTenantScopedOrderedPages()
    {
        await using var app = await TestApp.CreateAsync(activation: true);
        var gym = await app.CreateGymAsync();
        var other = await app.CreateGymAsync();
        using var actor = await InvitationActorAsync(app, gym);
        await SeedTrainerAsync(app, gym, "c-active", true, true);
        await SeedTrainerAsync(app, gym, "a-pending", true, false);
        await SeedTrainerAsync(app, gym, "b-disabled", false, false);
        await SeedTrainerAsync(app, other, "foreign", true, true);
        await app.CreateAccountAsync(gym, "student@example.invalid", "CorrectPassword1!", true);
        var before = await app.LogoutStateAsync();
        using var first = await actor.GetAsync($"/api/trainers?page=1&pageSize=2&gymId={other}&role=4");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.True(first.Headers.CacheControl?.NoStore);
        var body = await first.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(new[] { "items", "page", "pageSize", "totalCount" }, body.EnumerateObject().Select(p => p.Name));
        Assert.Equal((1, 2, 3), (body.GetProperty("page").GetInt32(), body.GetProperty("pageSize").GetInt32(), body.GetProperty("totalCount").GetInt32()));
        var items = body.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(new[] { "a-pending", "b-disabled" }, items.Select(i => i.GetProperty("accountId").GetString()));
        Assert.All(items, i => Assert.Equal(new[] { "accountId", "email", "isEnabled", "emailConfirmed" }, i.EnumerateObject().Select(p => p.Name)));
        Assert.True(items[0].GetProperty("isEnabled").GetBoolean());
        Assert.False(items[0].GetProperty("emailConfirmed").GetBoolean());
        Assert.False(items[1].GetProperty("isEnabled").GetBoolean());
        Assert.Equal("a-pending@example.invalid", items[0].GetProperty("email").GetString());
        using var second = await actor.GetAsync("/api/trainers?page=2&pageSize=2");
        var page = await second.Content.ReadFromJsonAsync<JsonElement>();
        var active = Assert.Single(page.GetProperty("items").EnumerateArray());
        Assert.Equal("c-active", active.GetProperty("accountId").GetString());
        Assert.True(active.GetProperty("emailConfirmed").GetBoolean());
        using var past = await actor.GetAsync("/api/trainers?page=3&pageSize=2");
        var empty = await past.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Empty(empty.GetProperty("items").EnumerateArray());
        Assert.Equal(3, empty.GetProperty("totalCount").GetInt32());
        Assert.True(before == await app.LogoutStateAsync(), "Listing must not mutate isolated persisted state.");
    }

    [Theory]
    [InlineData("anonymous", 401)]
    [InlineData("Trainer", 403)]
    [InlineData("Student", 403)]
    [InlineData("SuperAdmin", 403)]
    public async Task Trainers_AllRoutesAuthorizeBeforeInvalidInput(string role, int status)
    {
        await using var app = await TestApp.CreateAsync();
        var gym = await app.CreateGymAsync();
        using var client = app.Client();
        if (role != "anonymous")
        {
            if (role == "SuperAdmin") await app.CreateGlobalAsync("actor@example.invalid", "CorrectPassword1!");
            else await app.CreateAccountAsync(gym, "actor@example.invalid", "CorrectPassword1!", true, Enum.Parse<AccountRole>(role));
            Assert.Equal(HttpStatusCode.OK, (await app.LoginAsync(client, role == "SuperAdmin" ? null : gym, "actor@example.invalid", "CorrectPassword1!")).StatusCode);
        }
        var before = await app.LogoutStateAsync();
        foreach (var route in new[] { "/api/trainers", "/api/trainers?page=invalid", "/api/trainers/%20/deactivate", "/api/trainers/%20/reactivate", "/api/trainers/opaque/deactivate", "/api/trainers/opaque/reactivate" })
        {
            using var request = new HttpRequestMessage(route.Contains("/deactivate") || route.Contains("/reactivate") ? HttpMethod.Patch : HttpMethod.Get, route);
            using var response = await client.SendAsync(request);
            Assert.Equal((HttpStatusCode)status, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
        }
        Assert.True(before == await app.LogoutStateAsync(), "Rejected callers must not change accounts.");
    }

    [Theory]
    [InlineData("page=0")]
    [InlineData("page=-1")]
    [InlineData("pageSize=0")]
    [InlineData("pageSize=101")]
    [InlineData("pageSize=-1")]
    [InlineData("page=2147483647&pageSize=100")]
    [InlineData("page=2147483648")]
    [InlineData("pageSize=bad")]
    public async Task Trainers_InvalidPagination_ReturnsNoStore400(string query)
    {
        await using var app = await TestApp.CreateAsync();
        var gym = await app.CreateGymAsync();
        using var actor = await InvitationActorAsync(app, gym);
        var before = await app.LogoutStateAsync();
        using var response = await actor.GetAsync("/api/trainers?" + query);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.True(before == await app.LogoutStateAsync(), "Invalid pagination cannot write.");
    }

    [Theory]
    [InlineData("", 1, 20)]
    [InlineData("?pageSize=100", 1, 100)]
    [InlineData("?page=2147483647&pageSize=1", 2147483647, 1)]
    public async Task Trainers_EmptyGymAndValidBounds_ReturnExactEmptyPage(string query, int page, int size)
    {
        await using var app = await TestApp.CreateAsync();
        var gym = await app.CreateGymAsync();
        using var actor = await InvitationActorAsync(app, gym);
        using var response = await actor.GetAsync("/api/trainers" + query);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal($"{{\"items\":[],\"page\":{page},\"pageSize\":{size},\"totalCount\":0}}", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("deactivate")]
    [InlineData("reactivate")]
    public async Task Trainers_MissingForeignAndWrongRoles_AreIndistinguishableWithoutWrites(string action)
    {
        await using var app = await TestApp.CreateAsync();
        var gym = await app.CreateGymAsync();
        var other = await app.CreateGymAsync();
        using var actor = await InvitationActorAsync(app, gym);
        await SeedTrainerAsync(app, other, "foreign", true, true);
        var student = await app.CreateAccountAsync(gym, "student@example.invalid", "CorrectPassword1!", true);
        var global = await app.CreateGlobalAsync("global@example.invalid", "CorrectPassword1!");
        string self;
        using (var scope = app.Factory.Services.CreateScope()) self = (await scope.ServiceProvider.GetRequiredService<StaticXDbContext>().Users.SingleAsync(u => u.Role == AccountRole.GymAdmin)).Id;
        var before = await app.LogoutStateAsync();
        string? expected = null;
        foreach (var id in new[] { "missing", "foreign", student.Id, global, self })
        {
            using var response = await TrainerPatchAsync(actor, id, action);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
            var body = await response.Content.ReadAsStringAsync();
            expected ??= body;
            Assert.Equal(expected, body);
            Assert.DoesNotContain("email", body, StringComparison.OrdinalIgnoreCase);
        }
        Assert.True(before == await app.LogoutStateAsync(), "Isolated targets must not change any account.");
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("invalid")]
    [InlineData("prelogin")]
    public async Task Trainers_CsrfRejectionAndGet405_PreserveEnablementAndSessions(string scenario)
    {
        await using var app = await TestApp.CreateAsync();
        var gym = await app.CreateGymAsync();
        await SeedTrainerAsync(app, gym, "target", true, true);
        await app.CreateAccountAsync(gym, "actor@example.invalid", "CorrectPassword1!", true, AccountRole.GymAdmin);
        using var actor = app.Client();
        var prelogin = (await actor.GetFromJsonAsync<JsonElement>("/api/account/csrf")).GetProperty("token").GetString();
        Assert.Equal(HttpStatusCode.OK, (await app.LoginAsync(actor, gym, "actor@example.invalid", "CorrectPassword1!")).StatusCode);
        using var trainer = app.Client();
        Assert.Equal(HttpStatusCode.OK, (await app.LoginAsync(trainer, gym, "target@example.invalid", "CorrectPassword1!")).StatusCode);
        var before = await app.LogoutStateAsync();
        foreach (var action in new[] { "deactivate", "reactivate" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Patch, $"/api/trainers/target/{action}");
            if (scenario != "missing") request.Headers.Add("X-CSRF-TOKEN", scenario == "invalid" ? "invalid" : prelogin);
            using var response = await actor.SendAsync(request);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
            Assert.Equal(HttpStatusCode.MethodNotAllowed, (await actor.GetAsync($"/api/trainers/target/{action}")).StatusCode);
        }
        Assert.True(before == await app.LogoutStateAsync(), "CSRF/verb rejection cannot change state or stamps.");
        Assert.Equal(HttpStatusCode.OK, (await trainer.GetAsync("/api/account/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await actor.GetAsync("/api/account/me")).StatusCode);
    }

    [Theory]
    [InlineData("blank")]
    [InlineData("long")]
    public async Task Trainers_InvalidOpaqueId_Returns400ForBothActions(string scenario)
    {
        await using var app = await TestApp.CreateAsync();
        var gym = await app.CreateGymAsync();
        using var actor = await InvitationActorAsync(app, gym);
        var before = await app.LogoutStateAsync();
        foreach (var action in new[] { "deactivate", "reactivate" })
        {
            using var response = await TrainerPatchAsync(actor, scenario == "blank" ? " " : new string('x', 451), action);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
        }
        Assert.True(before == await app.LogoutStateAsync(), "Invalid IDs cannot mutate accounts.");
    }

    [Fact]
    public async Task Trainers_DisableRevokesAllCookiesAndRecovery_ReactivationRequiresFreshLogin()
    {
        await using var app = await TestApp.CreateAsync(activation: true);
        var gym = await app.CreateGymAsync();
        using var actor = await InvitationActorAsync(app, gym);
        await SeedTrainerAsync(app, gym, "legacy-trainer-id", true, true);
        await SeedTrainerAsync(app, gym, "other", true, true);
        var student = await InvitationStudentAsync(app, gym);
        using var current = app.Client();
        using var login = await app.LoginAsync(current, gym, "legacy-trainer-id@example.invalid", "CorrectPassword1!");
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        using var copied = app.Factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), HandleCookies = false, AllowAutoRedirect = false });
        copied.DefaultRequestHeaders.Add("Cookie", Assert.Single(login.Headers.GetValues("Set-Cookie"), c => c.StartsWith(".AspNetCore.Identity.Application=")).Split(';')[0]);
        using var other = app.Client();
        Assert.Equal(HttpStatusCode.OK, (await app.LoginAsync(other, gym, "other@example.invalid", "CorrectPassword1!")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await copied.GetAsync("/api/account/me")).StatusCode);
        using var anonymous = app.Client();
        Assert.Equal(HttpStatusCode.Accepted, (await app.PostAsync(anonymous, "/api/account/forgot-password", new { email = "legacy-trainer-id@example.invalid", gymId = gym })).StatusCode);
        string recovery;
        IdentityAccount original;
        using (var scope = app.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StaticXDbContext>();
            original = await db.Users.AsNoTracking().SingleAsync(u => u.Id == "legacy-trainer-id");
            var job = await db.PasswordRecoveryOutbox.SingleAsync();
            recovery = scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>().CreateProtector("Static-X.PasswordRecoveryDelivery.v1", job.Id.ToString("N")).Unprotect(job.ProtectedPayload);
        }
        using var disabled = await TrainerPatchAsync(actor, "legacy-trainer-id", "deactivate");
        Assert.Equal(HttpStatusCode.NoContent, disabled.StatusCode);
        Assert.Empty(await disabled.Content.ReadAsStringAsync());
        Assert.True(disabled.Headers.CacheControl?.NoStore);
        Assert.False(disabled.Headers.Contains("Set-Cookie"));
        foreach (var session in new[] { current, copied }) Assert.Equal(HttpStatusCode.Unauthorized, (await session.GetAsync("/api/account/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await other.GetAsync("/api/account/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await actor.GetAsync("/api/account/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.LoginAsync(anonymous, gym, original.Email!, "CorrectPassword1!")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await app.PostAsync(anonymous, "/api/account/reset-password", new { token = recovery, newPassword = "NewPassword2!" })).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await app.PostAsync(anonymous, "/api/account/forgot-password", new { email = original.Email, gymId = gym })).StatusCode);
        string rotated;
        string concurrency;
        using (var scope = app.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StaticXDbContext>();
            var user = await db.Users.SingleAsync(u => u.Id == original.Id);
            rotated = user.SecurityStamp!;
            concurrency = user.ConcurrencyStamp!;
            Assert.NotEqual(original.SecurityStamp, rotated);
            Assert.NotEqual(original.ConcurrencyStamp, concurrency);
            Assert.False(user.IsEnabled);
            Assert.False(await scope.ServiceProvider.GetRequiredService<IAccountRepository>().ChangePasswordAsync(gym, user.Id, "CorrectPassword1!", "NewPassword2!", default));
            Assert.Null(await scope.ServiceProvider.GetRequiredService<IAccountRepository>().LeaseDueAsync(DateTimeOffset.UtcNow, default));
        }
        using var repeat = await TrainerPatchAsync(actor, original.Id, "deactivate");
        Assert.Equal(HttpStatusCode.NoContent, repeat.StatusCode);
        using (var scope = app.Factory.Services.CreateScope())
        {
            var user = await scope.ServiceProvider.GetRequiredService<StaticXDbContext>().Users.SingleAsync(u => u.Id == original.Id);
            Assert.Equal((rotated, concurrency), (user.SecurityStamp, user.ConcurrencyStamp));
        }
        using var enabled = await TrainerPatchAsync(actor, original.Id, "reactivate");
        Assert.Equal(HttpStatusCode.NoContent, enabled.StatusCode);
        Assert.Empty(await enabled.Content.ReadAsStringAsync());
        Assert.True(enabled.Headers.CacheControl?.NoStore);
        using var repeatEnabled = await TrainerPatchAsync(actor, original.Id, "reactivate");
        Assert.Equal(HttpStatusCode.NoContent, repeatEnabled.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await copied.GetAsync("/api/account/me")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await app.PostAsync(anonymous, "/api/account/reset-password", new { token = recovery, newPassword = "NewPassword2!" })).StatusCode);
        using (var scope = app.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StaticXDbContext>();
            var user = await db.Users.SingleAsync(u => u.Id == original.Id);
            Assert.True(user.IsEnabled);
            Assert.False(user.StudentAccessSuspended);
            Assert.Equal(rotated, user.SecurityStamp);
            Assert.NotEqual(concurrency, user.ConcurrencyStamp);
            Assert.True(original.PasswordHash == user.PasswordHash, "State transitions preserve the password hash.");
            Assert.Equal((original.Email, original.EmailConfirmed, original.GymId, original.Role, original.LockoutEnd, original.AccessFailedCount),
                (user.Email, user.EmailConfirmed, user.GymId, user.Role, user.LockoutEnd, user.AccessFailedCount));
            Assert.Equal(student, (await db.Alumnos.SingleAsync()).Id);
            Assert.True((await db.Alumnos.SingleAsync()).IsActive);
            Assert.Null((await db.Alumnos.SingleAsync()).AccountId);
            Assert.Empty(await db.ActivationInvitations.ToListAsync());
            Assert.Single(await db.PasswordRecoveryOutbox.ToListAsync());
        }
        Assert.Equal(HttpStatusCode.OK, (await app.LoginAsync(anonymous, gym, original.Email!, "CorrectPassword1!")).StatusCode);
    }

    [Fact]
    public async Task Trainers_PendingDisablePausesDeliveryActivationAndReissue_ReactivateRetainsUnexpiredToken()
    {
        await using var app = await TestApp.CreateAsync(activation: true);
        var gym = await app.CreateGymAsync();
        using var actor = await InvitationActorAsync(app, gym);
        using var invite = await app.PostAsync(actor, InvitationsRoute, new InviteAccountRequest("trainer-pending", "pending@example.invalid", AccountRole.Trainer));
        var id = (await invite.Content.ReadFromJsonAsync<AccountInvitationResponse>())!.AccountId;
        var token = await InvitationTokenAsync(app, id);
        using var scope = app.Factory.Services.CreateScope();
        var deliveries = scope.ServiceProvider.GetRequiredService<IActivationDeliveryRepository>();
        var lease = await deliveries.LeaseDueAsync(DateTimeOffset.UtcNow, default);
        Assert.NotNull(lease);
        Assert.Equal(HttpStatusCode.NoContent, (await TrainerPatchAsync(actor, id, "deactivate")).StatusCode);
        Assert.False(await deliveries.IsCurrentAsync(lease, DateTimeOffset.UtcNow, default));
        await deliveries.CompleteAsync(lease, DateTimeOffset.UtcNow, false, default);
        using var anonymous = app.Client();
        Assert.Equal(HttpStatusCode.BadRequest, (await app.PostAsync(anonymous, "/api/account/activation", new { token, password = "CorrectPassword1!" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await app.PostAsync(actor, InvitationsRoute, new InviteAccountRequest("paused", "pending@example.invalid", AccountRole.Trainer))).StatusCode);
        Assert.Null(await deliveries.LeaseDueAsync(DateTimeOffset.UtcNow, default));
        Assert.Equal(HttpStatusCode.NoContent, (await TrainerPatchAsync(actor, id, "reactivate")).StatusCode);
        using (var verify = app.Factory.Services.CreateScope())
        {
            var db = verify.ServiceProvider.GetRequiredService<StaticXDbContext>();
            var pending = await db.Users.SingleAsync(u => u.Id == id);
            Assert.True(pending.IsEnabled);
            Assert.False(pending.EmailConfirmed);
            Assert.False(pending.StudentAccessSuspended);
            Assert.Null(pending.PasswordHash);
            Assert.Null((await db.ActivationInvitations.SingleAsync()).ConsumedAt);
            Assert.Single(await db.ActivationOutbox.ToListAsync());
            Assert.Single(await db.TenantInvitationRequests.ToListAsync());
            Assert.NotNull(await verify.ServiceProvider.GetRequiredService<IActivationDeliveryRepository>().LeaseDueAsync(DateTimeOffset.UtcNow.AddMinutes(3), default));
        }
        Assert.Equal(token, await InvitationTokenAsync(app, id));
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.LoginAsync(anonymous, gym, "pending@example.invalid", "CorrectPassword1!")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await app.PostAsync(anonymous, "/api/account/activation", new { token, password = "CorrectPassword1!" })).StatusCode);
    }

    [Theory]
    [InlineData("activate")]
    [InlineData("reissue")]
    public async Task Trainers_ConcurrentDisableAndInvitationWriter_EndDisabledWithoutBackdoor(string operation)
    {
        await using var app = await TestApp.CreateAsync(activation: true, recoveryConcurrency: true);
        var gym = await app.CreateGymAsync();
        using var actor = await InvitationActorAsync(app, gym);
        using var invite = await app.PostAsync(actor, InvitationsRoute, new InviteAccountRequest("trainer-race", "pending@example.invalid", AccountRole.Trainer));
        var id = (await invite.Content.ReadFromJsonAsync<AccountInvitationResponse>())!.AccountId;
        var token = await InvitationTokenAsync(app, id);
        using var second = app.Client();
        if (operation == "reissue") Assert.Equal(HttpStatusCode.OK, (await app.LoginAsync(second, gym, "actor@example.invalid", "CorrectPassword1!")).StatusCode);
        var writer = operation == "activate" ? app.PostAsync(second, "/api/account/activation", new { token, password = "CorrectPassword1!" }) :
            app.PostAsync(second, InvitationsRoute, new InviteAccountRequest("trainer-race-second", "pending@example.invalid", AccountRole.Trainer));
        var results = await Task.WhenAll(writer, TrainerPatchAsync(actor, id, "deactivate"));
        Assert.Equal(HttpStatusCode.NoContent, results[1].StatusCode);
        Assert.Contains(results[0].StatusCode, new[] { HttpStatusCode.OK, operation == "activate" ? HttpStatusCode.BadRequest : HttpStatusCode.Conflict });
        using var verify = app.Factory.Services.CreateScope();
        var user = await verify.ServiceProvider.GetRequiredService<StaticXDbContext>().Users.SingleAsync(u => u.Id == id);
        Assert.False(user.IsEnabled);
        Assert.False(user.StudentAccessSuspended);
        if (operation == "activate") Assert.Equal(results[0].IsSuccessStatusCode, user.EmailConfirmed);
        else Assert.Null(user.PasswordHash);
        using var anonymous = app.Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.LoginAsync(anonymous, gym, "pending@example.invalid", "CorrectPassword1!")).StatusCode);
        foreach (var result in results) result.Dispose();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Trainers_CommitFailure_RollsBackEnablementAndBothStamps(bool initiallyEnabled)
    {
        var interceptor = new TenantCommitFailure();
        await using var app = await TestApp.CreateAsync(tenantCommitInterceptor: interceptor);
        var gym = await app.CreateGymAsync();
        using var actor = await InvitationActorAsync(app, gym);
        await SeedTrainerAsync(app, gym, "rollback", initiallyEnabled, true);
        var before = await app.LogoutStateAsync();
        interceptor.Fail = true;
        using var response = await TrainerPatchAsync(actor, "rollback", initiallyEnabled ? "deactivate" : "reactivate");
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.DoesNotContain("Isolated tenant", await response.Content.ReadAsStringAsync());
        Assert.True(before == await app.LogoutStateAsync(), "Failed commit must roll back enablement and stamps together.");
    }

    private static async Task SeedTrainerAsync(TestApp app, int gym, string id, bool enabled, bool confirmed)
    {
        using var scope = app.Factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityAccount>>();
        var account = new IdentityAccount { Id = id, UserName = id.Length <= 256 ? id : "long-id-user", GymId = gym, Role = AccountRole.Trainer,
            Email = id.Length <= 64 ? id + "@example.invalid" : "long-id@example.invalid", IsEnabled = enabled, EmailConfirmed = confirmed, LockoutEnabled = true };
        var result = confirmed ? await users.CreateAsync(account, "CorrectPassword1!") : await users.CreateAsync(account);
        Assert.True(result.Succeeded);
    }

    private static async Task<HttpResponseMessage> TrainerPatchAsync(HttpClient actor, string id, string action)
    {
        var token = (await actor.GetFromJsonAsync<JsonElement>("/api/account/csrf")).GetProperty("token").GetString();
        using var request = new HttpRequestMessage(HttpMethod.Patch, $"/api/trainers/{Uri.EscapeDataString(id)}/{action}");
        request.Headers.Add("X-CSRF-TOKEN", token);
        return await actor.SendAsync(request);
    }
}
