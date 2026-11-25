using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BLL.DTOs.Accounts;
using DAL;
using Entity;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Static_X.Tests;

public sealed partial class AccountHttpTests
{
    [Theory]
    [InlineData(AccountRole.SuperAdmin)]
    [InlineData(AccountRole.GymAdmin)]
    [InlineData(AccountRole.Trainer)]
    [InlineData(AccountRole.Student)]
    public async Task ChangePassword_AllRoles_RevokesCurrentSecondAndCopiedCookiesAtSameClockAndRequiresNewLogin(AccountRole role)
    {
        var clock = new PasswordClock();
        await using var app = await TestApp.CreateAsync(clock: clock);
        int? gym = role == AccountRole.SuperAdmin ? null : await app.CreateGymAsync();
        var id = gym is null
            ? await app.CreateGlobalAsync("same@example.invalid", "CorrectPassword1!")
            : (await app.CreateAccountAsync(gym.Value, "same@example.invalid", "CorrectPassword1!", true, role)).Id;
        var otherGym = await app.CreateGymAsync();
        var other = await app.CreateAccountAsync(otherGym, "same@example.invalid", "OtherPassword1!", true);
        using var client = app.Client();
        using var second = app.Client();
        using var unaffected = app.Client();
        using var login = await app.LoginAsync(client, gym, "same@example.invalid", "CorrectPassword1!");
        using var secondLogin = await app.LoginAsync(second, gym, "same@example.invalid", "CorrectPassword1!");
        using var otherLogin = await app.LoginAsync(unaffected, otherGym, "same@example.invalid", "OtherPassword1!");
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.Equal(HttpStatusCode.OK, secondLogin.StatusCode);
        Assert.Equal(HttpStatusCode.OK, otherLogin.StatusCode);
        var options = app.Factory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(IdentityConstants.ApplicationScheme);
        var originalCookie = Assert.Single(login.Headers.GetValues("Set-Cookie"), value => value.StartsWith(options.Cookie.Name + "="));
        var ticket = options.TicketDataFormat.Unprotect(originalCookie.Split(';')[0][(options.Cookie.Name!.Length + 1)..]);
        Assert.NotNull(ticket);
        Assert.Equal(clock.GetUtcNow(), ticket.Properties.IssuedUtc);
        using var copied = app.Factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        { BaseAddress = new Uri("https://localhost"), HandleCookies = false, AllowAutoRedirect = false });
        copied.DefaultRequestHeaders.Add("Cookie", originalCookie.Split(';')[0]);
        Assert.Equal(HttpStatusCode.OK, (await copied.GetAsync("/api/account/me")).StatusCode);
        var before = await ReadPasswordAccountsAsync(app);

        // Extra client selectors must never target the other same-email account or change membership.
        using var changed = await app.PostAsync(client, "/api/account/change-password", new
        {
            currentPassword = "CorrectPassword1!", newPassword = " NewPassword2! ",
            accountId = other.Id, gymId = otherGym, role = 4
        });

        Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);
        Assert.Equal(string.Empty, await changed.Content.ReadAsStringAsync());
        Assert.True(changed.Headers.CacheControl?.NoStore);
        var expired = Assert.Single(Microsoft.Net.Http.Headers.SetCookieHeaderValue.ParseList(
            changed.Headers.GetValues("Set-Cookie").ToList()), cookie => cookie.Name.Value == options.Cookie.Name);
        Assert.True(string.IsNullOrEmpty(expired.Value.Value));
        Assert.True(expired.Expires < DateTimeOffset.UnixEpoch.AddYears(1));
        foreach (var session in new[] { client, second, copied })
        {
            using var me = await session.GetAsync("/api/account/me");
            Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
            Assert.Null(me.Headers.Location);
        }
        Assert.Equal(other.Id, (await unaffected.GetFromJsonAsync<AccountResponse>("/api/account/me"))!.Id);
        var after = await ReadPasswordAccountsAsync(app);
        Assert.Equal(before.Count, after.Count);
        Assert.True(JsonSerializer.Serialize(before[other.Id]) == JsonSerializer.Serialize(after[other.Id]), "Other account state must remain unchanged.");
        Assert.True(before[id].PasswordHash != after[id].PasswordHash, "Password hash must change.");
        Assert.True(before[id].SecurityStamp != after[id].SecurityStamp, "Security stamp must change.");
        Assert.Equal((gym, role, before[id].AccessFailedCount, before[id].LockoutEnd),
            (after[id].GymId, after[id].Role, after[id].AccessFailedCount, after[id].LockoutEnd));
        Assert.Equal(clock.GetUtcNow(), ticket.Properties.IssuedUtc);
        // LoginAsync obtains fresh anonymous CSRF; whitespace is part of the password.
        using var oldLogin = await app.LoginAsync(client, gym, "same@example.invalid", "CorrectPassword1!");
        Assert.Equal(HttpStatusCode.Unauthorized, oldLogin.StatusCode);
        using var trimmedLogin = await app.LoginAsync(client, gym, "same@example.invalid", "NewPassword2!");
        Assert.Equal(HttpStatusCode.Unauthorized, trimmedLogin.StatusCode);
        using var newLogin = await app.LoginAsync(client, gym, "same@example.invalid", " NewPassword2! ");
        Assert.Equal(HttpStatusCode.OK, newLogin.StatusCode);
        Assert.Equal(id, (await client.GetFromJsonAsync<AccountResponse>("/api/account/me"))!.Id);
    }

    [Theory]
    [InlineData("missing-csrf")]
    [InlineData("invalid-csrf")]
    [InlineData("prelogin-csrf")]
    [InlineData("wrong-current")]
    [InlineData("weak-new")]
    [InlineData("null-current")]
    [InlineData("null-new")]
    [InlineData("empty-current")]
    [InlineData("empty-new")]
    [InlineData("missing-current")]
    [InlineData("missing-new")]
    [InlineData("blank-current")]
    [InlineData("blank-new")]
    public async Task ChangePassword_InvalidCsrfCredentialsOrFields_RejectsWithoutMutationOrSigningOut(string scenario)
    {
        await using var app = await TestApp.CreateAsync();
        var gym = await app.CreateGymAsync();
        var account = await app.CreateAccountAsync(gym, "change@example.invalid", "CorrectPassword1!", true);
        using var client = app.Client();
        using var second = app.Client();
        var anonymous = await client.GetFromJsonAsync<CsrfResponse>("/api/account/csrf");
        using var login = await app.LoginAsync(client, gym, "change@example.invalid", "CorrectPassword1!");
        using var secondLogin = await app.LoginAsync(second, gym, "change@example.invalid", "CorrectPassword1!");
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.Equal(HttpStatusCode.OK, secondLogin.StatusCode);
        var before = await app.LogoutStateAsync();
        var fields = new Dictionary<string, string?>
        {
            ["currentPassword"] = scenario == "wrong-current" ? "WrongPassword1!" : scenario == "null-current" ? null : scenario == "empty-current" ? "" : scenario == "blank-current" ? " " : "CorrectPassword1!",
            ["newPassword"] = scenario == "weak-new" ? "weak" : scenario == "null-new" ? null : scenario == "empty-new" ? "" : scenario == "blank-new" ? " " : "NewPassword2!"
        };
        if (scenario == "missing-current") fields.Remove("currentPassword");
        if (scenario == "missing-new") fields.Remove("newPassword");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/account/change-password") { Content = JsonContent.Create(fields) };
        if (scenario != "missing-csrf")
        {
            var token = scenario == "invalid-csrf" ? "invalid" : scenario == "prelogin-csrf" ? anonymous!.Token
                : (await client.GetFromJsonAsync<CsrfResponse>("/api/account/csrf"))!.Token;
            request.Headers.Add("X-CSRF-TOKEN", token);
        }

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        Assert.True(before == await app.LogoutStateAsync(), "Invalid password changes must not mutate account or activation state.");
        foreach (var session in new[] { client, second })
            Assert.Equal(account.Id, (await session.GetFromJsonAsync<AccountResponse>("/api/account/me"))!.Id);
        if (scenario is "wrong-current" or "weak-new")
        {
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(400, body.RootElement.GetProperty("status").GetInt32());
            Assert.Equal("Invalid password change request.", body.RootElement.GetProperty("title").GetString());
            Assert.Equal(new[] { "status", "title" }, body.RootElement.EnumerateObject().Select(p => p.Name).Order());
            Assert.True(response.Headers.CacheControl?.NoStore);
        }
    }

    [Fact]
    public async Task ChangePassword_Anonymous_ReturnsUnauthorizedWithoutMutation()
    {
        await using var app = await TestApp.CreateAsync();
        using var client = app.Client();
        var before = await app.LogoutStateAsync();
        using var response = await client.PostAsJsonAsync("/api/account/change-password", new { currentPassword = "old", newPassword = "new" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        using var csrfResponse = await app.PostAsync(client, "/api/account/change-password", new { currentPassword = "old", newPassword = "new" });
        Assert.Equal(HttpStatusCode.Unauthorized, csrfResponse.StatusCode);
        Assert.True(before == await app.LogoutStateAsync(), "Anonymous request must not mutate state.");
    }

    private static async Task<Dictionary<string, GymAccount>> ReadPasswordAccountsAsync(TestApp app)
    {
        using var scope = app.Factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<StaticXDbContext>().Users.AsNoTracking().ToDictionaryAsync(u => u.Id);
    }

    [Theory]
    [InlineData("gym")]
    [InlineData("role")]
    [InlineData("global")]
    public async Task ChangePassword_ValidStampButForgedScopeOrRole_IsUnauthorizedWithoutMutation(string scenario)
    {
        await using var app = await TestApp.CreateAsync(clock: new PasswordClock());
        var gym = await app.CreateGymAsync();
        await app.CreateAccountAsync(gym, "forged@example.invalid", "CorrectPassword1!", true);
        using var genuine = app.Client();
        using var login = await app.LoginAsync(genuine, gym, "forged@example.invalid", "CorrectPassword1!");
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var options = app.Factory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(IdentityConstants.ApplicationScheme);
        var cookie = Assert.Single(login.Headers.GetValues("Set-Cookie"), value => value.StartsWith(options.Cookie.Name + "="));
        var ticket = options.TicketDataFormat.Unprotect(cookie.Split(';')[0][(options.Cookie.Name!.Length + 1)..])!;
        var identity = (System.Security.Claims.ClaimsIdentity)ticket.Principal.Identity!;
        if (scenario == "gym")
        {
            identity.RemoveClaim(identity.FindFirst("gym_id"));
            identity.AddClaim(new System.Security.Claims.Claim("gym_id", (gym + 1).ToString()));
        }
        else
        {
            identity.RemoveClaim(identity.FindFirst(System.Security.Claims.ClaimTypes.Role));
            identity.AddClaim(new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, scenario == "global" ? "SuperAdmin" : "Trainer"));
            if (scenario == "global") identity.RemoveClaim(identity.FindFirst("gym_id"));
        }
        using var forged = app.Factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        { BaseAddress = new Uri("https://localhost"), HandleCookies = false });
        forged.DefaultRequestHeaders.Add("Cookie", options.Cookie.Name + "=" + options.TicketDataFormat.Protect(ticket));
        var before = await app.LogoutStateAsync();

        using var response = await app.PostAsync(forged, "/api/account/change-password", new { currentPassword = "CorrectPassword1!", newPassword = "NewPassword2!" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.True(before == await app.LogoutStateAsync(), "Forged scope or role must not change passwords or identity state.");
        Assert.Equal(HttpStatusCode.OK, (await genuine.GetAsync("/api/account/me")).StatusCode);
    }

    private sealed class PasswordClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
    }
}
