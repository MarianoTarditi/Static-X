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
        var (gym, token) = await app.ProvisionAsync();
        var client = app.Client();
        var body = new ActivateAccountRequest { Token = token, Password = "StrongPassword1!" };
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync("/api/account/activation", body)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await app.LoginAsync(client, gym, "admin@example.com", "StrongPassword1!")).StatusCode);

        var csrf = await client.GetFromJsonAsync<CsrfToken>("/api/account/csrf");
        Assert.NotNull(csrf);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/account/activation")
        { Content = JsonContent.Create(body) };
        request.Headers.Add("X-CSRF-TOKEN", csrf.Token);
        var result = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NoContent, result.StatusCode);
        Assert.False(result.Headers.Contains("Set-Cookie"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/account/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await app.LoginAsync(client, gym, "admin@example.com", "StrongPassword1!")).StatusCode);
    }

    private sealed record CsrfToken(string Token);

    [Fact]
    public async Task LocalPickup_RealProvisioningActivationAndLogin_GrantsStudentAccessWithoutResend()
    {
        await using var app = await TestApp.CreateAsync(activation: true, localPickup: true);
        var (gym, token) = await app.ProvisionThroughLocalCliAsync();
        var client = app.Client();
        var body = new ActivateAccountRequest { Token = token, Password = "StrongPassword1!" };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/account/activation", body)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await app.LoginAsync(client, gym, "admin@example.com", "StrongPassword1!")).StatusCode);
        var csrf = await client.GetFromJsonAsync<CsrfToken>("/api/account/csrf");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/account/activation") { Content = JsonContent.Create(body) };
        request.Headers.Add("X-CSRF-TOKEN", csrf!.Token);
        using var activated = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NoContent, activated.StatusCode);
        Assert.Equal(string.Empty, await activated.Content.ReadAsStringAsync());
        Assert.False(activated.Headers.Contains("Set-Cookie"));
        Assert.Equal(HttpStatusCode.OK,
            (await app.LoginAsync(client, gym, "admin@example.com", "StrongPassword1!")).StatusCode);
        var current = await client.GetFromJsonAsync<AccountResponse>("/api/account/me");
        Assert.Equal(gym, current!.GymId);
        Assert.Equal(AccountRole.GymAdmin, current.Role);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/students")).StatusCode);
    }

    [Fact]
    public async Task HttpHost_PickupFlagsDoNotSelectLocalTransportOrRelaxDefaultDispatcher()
    {
        await using var app = await TestApp.CreateAsync(activation: true, localPickup: true, composeLocalPickup: false);
        using var scope = app.Factory.Services.CreateScope();
        Assert.IsType<ResendActivationEmailSender>(scope.ServiceProvider.GetRequiredService<IActivationEmailSender>());
        await scope.ServiceProvider.GetRequiredService<IActivationService>()
            .ProvisionAsync(new ProvisionGymRequest("http-pickup-flags", "Not local dispatch", "admin@example.invalid"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.ServiceProvider.GetRequiredService<IActivationDispatcher>().DispatchOneAsync());
        var job = await scope.ServiceProvider.GetRequiredService<StaticXDbContext>().ActivationOutbox.AsNoTracking().SingleAsync();
        Assert.Null(job.LeaseOwner);
        Assert.Null(job.SentAt);
        Assert.Equal(0, job.AttemptCount);
    }

    private sealed class TestApp : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        private readonly string? activationKeys;
        private readonly string? pickupPath;
        public WebApplicationFactory<Program> Factory { get; }

        private TestApp(SqliteConnection connection, WebApplicationFactory<Program> factory, string? activationKeys, string? pickupPath)
        {
            this.connection = connection;
            Factory = factory;
            this.activationKeys = activationKeys;
            this.pickupPath = pickupPath;
        }

        public static async Task<TestApp> CreateAsync(bool activation = false, bool localPickup = false, bool composeLocalPickup = true)
        {
            var pickup = localPickup ? Path.Combine(Path.GetTempPath(), "static-x-pickup-http-" + Guid.NewGuid().ToString("N")) : null;
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
                .UseSetting("Activation:DevelopmentPickupEnabled", localPickup ? "true" : null)
                .UseSetting("Activation:DevelopmentDatabaseConfirmed", localPickup ? "true" : null)
                .UseSetting("Activation:DevelopmentPickupPath", pickup)
                .UseSetting("Resend:ApiKey", "")
                .UseSetting("Resend:FromAddress", "")
                .ConfigureServices(services =>
                {
                    services.RemoveAll<DbContextOptions<StaticXDbContext>>();
                    services.RemoveAll<IDbContextOptionsConfiguration<StaticXDbContext>>();
                    services.AddDbContext<StaticXDbContext>(options => options.UseSqlite(connection));
                    if (localPickup && composeLocalPickup)
                    {
                        services.AddScoped<IActivationEmailSender, DevelopmentPickupEmailSender>();
                        services.AddScoped<IActivationDispatcher>(provider => new ActivationDispatcher(
                            provider.GetRequiredService<IActivationDeliveryRepository>(),
                            provider.GetRequiredService<IDataProtectionProvider>(), provider.GetRequiredService<IConfiguration>(),
                            provider.GetRequiredService<TimeProvider>(), provider.GetRequiredService<IActivationEmailSender>(),
                            requireResendConfiguration: false));
                    }
                }));
            using var scope = factory.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<StaticXDbContext>().Database.EnsureCreatedAsync();
            return new TestApp(connection, factory, keys, pickup);
        }

        public async Task<(int Gym, string Token)> ProvisionThroughLocalCliAsync()
        {
            using var scope = Factory.Services.CreateScope();
            var provider = scope.ServiceProvider;
            Assert.True(await ActivationCli.ExecuteAsync(ActivationCli.Parse(["provision-gym", "--idempotency-key", "local-1",
                "--gym-name", "Local test", "--admin-email", "admin@example.com"]), provider));
            var context = provider.GetRequiredService<StaticXDbContext>();
            var account = await context.Users.AsNoTracking().SingleAsync();
            Assert.Null(account.PasswordHash);
            Assert.False(account.EmailConfirmed);
            // A second real job proves that one CLI invocation does not drain the outbox.
            await provider.GetRequiredService<IActivationService>().ProvisionAsync(new ProvisionGymRequest("local-2", "Other", "other@example.com"));
            var firstInvitation = await context.ActivationInvitations.AsNoTracking().SingleAsync(i => i.AccountId == account.Id);
            await context.ActivationOutbox.Where(o => o.InvitationId == firstInvitation.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(o => o.NextAttemptAt, DateTime.UtcNow.AddMinutes(-2)));
            Assert.True(await ActivationCli.ExecuteAsync(ActivationCli.Parse(["dispatch-activation-local"]), provider));
            var file = Assert.Single(Directory.GetFiles(pickupPath!));
            using var record = JsonDocument.Parse(await File.ReadAllTextAsync(file));
            Assert.Equal(account.Email, record.RootElement.GetProperty("Email").GetString());
            var job = await context.ActivationOutbox.AsNoTracking().SingleAsync(o => o.SentAt != null);
            Assert.Equal("activation/" + job.Id.ToString("N"), record.RootElement.GetProperty("IdempotencyKey").GetString());
            Assert.Equal(string.Empty, job.ProtectedToken);
            Assert.Equal(1, await context.ActivationOutbox.AsNoTracking().CountAsync(o => o.SentAt == null && o.ProtectedToken != ""));
            var link = new Uri(record.RootElement.GetProperty("Link").GetString()!);
            Assert.Empty(link.Query);
            Assert.Equal("localhost", link.Host);
            return (account.GymId, Uri.UnescapeDataString(link.Fragment["#activation=".Length..]));
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

        public async Task<HttpResponseMessage> LoginAsync(HttpClient client, int gym, string email, string password)
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
            if (pickupPath is not null && Directory.Exists(pickupPath)) Directory.Delete(pickupPath, true);
        }

        private sealed record CsrfResponse(string Token);
    }
}
