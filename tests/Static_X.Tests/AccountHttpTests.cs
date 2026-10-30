using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using BLL.DTOs.Accounts;
using BLL.DTOs.Activations;
using BLL.DTOs.Gyms;
using BLL.Services.Interfaces;
using BLL.Interfaces;
using BLL;
using DAL.Repositories.Interfaces;
using Static_X.API;
using Static_X.API.Integrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using System.Text.Json;
using DAL;
using Entity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Static_X.Tests;

public sealed class AccountHttpTests
{
    [Theory]
    [InlineData(AccountRole.SuperAdmin)]
    [InlineData(AccountRole.GymAdmin)]
    [InlineData(AccountRole.Trainer)]
    [InlineData(AccountRole.Student)]
    public async Task Logout_AuthenticatedRole_ExpiresOnlyCurrentCookieWithoutChangingAccount(AccountRole role)
    {
        await using var app = await TestApp.CreateAsync();
        int? gym = role == AccountRole.SuperAdmin ? null : await app.CreateGymAsync();
        var accountId = gym is null
            ? await app.CreateGlobalAsync("logout@example.invalid", "CorrectPassword1!")
            : (await app.CreateAccountAsync(gym.Value, "logout@example.invalid", "CorrectPassword1!", true, role)).Id;
        using var client = app.Client();
        using var otherSession = app.Client();
        using var login = await app.LoginAsync(client, gym, "logout@example.invalid", "CorrectPassword1!");
        using var otherLogin = await app.LoginAsync(otherSession, gym, "logout@example.invalid", "CorrectPassword1!");
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.Equal(HttpStatusCode.OK, otherLogin.StatusCode);
        Assert.Equal(accountId, (await client.GetFromJsonAsync<AccountResponse>("/api/account/me"))!.Id);
        Assert.Equal(accountId, (await otherSession.GetFromJsonAsync<AccountResponse>("/api/account/me"))!.Id);
        using (var scope = app.Factory.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<StaticXDbContext>();
            var account = await context.Users.SingleAsync(u => u.Id == accountId);
            account.AccessFailedCount = 2;
            await context.SaveChangesAsync();
        }
        var before = await app.LogoutStateAsync();

        // PostAsync refreshes CSRF for the authenticated identity and sends no request body.
        using var logout = await app.PostAsync(client, "/api/account/logout", null);

        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.Equal(string.Empty, await logout.Content.ReadAsStringAsync());
        Assert.True(logout.Headers.CacheControl?.NoStore);
        var cookieOptions = app.Factory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(IdentityConstants.ApplicationScheme);
        var cookie = Assert.Single(Microsoft.Net.Http.Headers.SetCookieHeaderValue.ParseList(
            logout.Headers.GetValues("Set-Cookie").ToList()), value => value.Name.Value == cookieOptions.Cookie.Name);
        Assert.True(string.IsNullOrEmpty(cookie.Value.Value));
        Assert.True(cookie.Expires < DateTimeOffset.UnixEpoch.AddYears(1));
        using var me = await client.GetAsync("/api/account/me");
        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
        Assert.Null(me.Headers.Location);
        Assert.Equal(accountId, (await otherSession.GetFromJsonAsync<AccountResponse>("/api/account/me"))!.Id);
        Assert.True(before == await app.LogoutStateAsync(), "Logout must not change persisted account or activation state.");

        // LoginAsync obtains a fresh anonymous CSRF token after logout; no automatic sign-in occurs.
        using var relogin = await app.LoginAsync(client, gym, "logout@example.invalid", "CorrectPassword1!");
        Assert.Equal(HttpStatusCode.OK, relogin.StatusCode);
        Assert.Equal(accountId, (await client.GetFromJsonAsync<AccountResponse>("/api/account/me"))!.Id);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("invalid")]
    [InlineData("anonymous-token")]
    public async Task Logout_MissingInvalidOrPreLoginCsrf_RejectsWithoutSigningOut(string scenario)
    {
        await using var app = await TestApp.CreateAsync();
        var gym = await app.CreateGymAsync();
        var account = await app.CreateAccountAsync(gym, "logout@example.invalid", "CorrectPassword1!", true);
        using var client = app.Client();
        var anonymousCsrf = await client.GetFromJsonAsync<CsrfToken>("/api/account/csrf");
        using var login = await app.LoginAsync(client, gym, "logout@example.invalid", "CorrectPassword1!");
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var before = await app.LogoutStateAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/account/logout");
        if (scenario != "missing")
            request.Headers.Add("X-CSRF-TOKEN", scenario == "invalid" ? "invalid-token" : anonymousCsrf!.Token);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        Assert.Equal(account.Id, (await client.GetFromJsonAsync<AccountResponse>("/api/account/me"))!.Id);
        Assert.True(before == await app.LogoutStateAsync(), "Rejected logout must not change persisted state.");
    }

    [Fact]
    public async Task Logout_AnonymousPost_ReturnsUnauthorizedEvenWithoutCsrf()
    {
        await using var app = await TestApp.CreateAsync();
        using var client = app.Client();
        using var response = await client.PostAsync("/api/account/logout", null);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        using var withCsrf = await app.PostAsync(client, "/api/account/logout", null);
        Assert.Equal(HttpStatusCode.Unauthorized, withCsrf.StatusCode);
    }

    [Fact]
    public async Task Logout_Get_ReturnsMethodNotAllowedAndKeepsSession()
    {
        await using var app = await TestApp.CreateAsync();
        var gym = await app.CreateGymAsync();
        var account = await app.CreateAccountAsync(gym, "logout@example.invalid", "CorrectPassword1!", true);
        using var client = app.Client();
        using var login = await app.LoginAsync(client, gym, "logout@example.invalid", "CorrectPassword1!");
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        using var response = await client.GetAsync("/api/account/logout");

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        Assert.Equal(account.Id, (await client.GetFromJsonAsync<AccountResponse>("/api/account/me"))!.Id);
    }

    [Fact]
    public async Task Startup_WebApplicationFactoryHostSettings_BuildsHttpHost()
    {
        await using var app = await TestApp.CreateAsync();
        using var response = await app.Client().GetAsync("/api/account/csrf");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Login_ConfirmedAccountInCorrectGym_IssuesSecureCookieAndReturnsCurrentAccount()
    {
        await using var app = await TestApp.CreateAsync();
        var gym = await app.CreateGymAsync();
        var account = await app.CreateAccountAsync(gym, "member@example.com", "CorrectPassword1!", true);
        var client = app.Client();
        var response = await app.LoginAsync(client, gym, "member@example.com", "CorrectPassword1!");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"), value => value.StartsWith(".AspNetCore.Identity.Application="));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        var me = await client.GetAsync("/api/account/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        var identity = await me.Content.ReadFromJsonAsync<AccountResponse>();
        Assert.Equal(account.Id, identity?.Id);
        Assert.Equal(gym, identity?.GymId);
        Assert.Equal(AccountRole.Student, identity?.Role);
    }

    [Fact]
    public async Task Login_SameEmailOtherGymOrWrongPassword_ReturnsIndistinguishableUnauthorized()
    {
        await using var app = await TestApp.CreateAsync();
        var first = await app.CreateGymAsync();
        var second = await app.CreateGymAsync();
        await app.CreateAccountAsync(first, "same@example.com", "FirstPassword1!", true);
        await app.CreateAccountAsync(second, "same@example.com", "SecondPassword1!", true);
        var client = app.Client();
        var wrongGym = await app.LoginAsync(client, second, "same@example.com", "FirstPassword1!");
        var unknown = await app.LoginAsync(client, second, "unknown@example.com", "FirstPassword1!");

        Assert.Equal(HttpStatusCode.Unauthorized, wrongGym.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.Equal(await unknown.Content.ReadAsStringAsync(), await wrongGym.Content.ReadAsStringAsync());
        Assert.False(wrongGym.Headers.Contains("Set-Cookie"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/account/me")).StatusCode);
    }

    [Fact]
    public async Task Login_UnconfirmedStudentAndAdmin_DeniedDespiteCorrectPassword()
    {
        await using var app = await TestApp.CreateAsync();
        var gym = await app.CreateGymAsync();
        await app.CreateAccountAsync(gym, "student@example.com", "CorrectPassword1!", false);
        await app.CreateAccountAsync(gym, "admin@example.com", "CorrectPassword1!", false, AccountRole.GymAdmin);
        var client = app.Client();

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await app.LoginAsync(client, gym, "student@example.com", "CorrectPassword1!")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await app.LoginAsync(client, gym, "admin@example.com", "CorrectPassword1!")).StatusCode);
    }

    [Fact]
    public async Task Login_FiveWrongPasswords_LocksAccountAndDeniesCorrectPassword()
    {
        await using var app = await TestApp.CreateAsync();
        var gym = await app.CreateGymAsync();
        var account = await app.CreateAccountAsync(gym, "locked@example.com", "CorrectPassword1!", true);
        var client = app.Client();
        for (var attempt = 0; attempt < 5; attempt++)
            Assert.Equal(HttpStatusCode.Unauthorized,
                (await app.LoginAsync(client, gym, "locked@example.com", "WrongPassword1!")).StatusCode);

        using var scope = app.Factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<GymAccount>>();
        var user = await users.FindByIdAsync(account.Id);
        Assert.NotNull(user);
        Assert.True(await users.IsLockedOutAsync(user));
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await app.LoginAsync(client, gym, "locked@example.com", "CorrectPassword1!")).StatusCode);
    }

    [Fact]
    public async Task Me_SignedCookieWithForgedGymClaim_IsRejected()
    {
        await using var app = await TestApp.CreateAsync();
        var first = await app.CreateGymAsync();
        var second = await app.CreateGymAsync();
        var account = await app.CreateAccountAsync(first, "member@example.com", "CorrectPassword1!", true);
        var cookieOptions = app.Factory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(IdentityConstants.ApplicationScheme);
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, account.Id),
            new Claim("gym_id", second.ToString()),
            new Claim(ClaimTypes.Role, AccountRole.Student.ToString())
        }, IdentityConstants.ApplicationScheme));
        var ticket = new AuthenticationTicket(principal, IdentityConstants.ApplicationScheme);
        var cookie = cookieOptions.TicketDataFormat.Protect(ticket);
        var client = app.Client();
        client.DefaultRequestHeaders.Add("Cookie", $".AspNetCore.Identity.Application={cookie}");

        var response = await client.GetAsync("/api/account/me");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(response.Headers.Location);
    }

    [Fact]
    public async Task Me_PreviousCookieAfterRoleChange_IsRejected()
    {
        await using var app = await TestApp.CreateAsync();
        var gym = await app.CreateGymAsync();
        var account = await app.CreateAccountAsync(gym, "member@example.com", "CorrectPassword1!", true);
        var client = app.Client();
        Assert.Equal(HttpStatusCode.OK,
            (await app.LoginAsync(client, gym, "member@example.com", "CorrectPassword1!")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/account/me")).StatusCode);

        using (var scope = app.Factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<GymAccount>>();
            var user = await users.FindByIdAsync(account.Id);
            Assert.NotNull(user);
            user.Role = AccountRole.Trainer;
            Assert.True((await users.UpdateAsync(user)).Succeeded);
        }

        var response = await client.GetAsync("/api/account/me");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(response.Headers.Location);
    }

    [Fact]
    public async Task Me_PreviousCookieAfterEmailConfirmationRevoked_IsRejected()
    {
        await using var app = await TestApp.CreateAsync();
        var gym = await app.CreateGymAsync();
        var account = await app.CreateAccountAsync(gym, "member@example.com", "CorrectPassword1!", true);
        var client = app.Client();
        Assert.Equal(HttpStatusCode.OK,
            (await app.LoginAsync(client, gym, "member@example.com", "CorrectPassword1!")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/account/me")).StatusCode);

        using (var scope = app.Factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<GymAccount>>();
            var user = await users.FindByIdAsync(account.Id);
            Assert.NotNull(user);
            user.EmailConfirmed = false;
            Assert.True((await users.UpdateAsync(user)).Succeeded);
        }

        var response = await client.GetAsync("/api/account/me");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(response.Headers.Location);
    }

    [Fact]
    public async Task Login_PresentButMismatchedCsrfToken_IsRejectedWithoutIssuingCookie()
    {
        await using var app = await TestApp.CreateAsync();
        var gym = await app.CreateGymAsync();
        await app.CreateAccountAsync(gym, "member@example.com", "CorrectPassword1!", true);
        var client = app.Client();
        var csrfResponse = await client.GetAsync("/api/account/csrf");
        Assert.Equal(HttpStatusCode.OK, csrfResponse.StatusCode);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/account/login")
        {
            Content = JsonContent.Create(new LoginRequest
            {
                GymId = gym, Email = "member@example.com", Password = "CorrectPassword1!"
            })
        };
        request.Headers.Add("X-CSRF-TOKEN", "not-the-issued-request-token");

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/account/me")).StatusCode);
    }

    [Fact]
    public async Task Login_MissingCsrfTokenRejected_AndPublicRegistrationNotMapped()
    {
        await using var app = await TestApp.CreateAsync();
        var client = app.Client();
        var response = await client.PostAsJsonAsync("/api/account/login",
            new LoginRequest { GymId = 1, Email = "member@example.com", Password = "not-a-real-password" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.PostAsJsonAsync("/register", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.PostAsJsonAsync("/api/account/register", new { })).StatusCode);
    }

    [Fact]
    public async Task Activation_AnonymousPostRequiresCsrfAndDoesNotSignIn()
    {
        await using var app = await TestApp.CreateAsync(activation: true);
        var otherGym = await app.CreateGymAsync();
        var (gym, token) = await app.ProvisionAsync();
        Assert.NotEqual(otherGym, gym);
        var client = app.Client();
        var body = new ActivateAccountRequest { Token = token, Password = "StrongPassword1!" };
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync("/api/account/activation", body)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await app.LoginAsync(client, gym, "admin@example.com", "StrongPassword1!")).StatusCode);

        var csrf = await client.GetFromJsonAsync<CsrfToken>("/api/account/csrf");
        Assert.NotNull(csrf);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/account/activation")
        { Content = JsonContent.Create(new { body.Token, body.Password, gymId = otherGym, role = 4, accountId = "untrusted" }) };
        request.Headers.Add("X-CSRF-TOKEN", csrf.Token);
        var result = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.Equal($"{{\"gymId\":{gym}}}", await result.Content.ReadAsStringAsync());
        var activated = await result.Content.ReadFromJsonAsync<ActivationResponse>();
        Assert.NotNull(activated);
        using (var scope = app.Factory.Services.CreateScope())
        {
            var persisted = await scope.ServiceProvider.GetRequiredService<StaticXDbContext>()
                .Users.AsNoTracking().SingleAsync();
            Assert.Equal(persisted.GymId, activated.GymId);
            Assert.True(persisted.EmailConfirmed);
            Assert.NotNull(persisted.PasswordHash);
        }
        Assert.True(result.Headers.CacheControl?.NoStore);
        Assert.Equal("no-referrer", Assert.Single(result.Headers.GetValues("Referrer-Policy")));
        Assert.False(result.Headers.Contains("Set-Cookie"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/account/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await app.LoginAsync(client, activated.GymId, "admin@example.com", "StrongPassword1!")).StatusCode);
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("expired")]
    [InlineData("revoked")]
    [InlineData("replay")]
    public async Task Activation_InvalidExpiredRevokedOrReplayedToken_ReturnsGenericFailureWithoutContext(string scenario)
    {
        await using var app = await TestApp.CreateAsync(activation: true);
        var (gym, token) = await app.ProvisionAsync();
        using var client = app.Client();
        if (scenario == "replay")
        {
            using var success = await app.PostAsync(client, "/api/account/activation",
                new ActivateAccountRequest { Token = token, Password = "StrongPassword1!" });
            Assert.Equal(HttpStatusCode.OK, success.StatusCode);
        }
        else if (scenario is "expired" or "revoked")
        {
            using var scope = app.Factory.Services.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<StaticXDbContext>();
            var invitation = await context.ActivationInvitations.SingleAsync();
            if (scenario == "expired") invitation.ExpiresAt = DateTime.UtcNow.AddMinutes(-1);
            else invitation.RevokedAt = DateTime.UtcNow;
            await context.SaveChangesAsync();
        }

        using var result = await app.PostAsync(client, "/api/account/activation",
            new ActivateAccountRequest { Token = scenario == "invalid" ? new string('0', 64) : token, Password = "OtherPassword1!" });
        Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        var text = await result.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(text);
        Assert.Equal(400, document.RootElement.GetProperty("status").GetInt32());
        Assert.Equal("Invalid activation request.", document.RootElement.GetProperty("title").GetString());
        Assert.False(document.RootElement.TryGetProperty("gymId", out _));
        Assert.DoesNotContain("admin@example.com", text);
        Assert.DoesNotContain(token, text);
        Assert.DoesNotContain("Password", text);
        Assert.False(result.Headers.Contains("Set-Cookie"));
        Assert.True(result.Headers.CacheControl?.NoStore);
        Assert.Equal("no-referrer", Assert.Single(result.Headers.GetValues("Referrer-Policy")));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/account/me")).StatusCode);
        using var verification = app.Factory.Services.CreateScope();
        var persisted = await verification.ServiceProvider.GetRequiredService<StaticXDbContext>().Users.AsNoTracking().SingleAsync();
        Assert.Equal(gym, persisted.GymId);
        Assert.Equal(scenario == "replay", persisted.EmailConfirmed);
        if (scenario != "replay") Assert.Null(persisted.PasswordHash);
        else Assert.True(await verification.ServiceProvider.GetRequiredService<UserManager<GymAccount>>()
            .CheckPasswordAsync(persisted, "StrongPassword1!"));
    }

    private sealed record CsrfToken(string Token);

    [Fact]
    public async Task Login_GlobalAndTenantSameEmail_SelectsOnlyExplicitScopeAndMeHasNullGym()
    {
        await using var app = await TestApp.CreateAsync();
        var gym = await app.CreateGymAsync();
        await app.CreateAccountAsync(gym, "same@example.com", "TenantPassword1!", true);
        var global = await app.CreateGlobalAsync("same@example.com", "GlobalPassword1!");
        var client = app.Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.LoginAsync(client, null, "same@example.com", "TenantPassword1!")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.LoginAsync(client, gym, "same@example.com", "GlobalPassword1!")).StatusCode);
        using var login = await app.LoginAsync(client, null, "same@example.com", "GlobalPassword1!");
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var me = await client.GetFromJsonAsync<AccountResponse>("/api/account/me");
        Assert.Equal(global, me!.Id);
        Assert.Null(me.GymId);
        Assert.Equal(AccountRole.SuperAdmin, me.Role);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/students")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await app.PostAsync(client, "/api/students", new { nombre = "Ana", apellido = "Smith", email = "a@example.com" })).StatusCode);
        using var tenant = app.Client();
        Assert.Equal(HttpStatusCode.OK, (await app.LoginAsync(tenant, gym, "same@example.com", "TenantPassword1!")).StatusCode);
        Assert.Equal(gym, (await tenant.GetFromJsonAsync<AccountResponse>("/api/account/me"))!.GymId);
    }

    [Fact]
    public async Task Login_OmittedGym_DoesNotFallBackToTenantAndIgnoresClientRole()
    {
        await using var app = await TestApp.CreateAsync();
        var gym = await app.CreateGymAsync();
        await app.CreateAccountAsync(gym, "tenant@example.com", "TenantPassword1!", true, AccountRole.GymAdmin);
        using var client = app.Client();
        using var response = await app.PostAsync(client, "/api/account/login", new
        { email = "tenant@example.com", password = "TenantPassword1!", role = "SuperAdmin" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task Login_GlobalUnconfirmedAndLockedAccounts_AreDenied()
    {
        await using var app = await TestApp.CreateAsync();
        await app.CreateGlobalAsync("pending@example.com", "GlobalPassword1!", confirmed: false);
        var lockedId = await app.CreateGlobalAsync("locked-global@example.com", "GlobalPassword1!");
        using var client = app.Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.LoginAsync(client, null, "pending@example.com", "GlobalPassword1!")).StatusCode);
        for (var attempt = 0; attempt < 5; attempt++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await app.LoginAsync(client, null, "locked-global@example.com", "WrongPassword1!")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.LoginAsync(client, null, "locked-global@example.com", "GlobalPassword1!")).StatusCode);
        using var scope = app.Factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<GymAccount>>();
        Assert.True(await users.IsLockedOutAsync((await users.FindByIdAsync(lockedId))!));
    }

    [Theory]
    [InlineData("global-with-gym")]
    [InlineData("tenant-as-global")]
    [InlineData("tenant-without-gym")]
    public async Task Me_ForgedGlobalScopeOrRole_IsRejected(string scenario)
    {
        await using var app = await TestApp.CreateAsync();
        var gym = await app.CreateGymAsync();
        var tenant = await app.CreateAccountAsync(gym, "tenant@example.com", "TenantPassword1!", true);
        var global = await app.CreateGlobalAsync("global@example.com", "GlobalPassword1!");
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, scenario == "global-with-gym" ? global : tenant.Id),
            new(ClaimTypes.Role, scenario == "tenant-without-gym" ? "Student" : "SuperAdmin")
        };
        if (scenario == "global-with-gym") claims.Add(new("gym_id", gym.ToString()));
        var options = app.Factory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(IdentityConstants.ApplicationScheme);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, IdentityConstants.ApplicationScheme)), IdentityConstants.ApplicationScheme);
        using var client = app.Client();
        client.DefaultRequestHeaders.Add("Cookie", ".AspNetCore.Identity.Application=" + options.TicketDataFormat.Protect(ticket));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/account/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.PostAsync(client, "/api/gyms/provision", new ProvisionGymRequest("forged", "Forged", "admin@example.com"))).StatusCode);
    }

    [Theory]
    [InlineData("anonymous")]
    [InlineData("GymAdmin")]
    [InlineData("Trainer")]
    [InlineData("Student")]
    public async Task ProvisionAndReissue_NonGlobalCaller_CannotMutate(string role)
    {
        await using var app = await TestApp.CreateAsync(activation: true);
        using var client = app.Client();
        if (role != "anonymous")
        {
            var gym = await app.CreateGymAsync();
            await app.CreateAccountAsync(gym, "tenant@example.com", "TenantPassword1!", true, Enum.Parse<AccountRole>(role));
            Assert.Equal(HttpStatusCode.OK, (await app.LoginAsync(client, gym, "tenant@example.com", "TenantPassword1!")).StatusCode);
        }
        var expected = role == "anonymous" ? HttpStatusCode.Unauthorized : HttpStatusCode.Forbidden;
        Assert.Equal(expected, (await app.PostAsync(client, "/api/gyms/provision", new ProvisionGymRequest("denied", "Denied", "admin@example.com"))).StatusCode);
        Assert.Equal(expected, (await app.PostAsync(client, "/api/gyms/1/administrators/unknown/invitation", new { })).StatusCode);
        using var scope = app.Factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<StaticXDbContext>();
        Assert.Equal(0, await context.ProvisioningRequests.CountAsync());
        Assert.Equal(0, await context.ActivationOutbox.CountAsync());
    }

    [Fact]
    public async Task ProvisionAndReissue_GlobalCaller_RequiresCsrfAndPreservesIdempotencyAndPendingActivation()
    {
        await using var app = await TestApp.CreateAsync(activation: true);
        var globalId = await app.CreateGlobalAsync("global@example.com", "GlobalPassword1!");
        using var client = app.Client();
        Assert.Equal(HttpStatusCode.OK, (await app.LoginAsync(client, null, "global@example.com", "GlobalPassword1!")).StatusCode);
        var body = new ProvisionGymRequest("http-1", "North", "admin@example.com");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/gyms/provision", body)).StatusCode);
        using var provision = await app.PostAsync(client, "/api/gyms/provision", body);
        Assert.Equal(HttpStatusCode.OK, provision.StatusCode);
        var gym = await provision.Content.ReadFromJsonAsync<int>();
        Assert.True(gym > 0);
        using var retry = await app.PostAsync(client, "/api/gyms/provision", body);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(gym, await retry.Content.ReadFromJsonAsync<int>());
        Assert.Equal(HttpStatusCode.Conflict, (await app.PostAsync(client, "/api/gyms/provision", new ProvisionGymRequest("http-1", "Other", "admin@example.com"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await app.PostAsync(client, "/api/gyms/provision", new ProvisionGymRequest("bad", "North", "not-email"))).StatusCode);
        using var scope = app.Factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<StaticXDbContext>();
        var account = await context.Users.AsNoTracking().SingleAsync(u => u.GymId == gym);
        Assert.Equal(AccountRole.GymAdmin, account.Role);
        Assert.False(account.EmailConfirmed);
        Assert.Null(account.PasswordHash);
        Assert.Equal(1, await context.Gimnasios.CountAsync());
        Assert.Equal(1, await context.ActivationOutbox.CountAsync());
        var route = $"/api/gyms/{gym}/administrators/{account.Id}/invitation";
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(route, new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.PostAsync(client, $"/api/gyms/{gym + 1}/administrators/{account.Id}/invitation", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.PostAsync(client, $"/api/gyms/{gym}/administrators/{globalId}/invitation", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await app.PostAsync(client, route, null)).StatusCode);
        var current = await context.ActivationInvitations.AsNoTracking().SingleAsync(i => i.RevokedAt == null);
        Assert.Equal(1, await context.ActivationInvitations.CountAsync(i => i.RevokedAt != null));
        var payload = await context.ActivationOutbox.AsNoTracking().SingleAsync(o => o.InvitationId == current.Id);
        var token = scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>().CreateProtector("Static-X.ActivationDelivery.v1", current.Id.ToString("N")).Unprotect(payload.ProtectedToken);
        using var anonymous = app.Client();
        using var activation = await app.PostAsync(anonymous, "/api/account/activation", new ActivateAccountRequest { Token = token, Password = "AdminPassword1!" });
        Assert.Equal(HttpStatusCode.OK, activation.StatusCode);
        Assert.Equal($"{{\"gymId\":{gym}}}", await activation.Content.ReadAsStringAsync());
        var activated = await activation.Content.ReadFromJsonAsync<ActivationResponse>();
        Assert.Equal(account.GymId, activated!.GymId);
        Assert.False(activation.Headers.Contains("Set-Cookie"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/account/me")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.PostAsync(client, route, new { })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await app.LoginAsync(anonymous, activated.GymId, "admin@example.com", "AdminPassword1!")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/api/students")).StatusCode);
        Assert.Null((await context.Users.AsNoTracking().SingleAsync(u => u.Id == globalId)).GymId);
    }

    [Theory]
    [InlineData("missing-config", 503)]
    [InlineData("persistence-error", 500)]
    public async Task ProvisionAndReissue_OperationalFailures_ReturnSanitizedErrorsWithoutWriting(string scenario, int status)
    {
        await using var app = await TestApp.CreateAsync(activation: scenario == "persistence-error", failPersistence: scenario == "persistence-error");
        await app.CreateGlobalAsync("global@example.com", "GlobalPassword1!");
        using var client = app.Client();
        Assert.Equal(HttpStatusCode.OK, (await app.LoginAsync(client, null, "global@example.com", "GlobalPassword1!")).StatusCode);
        using var provision = await app.PostAsync(client, "/api/gyms/provision", new ProvisionGymRequest("error", "Error", "admin@example.com"));
        using var reissue = await app.PostAsync(client, "/api/gyms/1/administrators/unknown/invitation", null);
        Assert.Equal((HttpStatusCode)status, provision.StatusCode);
        Assert.Equal((HttpStatusCode)status, reissue.StatusCode);
        Assert.DoesNotContain("private-database-detail", await provision.Content.ReadAsStringAsync());
        Assert.DoesNotContain("private-database-detail", await reissue.Content.ReadAsStringAsync());
        using var scope = app.Factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<StaticXDbContext>();
        Assert.Equal(0, await context.Gimnasios.CountAsync());
        Assert.Equal(0, await context.ActivationOutbox.CountAsync());
    }

    [Fact]
    public async Task GymQueries_Anonymous_RequiresAuthentication()
    {
        await using var app = await TestApp.CreateAsync();
        using var client = app.Client();
        using var gyms = await client.GetAsync("/api/gyms");
        Assert.Equal(HttpStatusCode.Unauthorized, gyms.StatusCode);
        Assert.True(gyms.Headers.CacheControl?.NoStore);
        using var pending = await client.GetAsync("/api/gyms/1/administrators/pending");
        Assert.Equal(HttpStatusCode.Unauthorized, pending.StatusCode);
        Assert.True(pending.Headers.CacheControl?.NoStore);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/gyms/not-an-integer/administrators/pending?page=0")).StatusCode);
    }

    [Fact]
    public async Task GymQueries_GlobalSession_ReturnsExactPagesWithoutCsrf()
    {
        await using var app = await TestApp.CreateAsync();
        var gym = await app.CreateGymAsync();
        await app.CreateGlobalAsync("query-global@example.invalid", "CorrectPassword1!");
        using var client = app.Client();
        Assert.Equal(HttpStatusCode.OK, (await app.LoginAsync(client, null, "query-global@example.invalid", "CorrectPassword1!")).StatusCode);
        using var gyms = await client.GetAsync("/api/gyms");
        Assert.Equal(HttpStatusCode.OK, gyms.StatusCode);
        Assert.True(gyms.Headers.CacheControl?.NoStore);
        Assert.Equal($"{{\"items\":[{{\"id\":{gym},\"nombre\":\"Test gym\"}}],\"page\":1,\"pageSize\":20,\"totalCount\":1}}", await gyms.Content.ReadAsStringAsync());
        using var pending = await client.GetAsync($"/api/gyms/{gym}/administrators/pending");
        Assert.Equal(HttpStatusCode.OK, pending.StatusCode);
        Assert.True(pending.Headers.CacheControl?.NoStore);
        Assert.Equal("{\"items\":[],\"page\":1,\"pageSize\":20,\"totalCount\":0}", await pending.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData(AccountRole.GymAdmin)]
    [InlineData(AccountRole.Trainer)]
    [InlineData(AccountRole.Student)]
    public async Task GymQueries_TenantRoles_CannotSelectTargetsOrSpoofGlobalRole(AccountRole role)
    {
        await using var app = await TestApp.CreateAsync();
        var gym = await app.CreateGymAsync();
        await app.CreateAccountAsync(gym, "tenant-query@example.invalid", "CorrectPassword1!", true, role);
        using var client = app.Client();
        Assert.Equal(HttpStatusCode.OK, (await app.LoginAsync(client, gym, "tenant-query@example.invalid", "CorrectPassword1!")).StatusCode);
        foreach (var route in new[] { "/api/gyms?role=SuperAdmin", $"/api/gyms/{gym}/administrators/pending?role=4",
            "/api/gyms/999/administrators/pending", "/api/gyms/invalid/administrators/pending?page=0" })
        {
            using var response = await client.GetAsync(route);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
        }
    }

    [Theory]
    [InlineData("page=0")]
    [InlineData("page=-1")]
    [InlineData("page=abc")]
    [InlineData("page=2147483648")]
    [InlineData("page=2147483647&pageSize=100")]
    [InlineData("page=21474838&pageSize=100")]
    [InlineData("pageSize=0")]
    [InlineData("pageSize=-1")]
    [InlineData("pageSize=101")]
    [InlineData("pageSize=abc")]
    [InlineData("pageSize=2147483648")]
    public async Task GymQueries_InvalidPagination_ReturnsGenericBadRequest(string query)
    {
        await using var app = await TestApp.CreateAsync();
        var gym = await app.CreateGymAsync();
        await app.CreateGlobalAsync("query-global@example.invalid", "CorrectPassword1!");
        using var client = app.Client();
        Assert.Equal(HttpStatusCode.OK, (await app.LoginAsync(client, null, "query-global@example.invalid", "CorrectPassword1!")).StatusCode);
        foreach (var route in new[] { "/api/gyms", $"/api/gyms/{gym}/administrators/pending" })
        {
            using var response = await client.GetAsync($"{route}?{query}");
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
            var problem = await response.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ProblemDetails>();
            Assert.Equal(400, problem?.Status);
            var text = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain("SqlException", text);
            Assert.DoesNotContain("AspNetUsers", text);
        }
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("abc")]
    [InlineData("2147483648")]
    public async Task GymQueries_InvalidGymSelector_ReturnsBadRequestRatherThanRouteNotFound(string selector)
    {
        await using var app = await TestApp.CreateAsync();
        await app.CreateGlobalAsync("query-global@example.invalid", "CorrectPassword1!");
        using var client = app.Client();
        Assert.Equal(HttpStatusCode.OK, (await app.LoginAsync(client, null, "query-global@example.invalid", "CorrectPassword1!")).StatusCode);
        using var response = await client.GetAsync($"/api/gyms/{selector}/administrators/pending");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal(400, (await response.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ProblemDetails>())?.Status);
    }

    [Fact]
    public async Task GymQueries_PersistedPendingAndPastEnd_ReturnExactSafeFieldsWithoutMutation()
    {
        await using var app = await TestApp.CreateAsync();
        var first = await app.CreateGymAsync();
        var second = await app.CreateGymAsync();
        await app.CreateGlobalAsync("query-global@example.invalid", "CorrectPassword1!");
        using (var scope = app.Factory.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<StaticXDbContext>();
            context.Users.Add(new GymAccount { Id = "opaque-pending", UserName = "opaque-pending", GymId = first,
                Role = AccountRole.GymAdmin, Email = "pending@example.invalid", NormalizedEmail = "PENDING@EXAMPLE.INVALID",
                SecurityStamp = "unchanged-stamp", ConcurrencyStamp = "unchanged-concurrency" });
            context.ProvisioningRequests.Add(new ProvisioningRequest { Key = "query-only", GymId = first, AccountId = "opaque-pending" });
            await context.SaveChangesAsync();
        }
        using var client = app.Client();
        Assert.Equal(HttpStatusCode.OK, (await app.LoginAsync(client, null, "query-global@example.invalid", "CorrectPassword1!")).StatusCode);
        using var pending = await client.GetAsync($"/api/gyms/{first}/administrators/pending");
        Assert.Equal(HttpStatusCode.OK, pending.StatusCode);
        Assert.Equal("{\"items\":[{\"accountId\":\"opaque-pending\",\"email\":\"pending@example.invalid\"}],\"page\":1,\"pageSize\":20,\"totalCount\":1}", await pending.Content.ReadAsStringAsync());
        using var beyond = await client.GetAsync($"/api/gyms/{first}/administrators/pending?page=2&pageSize=1");
        Assert.Equal(HttpStatusCode.OK, beyond.StatusCode);
        Assert.Equal("{\"items\":[],\"page\":2,\"pageSize\":1,\"totalCount\":1}", await beyond.Content.ReadAsStringAsync());
        using var empty = await client.GetAsync($"/api/gyms/{second}/administrators/pending");
        Assert.Equal(HttpStatusCode.OK, empty.StatusCode);
        Assert.Equal("{\"items\":[],\"page\":1,\"pageSize\":20,\"totalCount\":0}", await empty.Content.ReadAsStringAsync());
        using var missing = await client.GetAsync("/api/gyms/999/administrators/pending");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.True(missing.Headers.CacheControl?.NoStore);
        Assert.Equal(404, (await missing.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ProblemDetails>())?.Status);
        using var gyms = await client.GetAsync("/api/gyms?page=2&pageSize=1");
        Assert.Equal(HttpStatusCode.OK, gyms.StatusCode);
        Assert.Equal($"{{\"items\":[{{\"id\":{second},\"nombre\":\"Test gym\"}}],\"page\":2,\"pageSize\":1,\"totalCount\":2}}", await gyms.Content.ReadAsStringAsync());
        using var gymsBeyond = await client.GetAsync("/api/gyms?page=3&pageSize=1");
        Assert.Equal(HttpStatusCode.OK, gymsBeyond.StatusCode);
        Assert.Equal("{\"items\":[],\"page\":3,\"pageSize\":1,\"totalCount\":2}", await gymsBeyond.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/students")).StatusCode);
        using var verification = app.Factory.Services.CreateScope();
        var db = verification.ServiceProvider.GetRequiredService<StaticXDbContext>();
        var account = await db.Users.AsNoTracking().SingleAsync(u => u.Id == "opaque-pending");
        Assert.False(account.EmailConfirmed);
        Assert.Null(account.PasswordHash);
        Assert.Equal("unchanged-stamp", account.SecurityStamp);
        Assert.Equal("unchanged-concurrency", account.ConcurrencyStamp);
        Assert.Equal(2, await db.Users.CountAsync());
        Assert.Equal(2, await db.Gimnasios.CountAsync());
        Assert.Equal(1, await db.ProvisioningRequests.CountAsync());
        Assert.Equal(0, await db.ActivationInvitations.CountAsync());
        Assert.Equal(0, await db.ActivationOutbox.CountAsync());
    }

    [Fact]
    public async Task GymQueries_EmptyGyms_ReturnsEmptyPageAtMaximumPageSize()
    {
        await using var app = await TestApp.CreateAsync();
        await app.CreateGlobalAsync("query-global@example.invalid", "CorrectPassword1!");
        using var client = app.Client();
        Assert.Equal(HttpStatusCode.OK, (await app.LoginAsync(client, null, "query-global@example.invalid", "CorrectPassword1!")).StatusCode);
        using var response = await client.GetAsync("/api/gyms?pageSize=100");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("{\"items\":[],\"page\":1,\"pageSize\":100,\"totalCount\":0}", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task GymQueries_UnexpectedPersistenceFailures_ReturnSanitizedProblems()
    {
        await using var app = await TestApp.CreateAsync(failQueries: true);
        await app.CreateGlobalAsync("query-global@example.invalid", "CorrectPassword1!");
        using var client = app.Client();
        Assert.Equal(HttpStatusCode.OK, (await app.LoginAsync(client, null, "query-global@example.invalid", "CorrectPassword1!")).StatusCode);
        foreach (var route in new[] { "/api/gyms", "/api/gyms/1/administrators/pending" })
        {
            using var response = await client.GetAsync(route);
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
            Assert.Equal(500, (await response.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ProblemDetails>())?.Status);
            var text = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain("private-database-detail", text);
            Assert.DoesNotContain("AspNetUsers", text);
        }
    }

    [Fact]
    public async Task GymQueries_ForgedGlobalCookie_IsRejectedBeforeSelectingGym()
    {
        await using var app = await TestApp.CreateAsync();
        var gym = await app.CreateGymAsync();
        var account = await app.CreateAccountAsync(gym, "tenant-query@example.invalid", "CorrectPassword1!", true, AccountRole.GymAdmin);
        var options = app.Factory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(IdentityConstants.ApplicationScheme);
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, account.Id), new Claim(ClaimTypes.Role, "SuperAdmin")
        }, IdentityConstants.ApplicationScheme));
        using var client = app.Client();
        client.DefaultRequestHeaders.Add("Cookie", ".AspNetCore.Identity.Application=" + options.TicketDataFormat.Protect(
            new AuthenticationTicket(principal, IdentityConstants.ApplicationScheme)));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/gyms")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync($"/api/gyms/{gym}/administrators/pending")).StatusCode);
    }

    [Fact]
    public async Task GymQueries_AccountConfirmedAfterListing_ReissueRemainsNotFoundAndRequiresCsrf()
    {
        await using var app = await TestApp.CreateAsync(activation: true);
        var gym = await app.CreateGymAsync();
        await app.CreateGlobalAsync("query-global@example.invalid", "CorrectPassword1!");
        using (var scope = app.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StaticXDbContext>();
            db.Users.Add(new GymAccount { Id = "advisory-account", UserName = "advisory-account", GymId = gym,
                Role = AccountRole.GymAdmin, Email = "advisory@example.invalid", NormalizedEmail = "ADVISORY@EXAMPLE.INVALID" });
            db.ProvisioningRequests.Add(new ProvisioningRequest { Key = "advisory-request", GymId = gym, AccountId = "advisory-account" });
            await db.SaveChangesAsync();
        }
        using var client = app.Client();
        Assert.Equal(HttpStatusCode.OK, (await app.LoginAsync(client, null, "query-global@example.invalid", "CorrectPassword1!")).StatusCode);
        using var listed = await client.GetAsync($"/api/gyms/{gym}/administrators/pending");
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        Assert.Equal("advisory-account", Assert.Single((await listed.Content.ReadFromJsonAsync<PendingAdministratorListResponse>())!.Items).AccountId);
        using (var scope = app.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StaticXDbContext>();
            var account = await db.Users.SingleAsync(u => u.Id == "advisory-account");
            account.EmailConfirmed = true;
            account.PasswordHash = "test-only-activated-state";
            await db.SaveChangesAsync();
        }
        var route = $"/api/gyms/{gym}/administrators/advisory-account/invitation";
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync(route, null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.PostAsync(client, route, null)).StatusCode);
        using var nowEmpty = await client.GetAsync($"/api/gyms/{gym}/administrators/pending");
        Assert.Equal(HttpStatusCode.OK, nowEmpty.StatusCode);
        var page = await nowEmpty.Content.ReadFromJsonAsync<PendingAdministratorListResponse>();
        Assert.Empty(page!.Items);
        Assert.Equal(0, page.TotalCount);
        using var verification = app.Factory.Services.CreateScope();
        var context = verification.ServiceProvider.GetRequiredService<StaticXDbContext>();
        Assert.Equal(0, await context.ActivationInvitations.CountAsync());
        Assert.Equal(0, await context.ActivationOutbox.CountAsync());
    }

    private sealed class TestApp : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        private readonly string? activationKeys;
        public WebApplicationFactory<Program> Factory { get; }

        private TestApp(SqliteConnection connection, WebApplicationFactory<Program> factory, string? activationKeys)
        {
            this.connection = connection;
            Factory = factory;
            this.activationKeys = activationKeys;
        }

        public static async Task<TestApp> CreateAsync(bool activation = false, bool failPersistence = false, bool failQueries = false)
        {
            var keys = activation
                ? Path.Combine(Path.GetTempPath(), "static-x-activation-http", Guid.NewGuid().ToString("N"))
                : null;
            if (keys is not null) Directory.CreateDirectory(keys);
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
                builder.UseEnvironment("Development")
                .UseSetting("ConnectionStrings:Static-X", "Server=unused;Database=unused;Integrated Security=true")
                .UseSetting("Activation:KeyRingPath", keys)
                .UseSetting("Activation:FrontendUrl", "https://localhost/activate")
                .UseSetting("Resend:ApiKey", "")
                .UseSetting("Resend:FromAddress", "")
                .ConfigureServices(services =>
                {
                    services.RemoveAll<DbContextOptions<StaticXDbContext>>();
                    services.RemoveAll<IDbContextOptionsConfiguration<StaticXDbContext>>();
                    services.AddDbContext<StaticXDbContext>(options => options.UseSqlite(connection));
                    if (failPersistence) services.AddScoped<IActivationRepository, FaultingActivationRepository>();
                    if (failQueries) services.AddScoped<IGymRepository, FaultingGymRepository>();
                }));
            using var scope = factory.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<StaticXDbContext>().Database.EnsureCreatedAsync();
            return new TestApp(connection, factory, keys);
        }

        public async Task<(int Gym, string Token)> ProvisionAsync()
        {
            using var scope = Factory.Services.CreateScope();
            var provider = scope.ServiceProvider;
            var gym = await provider.GetRequiredService<IActivationService>()
                .ProvisionAsync(new ProvisionGymRequest("bootstrap-1", "Test gym", "admin@example.com"));
            var context = provider.GetRequiredService<StaticXDbContext>();
            var invitation = await context.ActivationInvitations.SingleAsync();
            var payload = await context.ActivationOutbox.SingleAsync();
            var token = provider.GetRequiredService<IDataProtectionProvider>()
                .CreateProtector("Static-X.ActivationDelivery.v1", invitation.Id.ToString("N"))
                .Unprotect(payload.ProtectedToken);
            return (gym, token);
        }

        public async Task<string> CreateGlobalAsync(string email, string password, bool confirmed = true)
        {
            using var scope = Factory.Services.CreateScope();
            var user = new GymAccount { Id = Guid.NewGuid().ToString("N"), UserName = Guid.NewGuid().ToString("N"),
                Email = email, Role = AccountRole.SuperAdmin, GymId = null, EmailConfirmed = confirmed, LockoutEnabled = true };
            Assert.True((await scope.ServiceProvider.GetRequiredService<UserManager<GymAccount>>().CreateAsync(user, password)).Succeeded);
            return user.Id;
        }

        public async Task<HttpResponseMessage> PostAsync(HttpClient client, string route, object? body)
        {
            var csrf = await client.GetFromJsonAsync<CsrfToken>("/api/account/csrf");
            using var request = new HttpRequestMessage(HttpMethod.Post, route) { Content = body is null ? null : JsonContent.Create(body) };
            request.Headers.Add("X-CSRF-TOKEN", csrf!.Token);
            return await client.SendAsync(request);
        }

        public async Task<string> LogoutStateAsync()
        {
            using var scope = Factory.Services.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<StaticXDbContext>();
            // Keep sensitive snapshot values private, including on assertion failure.
            return JsonSerializer.Serialize(new
            {
                Accounts = await context.Users.AsNoTracking().OrderBy(u => u.Id).ToListAsync(),
                Gyms = await context.Gimnasios.CountAsync(),
                Invitations = await context.ActivationInvitations.CountAsync(),
                ProvisioningRequests = await context.ProvisioningRequests.CountAsync(),
                Outbox = await context.ActivationOutbox.CountAsync()
            });
        }

        public HttpClient Client() => Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false,
            HandleCookies = true
        });

        public async Task<int> CreateGymAsync()
        {
            using var scope = Factory.Services.CreateScope();
            return (await scope.ServiceProvider.GetRequiredService<IGymService>().AddAsync(new CreateGymRequest("Test gym"))).Id;
        }

        public async Task<AccountResponse> CreateAccountAsync(int gym, string email, string password,
            bool confirmed, AccountRole role = AccountRole.Student)
        {
            using var scope = Factory.Services.CreateScope();
            var account = await scope.ServiceProvider.GetRequiredService<IAccountService>()
                .CreateAsync(gym, new CreateAccountRequest(email, password), role);
            if (confirmed)
            {
                var users = scope.ServiceProvider.GetRequiredService<UserManager<GymAccount>>();
                var user = await users.FindByIdAsync(account.Id);
                Assert.NotNull(user);
                user.EmailConfirmed = true; // Simulates trusted provisioning; no public activation route exists.
                Assert.True((await users.UpdateAsync(user)).Succeeded);
            }
            return account;
        }

        public async Task<HttpResponseMessage> LoginAsync(HttpClient client, int? gym, string email, string password)
        {
            var csrfResponse = await client.GetAsync("/api/account/csrf");
            Assert.True(csrfResponse.IsSuccessStatusCode, await csrfResponse.Content.ReadAsStringAsync());
            var csrf = await csrfResponse.Content.ReadFromJsonAsync<CsrfResponse>();
            Assert.NotNull(csrf);
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/account/login")
            {
                Content = JsonContent.Create(new LoginRequest { GymId = gym, Email = email, Password = password })
            };
            request.Headers.Add("X-CSRF-TOKEN", csrf.Token);
            return await client.SendAsync(request);
        }

        public async ValueTask DisposeAsync()
        {
            await Factory.DisposeAsync();
            await connection.DisposeAsync();
            if (activationKeys is not null) Directory.Delete(activationKeys, true);
        }

        private sealed record CsrfResponse(string Token);

        private sealed class FaultingGymRepository : IGymRepository
        {
            public Task<Gimnasio> AddAsync(Gimnasio gym, CancellationToken cancellationToken) => throw new NotSupportedException();
            public Task<global::DAL.Repositories.Models.GymPage> ListAsync(int offset, int pageSize, CancellationToken cancellationToken) =>
                throw new InvalidDataException("private-database-detail AspNetUsers");
            public Task<global::DAL.Repositories.Models.PendingAdministratorPage?> ListPendingAdministratorsAsync(int gymId, int offset, int pageSize, CancellationToken cancellationToken) =>
                throw new InvalidDataException("private-database-detail AspNetUsers");
        }

        private sealed class FaultingActivationRepository : IActivationRepository
        {
            public Task<int> ProvisionAsync(string key, string gymName, string email, string tokenHash, Guid invitationId,
                string protectedToken, DateTimeOffset now, DateTimeOffset expiresAt, CancellationToken cancellationToken) =>
                throw new InvalidDataException("private-database-detail");
            public Task<bool> ReissueAsync(int gymId, string accountId, string tokenHash, Guid invitationId,
                string protectedToken, DateTimeOffset now, DateTimeOffset expiresAt, CancellationToken cancellationToken) =>
                throw new InvalidDataException("private-database-detail");
            public Task<Account?> ActivateAsync(string tokenHash, string password, DateTimeOffset now, CancellationToken cancellationToken) =>
                throw new NotSupportedException();
        }
    }
}
