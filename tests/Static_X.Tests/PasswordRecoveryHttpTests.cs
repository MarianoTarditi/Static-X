using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using BLL;
using BLL.DTOs.Accounts;
using BLL.Interfaces;
using BLL.Services.Interfaces;
using DAL;
using DAL.Repositories.Interfaces;
using DAL.Repositories.Models;
using Entity;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Static_X.Tests;

public sealed partial class AccountHttpTests
{
    [Fact]
    public async Task ForgotPassword_AnonymousUnknownAccount_ReturnsUniformAccepted()
    {
        await using var app = await TestApp.CreateAsync(activation: true);
        using var client = app.Client();
        using var response = await app.PostAsync(client, "/api/account/forgot-password", new { email = "unknown@example.invalid" });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("{\"message\":\"If an eligible account exists, password reset instructions will be sent.\"}", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("eligible", 1)]
    [InlineData("locked", 1)]
    [InlineData("unconfirmed", 0)]
    [InlineData("no-password", 0)]
    [InlineData("wrong-scope", 0)]
    [InlineData("unknown", 0)]
    public async Task ForgotPassword_EligibilityIsPrivateAndDoesNotChangeIdentity(string scenario, int jobs)
    {
        await using var app = await TestApp.CreateAsync(activation: true);
        var gym = await app.CreateGymAsync();
        var account = await app.CreateAccountAsync(gym, "member@example.invalid", "CorrectPassword1!", scenario != "unconfirmed");
        using (var scope = app.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StaticXDbContext>();
            var user = await db.Users.SingleAsync(u => u.Id == account.Id);
            if (scenario == "no-password") user.PasswordHash = null;
            if (scenario == "locked") { user.LockoutEnd = DateTimeOffset.UtcNow.AddHours(1); user.AccessFailedCount = 4; }
            await db.SaveChangesAsync();
        }
        var before = await app.LogoutStateAsync();
        using var client = app.Client();
        using var response = await app.PostAsync(client, "/api/account/forgot-password", new
        { email = scenario == "unknown" ? "unknown@example.invalid" : "member@example.invalid", gymId = scenario == "wrong-scope" ? (int?)null : gym });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("{\"message\":\"If an eligible account exists, password reset instructions will be sent.\"}", await response.Content.ReadAsStringAsync());
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        Assert.True(before == await app.LogoutStateAsync(), "Forgot must not mutate identity, lockout or activation state.");
        using var check = app.Factory.Services.CreateScope();
        Assert.Equal(jobs, await check.ServiceProvider.GetRequiredService<StaticXDbContext>().PasswordRecoveryOutbox.CountAsync());
    }

    [Theory]
    [InlineData("forgot-password", "missing")]
    [InlineData("forgot-password", "invalid")]
    [InlineData("reset-password", "missing")]
    [InlineData("reset-password", "invalid")]
    public async Task Recovery_AnonymousMissingOrInvalidCsrf_Returns400WithoutMutation(string route, string csrf)
    {
        await using var app = await TestApp.CreateAsync(activation: true);
        using var client = app.Client();
        var before = await app.LogoutStateAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/account/" + route)
        { Content = JsonContent.Create(new { email = "unknown@example.invalid", token = "invalid", newPassword = "NewPassword2!" }) };
        if (csrf == "invalid") request.Headers.Add("X-CSRF-TOKEN", "invalid");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(before == await app.LogoutStateAsync(), "CSRF rejection must precede state changes.");
        using var scope = app.Factory.Services.CreateScope();
        Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<StaticXDbContext>().PasswordRecoveryOutbox.CountAsync());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task ForgotPassword_NonpositiveGym_Returns400(int gymId)
    {
        await using var app = await TestApp.CreateAsync(activation: true);
        using var client = app.Client();
        using var response = await app.PostAsync(client, "/api/account/forgot-password", new { email = "a@example.invalid", gymId });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ForgotPassword_RateLimitCombinesEligibleAndUnknownByIpOnlyAndDoesNotLimitLoginOrReset()
    {
        await using var app = await TestApp.CreateAsync(activation: true);
        var gym = await app.CreateGymAsync();
        await app.CreateAccountAsync(gym, "member@example.invalid", "CorrectPassword1!", true);
        using var client = app.Client();
        client.DefaultRequestHeaders.Add("X-Test-Remote-IP", "192.0.2.1");
        for (var i = 0; i < 5; i++)
        {
            using var response = await app.PostAsync(client, "/api/account/forgot-password", new
            { email = i % 2 == 0 ? "member@example.invalid" : "unknown@example.invalid", gymId = gym });
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        }
        using var rejected = await app.PostAsync(client, "/api/account/forgot-password", new { email = "new@example.invalid", gymId = gym });
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.InRange(rejected.Headers.RetryAfter!.Delta!.Value.TotalSeconds, 1, 900);
        using var other = app.Client();
        other.DefaultRequestHeaders.Add("X-Test-Remote-IP", "192.0.2.2");
        using var allowed = await app.PostAsync(other, "/api/account/forgot-password", new { email = "unknown@example.invalid" });
        Assert.Equal(HttpStatusCode.Accepted, allowed.StatusCode);
        using var login = await app.LoginAsync(client, gym, "member@example.invalid", "CorrectPassword1!");
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        using var reset = await app.PostAsync(other, "/api/account/reset-password", new { token = "invalid", newPassword = "NewPassword2!" });
        Assert.Equal(HttpStatusCode.BadRequest, reset.StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForgotPassword_MissingKeys_ReturnsUniform503BeforeLookup(bool existing)
    {
        await using var app = await TestApp.CreateAsync();
        if (existing) await app.CreateGlobalAsync("member@example.invalid", "CorrectPassword1!");
        using var client = app.Client();
        using var response = await app.PostAsync(client, "/api/account/forgot-password", new { email = "member@example.invalid" });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("Password recovery is unavailable.", (await response.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ProblemDetails>())!.Title);
    }

    [Fact]
    public async Task Recovery_SameEmailGlobalAndTwoGyms_SelectsOnlyPersistedScopeWithoutFallback()
    {
        await using var app = await TestApp.CreateAsync(activation: true);
        var global = await app.CreateGlobalAsync("same@example.invalid", "CorrectPassword1!");
        var firstGym = await app.CreateGymAsync();
        var secondGym = await app.CreateGymAsync();
        var first = await app.CreateAccountAsync(firstGym, "same@example.invalid", "CorrectPassword1!", true);
        var second = await app.CreateAccountAsync(secondGym, "same@example.invalid", "CorrectPassword1!", true);
        using var client = app.Client();
        var ids = new[] { global, first.Id, second.Id };
        var gyms = new int?[] { null, firstGym, secondGym };
        for (var index = 0; index < 3; index++)
        {
            var before = await ReadPasswordAccountsAsync(app);
            var token = await RecoveryLinkAsync(app, client, "same@example.invalid", gyms[index]);
            using var reset = await app.PostAsync(client, "/api/account/reset-password", new
            { token, newPassword = "NewPassword2!", gymId = gyms[(index + 1) % 3], accountId = ids[(index + 1) % 3], role = 4 });
            Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
            var after = await ReadPasswordAccountsAsync(app);
            Assert.True(before[ids[index]].SecurityStamp != after[ids[index]].SecurityStamp, "Selected account stamp must rotate.");
            foreach (var other in ids.Where(id => id != ids[index]))
                Assert.True(JsonSerializer.Serialize(before[other]) == JsonSerializer.Serialize(after[other]), "Other scope must remain unchanged.");
        }
        using var wrong = await app.PostAsync(client, "/api/account/forgot-password", new { email = "same@example.invalid", gymId = secondGym + 100 });
        Assert.Equal(HttpStatusCode.Accepted, wrong.StatusCode);
        using var check = app.Factory.Services.CreateScope();
        Assert.Equal(3, await check.ServiceProvider.GetRequiredService<StaticXDbContext>().PasswordRecoveryOutbox.CountAsync());
    }

    [Theory]
    [InlineData(AccountRole.SuperAdmin)]
    [InlineData(AccountRole.GymAdmin)]
    [InlineData(AccountRole.Trainer)]
    [InlineData(AccountRole.Student)]
    public async Task ResetPassword_AllRoles_RevokesSessionsAtSameClockWithoutAutoLoginAndPreservesOtherAccount(AccountRole role)
    {
        var clock = new RecoveryClock();
        await using var app = await TestApp.CreateAsync(activation: true, clock: clock);
        int? gym = role == AccountRole.SuperAdmin ? null : await app.CreateGymAsync();
        var id = gym is null ? await app.CreateGlobalAsync("same@example.invalid", "CorrectPassword1!") :
            (await app.CreateAccountAsync(gym.Value, "same@example.invalid", "CorrectPassword1!", true, role)).Id;
        var otherGym = await app.CreateGymAsync();
        var other = await app.CreateAccountAsync(otherGym, "same@example.invalid", "OtherPassword1!", true);
        using var current = app.Client();
        using var second = app.Client();
        using var unaffected = app.Client();
        using var anonymous = app.Client();
        using var login = await app.LoginAsync(current, gym, "same@example.invalid", "CorrectPassword1!");
        using var login2 = await app.LoginAsync(second, gym, "same@example.invalid", "CorrectPassword1!");
        using var otherLogin = await app.LoginAsync(unaffected, otherGym, "same@example.invalid", "OtherPassword1!");
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.Equal(HttpStatusCode.OK, login2.StatusCode);
        Assert.Equal(HttpStatusCode.OK, otherLogin.StatusCode);
        var options = app.Factory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(IdentityConstants.ApplicationScheme);
        var cookie = Assert.Single(login.Headers.GetValues("Set-Cookie"), value => value.StartsWith(options.Cookie.Name + "="));
        using var copied = app.Factory.CreateClient(new WebApplicationFactoryClientOptions
        { BaseAddress = new Uri("https://localhost"), HandleCookies = false, AllowAutoRedirect = false });
        copied.DefaultRequestHeaders.Add("Cookie", cookie.Split(';')[0]);
        var token = await RecoveryLinkAsync(app, anonymous, "same@example.invalid", gym);
        var before = await ReadPasswordAccountsAsync(app);
        using var reset = await app.PostAsync(anonymous, "/api/account/reset-password", new { token, newPassword = " NewPassword2! " });
        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
        Assert.Equal(string.Empty, await reset.Content.ReadAsStringAsync());
        Assert.True(reset.Headers.CacheControl?.NoStore);
        Assert.Equal("no-referrer", Assert.Single(reset.Headers.GetValues("Referrer-Policy")));
        Assert.False(reset.Headers.Contains("Set-Cookie"));
        foreach (var session in new[] { current, second, copied, anonymous })
            Assert.Equal(HttpStatusCode.Unauthorized, (await session.GetAsync("/api/account/me")).StatusCode);
        Assert.Equal(other.Id, (await unaffected.GetFromJsonAsync<AccountResponse>("/api/account/me"))!.Id);
        var after = await ReadPasswordAccountsAsync(app);
        Assert.True(before[id].PasswordHash != after[id].PasswordHash, "Password hash must change.");
        Assert.True(before[id].SecurityStamp != after[id].SecurityStamp, "Security stamp must rotate.");
        Assert.True(JsonSerializer.Serialize(before[other.Id]) == JsonSerializer.Serialize(after[other.Id]), "Other identity must not change.");
        using var old = await app.LoginAsync(anonymous, gym, "same@example.invalid", "CorrectPassword1!");
        Assert.Equal(HttpStatusCode.Unauthorized, old.StatusCode);
        using var trimmed = await app.LoginAsync(anonymous, gym, "same@example.invalid", "NewPassword2!");
        Assert.Equal(HttpStatusCode.Unauthorized, trimmed.StatusCode);
        using var fresh = await app.LoginAsync(anonymous, gym, "same@example.invalid", " NewPassword2! ");
        Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResetPassword_AuthenticatedCaller_ExpiresOnlyMatchingAccountCookie(bool differentAccount)
    {
        await using var app = await TestApp.CreateAsync(activation: true);
        var id = await app.CreateGlobalAsync("target@example.invalid", "CorrectPassword1!");
        if (differentAccount) await app.CreateGlobalAsync("other@example.invalid", "CorrectPassword1!");
        using var caller = app.Client();
        using var anonymous = app.Client();
        using var login = await app.LoginAsync(caller, null, differentAccount ? "other@example.invalid" : "target@example.invalid", "CorrectPassword1!");
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var token = await RecoveryLinkAsync(app, anonymous, "target@example.invalid", null);
        using var reset = await app.PostAsync(caller, "/api/account/reset-password", new { token, newPassword = "NewPassword2!" });
        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
        Assert.Equal(!differentAccount, reset.Headers.Contains("Set-Cookie"));
        Assert.Equal(differentAccount ? HttpStatusCode.OK : HttpStatusCode.Unauthorized, (await caller.GetAsync("/api/account/me")).StatusCode);
    }

    [Theory]
    [InlineData("tampered")]
    [InlineData("activation")]
    [InlineData("expired")]
    [InlineData("expired-after")]
    [InlineData("revoked")]
    [InlineData("stale")]
    [InlineData("weak")]
    [InlineData("empty-password")]
    [InlineData("null-token")]
    [InlineData("oversized-token")]
    [InlineData("malformed-envelope")]
    [InlineData("missing-request")]
    [InlineData("invalid-identity-token")]
    public async Task ResetPassword_InvalidInputsAreGenericAndDoNotMutateIdentityOrConsumeRequest(string scenario)
    {
        var clock = new RecoveryClock();
        await using var app = await TestApp.CreateAsync(activation: true, clock: clock);
        var id = await app.CreateGlobalAsync("member@example.invalid", "CorrectPassword1!");
        using var client = app.Client();
        string? token = await RecoveryLinkAsync(app, client, "member@example.invalid", null);
        if (scenario == "tampered") token = token[..^10] + "AAAAAAAAAA";
        if (scenario == "activation") token = (await app.ProvisionAsync()).Token;
        if (scenario == "expired") clock.Advance(TimeSpan.FromMinutes(30));
        if (scenario == "expired-after") clock.Advance(TimeSpan.FromMinutes(30) + TimeSpan.FromTicks(1));
        if (scenario is "revoked" or "stale")
        {
            using var setup = app.Factory.Services.CreateScope();
            var db = setup.ServiceProvider.GetRequiredService<StaticXDbContext>();
            if (scenario == "revoked") await db.PasswordRecoveryOutbox.ExecuteUpdateAsync(s => s.SetProperty(o => o.RevokedAt, clock.GetUtcNow().UtcDateTime));
            else Assert.True((await setup.ServiceProvider.GetRequiredService<UserManager<GymAccount>>()
                .ChangePasswordAsync((await db.Users.FindAsync(id))!, "CorrectPassword1!", "ChangedPassword3!")).Succeeded);
        }
        if (scenario == "null-token") token = null;
        if (scenario == "oversized-token") token = new string('x', 8193);
        if (scenario is "malformed-envelope" or "missing-request" or "invalid-identity-token")
        {
            var protector = app.Factory.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("Static-X.PasswordRecovery.v1");
            var payload = protector.Unprotect(token!);
            token = protector.Protect(scenario == "malformed-envelope" ? "malformed" : scenario == "missing-request"
                ? Guid.NewGuid().ToString("N") + payload[32..] : payload[..33] + "invalid");
        }
        var before = await app.LogoutStateAsync();
        using var response = await app.PostAsync(client, "/api/account/reset-password", new
        { token, newPassword = scenario == "weak" ? "weak" : scenario == "empty-password" ? "" : "NewPassword2!" });
        await AssertInvalidResetAsync(response);
        Assert.True(before == await app.LogoutStateAsync(), "Rejected resets must not change Identity or activation state.");
        using var check = app.Factory.Services.CreateScope();
        Assert.Null((await check.ServiceProvider.GetRequiredService<StaticXDbContext>().PasswordRecoveryOutbox.SingleAsync()).ConsumedAt);
        if (scenario == "weak")
        {
            using var retry = await app.PostAsync(client, "/api/account/reset-password", new { token, newPassword = "NewPassword2!" });
            Assert.Equal(HttpStatusCode.NoContent, retry.StatusCode);
        }
    }

    [Fact]
    public async Task ResetPassword_ImmediatelyBeforeExpirySucceedsAndReplayIsGeneric400()
    {
        var clock = new RecoveryClock();
        await using var app = await TestApp.CreateAsync(activation: true, clock: clock);
        await app.CreateGlobalAsync("member@example.invalid", "CorrectPassword1!");
        using var client = app.Client();
        var token = await RecoveryLinkAsync(app, client, "member@example.invalid", null);
        clock.Advance(TimeSpan.FromMinutes(30) - TimeSpan.FromTicks(1));
        using var response = await app.PostAsync(client, "/api/account/reset-password", new { token, newPassword = "NewPassword2!" });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var before = await app.LogoutStateAsync();
        using var replay = await app.PostAsync(client, "/api/account/reset-password", new { token, newPassword = "AnotherPassword3!" });
        await AssertInvalidResetAsync(replay);
        Assert.True(before == await app.LogoutStateAsync(), "Replay must not change the new password.");
    }

    [Fact]
    public async Task Recovery_LockedAccountIsEligibleAndResetPreservesLockoutAndFailedCount()
    {
        await using var app = await TestApp.CreateAsync(activation: true);
        var id = await app.CreateGlobalAsync("locked@example.invalid", "CorrectPassword1!");
        var until = DateTimeOffset.UtcNow.AddHours(2);
        using (var scope = app.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StaticXDbContext>();
            var user = (await db.Users.FindAsync(id))!;
            user.LockoutEnd = until; user.AccessFailedCount = 4;
            await db.SaveChangesAsync();
        }
        using var client = app.Client();
        var token = await RecoveryLinkAsync(app, client, "locked@example.invalid", null);
        using var reset = await app.PostAsync(client, "/api/account/reset-password", new { token, newPassword = "NewPassword2!" });
        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
        var after = (await ReadPasswordAccountsAsync(app))[id];
        Assert.Equal(until, after.LockoutEnd);
        Assert.Equal(4, after.AccessFailedCount);
        using var login = await app.LoginAsync(client, null, "locked@example.invalid", "NewPassword2!");
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
    }

    [Fact]
    public async Task ForgotPassword_CoalescesUnsentButNeverInvalidatesAlreadySentLink()
    {
        await using var app = await TestApp.CreateAsync(activation: true);
        await app.CreateGlobalAsync("member@example.invalid", "CorrectPassword1!");
        using var client = app.Client();
        for (var i = 0; i < 2; i++)
        {
            using var response = await app.PostAsync(client, "/api/account/forgot-password", new { email = "member@example.invalid" });
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        }
        var sender = new RecoverySender();
        await DispatchRecoveryAsync(app, sender);
        var firstToken = sender.Token;
        var secondToken = await RecoveryLinkAsync(app, client, "member@example.invalid", null);
        using (var check = app.Factory.Services.CreateScope())
        {
            var rows = await check.ServiceProvider.GetRequiredService<StaticXDbContext>().PasswordRecoveryOutbox.ToListAsync();
            Assert.Equal(2, rows.Count);
            Assert.All(rows, row => { Assert.NotNull(row.SentAt); Assert.Equal(string.Empty, row.ProtectedPayload); Assert.Null(row.RevokedAt); });
        }
        using var reset = await app.PostAsync(client, "/api/account/reset-password", new { token = firstToken, newPassword = "NewPassword2!" });
        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
        using var invalidated = await app.PostAsync(client, "/api/account/reset-password", new { token = secondToken, newPassword = "AnotherPassword3!" });
        await AssertInvalidResetAsync(invalidated);
    }

    [Fact]
    public async Task ForgotPassword_DoesNotRevokeCurrentOrSecondSession()
    {
        await using var app = await TestApp.CreateAsync(activation: true);
        var id = await app.CreateGlobalAsync("member@example.invalid", "CorrectPassword1!");
        using var current = app.Client();
        using var second = app.Client();
        using var anonymous = app.Client();
        using var login = await app.LoginAsync(current, null, "member@example.invalid", "CorrectPassword1!");
        using var login2 = await app.LoginAsync(second, null, "member@example.invalid", "CorrectPassword1!");
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.Equal(HttpStatusCode.OK, login2.StatusCode);
        var before = await app.LogoutStateAsync();
        using var forgot = await app.PostAsync(anonymous, "/api/account/forgot-password", new { email = "member@example.invalid" });
        Assert.Equal(HttpStatusCode.Accepted, forgot.StatusCode);
        Assert.True(before == await app.LogoutStateAsync(), "Forgot must not update credentials or stamps.");
        foreach (var client in new[] { current, second })
            Assert.Equal(id, (await client.GetFromJsonAsync<AccountResponse>("/api/account/me"))!.Id);
    }

    [Fact]
    public async Task ResetPassword_ConcurrentRedemptionsAcrossScopesHaveExactlyOneWinner()
    {
        await using var app = await TestApp.CreateAsync(activation: true, recoveryConcurrency: true);
        await app.CreateGlobalAsync("member@example.invalid", "CorrectPassword1!");
        using var client = app.Client();
        var token = await RecoveryLinkAsync(app, client, "member@example.invalid", null);
        async Task<string?> RedeemAsync()
        {
            await using var scope = app.Factory.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<IAccountService>()
                .ResetAsync(new ResetPasswordRequest { Token = token, NewPassword = "NewPassword2!" });
        }
        var attempts = await Task.WhenAll(Task.Run(RedeemAsync), Task.Run(RedeemAsync));
        Assert.Single(attempts, id => id is not null);
        Assert.Single(attempts, id => id is null);
        using var check = app.Factory.Services.CreateScope();
        Assert.NotNull((await check.ServiceProvider.GetRequiredService<StaticXDbContext>().PasswordRecoveryOutbox.SingleAsync()).ConsumedAt);
    }

    private static async Task AssertInvalidResetAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(new[] { "status", "title" }, body.RootElement.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal("Invalid password reset request.", body.RootElement.GetProperty("title").GetString());
        Assert.Equal(400, body.RootElement.GetProperty("status").GetInt32());
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    private static async Task<string> RecoveryLinkAsync(TestApp app, HttpClient client, string email, int? gymId)
    {
        using var response = await app.PostAsync(client, "/api/account/forgot-password", new { email, gymId });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var sender = new RecoverySender();
        Assert.True(await DispatchRecoveryAsync(app, sender));
        Assert.Equal(1, sender.Calls);
        return sender.Token;
    }

    private static async Task<bool> DispatchRecoveryAsync(TestApp app, RecoverySender sender, CancellationToken cancellationToken = default)
    {
        await using var scope = app.Factory.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var configuration = RecoveryConfiguration(app);
        var dispatcher = new PasswordRecoveryDispatcher(services.GetRequiredService<IAccountRepository>(),
            services.GetRequiredService<IDataProtectionProvider>(), configuration, services.GetRequiredService<TimeProvider>(), sender);
        await dispatcher.CleanupAsync(cancellationToken);
        return await dispatcher.DispatchOneAsync(cancellationToken);
    }

    private static IConfigurationRoot RecoveryConfiguration(TestApp app) => new ConfigurationBuilder().AddInMemoryCollection(
        new Dictionary<string, string?>
        {
            ["Activation:KeyRingPath"] = app.Factory.Services.GetRequiredService<IConfiguration>()["Activation:KeyRingPath"],
            ["PasswordRecovery:FrontendUrl"] = "https://frontend.example.invalid/reset",
            ["Resend:ApiKey"] = "re_fake_never_sent", ["Resend:FromAddress"] = "sender@example.invalid"
        }).Build();

    private sealed class RecoverySender : IPasswordRecoveryEmailSender
    {
        public int Calls { get; private set; }
        public bool Accepted { get; set; } = true;
        public bool UnknownAcceptance { get; set; }
        public Action? BeforeSend { get; set; }
        public List<(string Key, string From, string To, string Link)> Attempts { get; } = [];
        public string Token => Uri.UnescapeDataString(new Uri(Attempts.Last().Link).Fragment["#reset-password=".Length..]);
        public Task<bool> SendAsync(string idempotencyKey, string from, string to, string link, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            Attempts.Add((idempotencyKey, from, to, link));
            BeforeSend?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            if (UnknownAcceptance) throw new IOException("private provider detail");
            return Task.FromResult(Accepted);
        }
    }

    private sealed class RecoveryClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan elapsed) => now += elapsed;
    }

    private sealed class RecoveryTestAddressFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, continuation) =>
            {
                if (IPAddress.TryParse(context.Request.Headers["X-Test-Remote-IP"], out var address))
                    context.Connection.RemoteIpAddress = address;
                await continuation();
            });
            next(app);
        };
    }
}
