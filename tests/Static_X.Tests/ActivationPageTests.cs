using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DAL;
using DAL.Repositories.Interfaces;
using Entity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.HttpsPolicy;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Static_X.Tests;

public sealed class ActivationPageTests
{
    private const string Page = "/activate";
    private const string Script = "/activate/activation.js";
    private const string Styles = "/activate/activation.css";
    private const string Activation = "/api/account/activation";
    private const string Csrf = "/api/account/csrf";
    private const string SamplePassword = "Sample password 42!";
    private static readonly string SampleToken = new('A', 64);

    [Theory]
    [InlineData(Page, "text/html")]
    [InlineData(Script, "text/javascript")]
    [InlineData(Styles, "text/css")]
    public async Task Development_AnonymousHttpsGet_ServesOnlyProtectedPageResources(string path, string mediaType)
    {
        await using var app = new TestHost(Environments.Development);
        using var client = app.Client();
        using var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(mediaType, response.Content.Headers.ContentType?.MediaType);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal("no-referrer", Assert.Single(response.Headers.GetValues("Referrer-Policy")));
        Assert.Equal("DENY", Assert.Single(response.Headers.GetValues("X-Frame-Options")));
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        var policy = Assert.Single(response.Headers.GetValues("Content-Security-Policy"));
        Assert.Equal("default-src 'none'; script-src 'self'; style-src 'self'; connect-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'", policy);
        var body = await response.Content.ReadAsStringAsync();
        Assert.NotEmpty(body);
        if (path != Page) return;

        Assert.Contains($"src=\"{Script}\"", body);
        Assert.Contains($"href=\"{Styles}\"", body);
        Assert.Contains("<noscript>", body);
        Assert.Contains("method=\"post\"", body);
        Assert.Contains("<fieldset id=\"activation-fields\" disabled>", body);
        Assert.Contains("<label for=\"new-password\">", body);
        Assert.Contains("<input id=\"new-password\" type=\"password\" autocomplete=\"new-password\" required>", body);
        Assert.Contains("role=\"status\"", body);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task OutsideDevelopment_PageAndAssetsAreAbsentButCsrfApiRemains(string environment)
    {
        await using var app = new TestHost(environment);
        using var client = app.Client();
        foreach (var path in new[] { Page, Script, Styles })
        {
            using var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
        using var csrf = await client.GetAsync(Csrf);
        Assert.Equal(HttpStatusCode.OK, csrf.StatusCode);
    }

    [Theory]
    [InlineData("/activate/")]
    [InlineData("/Activate")]
    [InlineData("/activate/other.js")]
    [InlineData("/ActivationPage/index.html")]
    public async Task Development_AlternatePaths_DoNotServeActivationContent(string path)
    {
        await using var app = new TestHost(Environments.Development);
        using var client = app.Client();
        using var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Development_FormPostToPage_IsNotAnActivationEndpoint()
    {
        await using var app = new TestHost(Environments.Development);
        using var client = app.Client();
        using var response = await client.PostAsync(Page, null);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    [Fact]
    public async Task Development_PlainHttpRedirectsWithoutServingActivationContent()
    {
        await using var app = new TestHost(Environments.Development);
        using var client = app.Client();
        using var response = await client.GetAsync("http://localhost/activate");
        Assert.Equal(HttpStatusCode.TemporaryRedirect, response.StatusCode);
        Assert.Equal(new Uri("https://localhost/activate"), response.Headers.Location);
        Assert.Empty(await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task ExistingApis_RequireMatchingAnonymousCsrf_AndActivateWithoutSigningIn(string environment)
    {
        await using var app = new TestHost(environment);
        using var client = app.Client();
        using var noCookie = app.Client(handleCookies: false);
        using var csrf = await client.GetAsync(Csrf);
        Assert.Equal(HttpStatusCode.OK, csrf.StatusCode);
        Assert.True(csrf.Headers.CacheControl?.NoStore);
        Assert.Single(csrf.Headers.GetValues("Set-Cookie"));
        var requestToken = (await csrf.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        Assert.False(string.IsNullOrEmpty(requestToken));

        using var missingHeader = await SubmitAsync(client, null, SampleToken);
        using var missingCookie = await SubmitAsync(noCookie, requestToken, SampleToken);
        Assert.Equal(HttpStatusCode.BadRequest, missingHeader.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, missingCookie.StatusCode);
        Assert.Equal(0, app.Activations.Calls);

        using var invalid = await SubmitAsync(client, requestToken, new string('B', 64));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.True(invalid.Headers.CacheControl?.NoStore);

        using var activated = await SubmitAsync(client, requestToken, SampleToken);
        Assert.Equal(HttpStatusCode.OK, activated.StatusCode);
        Assert.Equal("{\"gymId\":29}", await activated.Content.ReadAsStringAsync());
        Assert.True(activated.Headers.CacheControl?.NoStore);
        Assert.False(activated.Headers.Contains("Set-Cookie"));
        Assert.Equal(2, app.Activations.Calls);
        using var me = await client.GetAsync("/api/account/me");
        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
    }

    private static async Task<HttpResponseMessage> SubmitAsync(HttpClient client, string? csrf, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Activation)
        {
            Content = JsonContent.Create(new { token, password = SamplePassword })
        };
        if (csrf is not null) request.Headers.Add("X-CSRF-TOKEN", csrf);
        return await client.SendAsync(request);
    }

    private sealed class TestHost : WebApplicationFactory<Program>
    {
        private readonly string environment;
        public ActivationRepositoryStub Activations { get; } = new();

        public TestHost(string environment) => this.environment = environment;

        public HttpClient Client(bool handleCookies = true) => CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false,
            HandleCookies = handleCookies
        });

        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder
            .UseEnvironment(environment)
            .UseSetting("ConnectionStrings:Static-X", "Server=unused;Database=unused;Integrated Security=true")
            .UseSetting("Activation:KeyRingPath", "")
            .UseSetting("Resend:ApiKey", "")
            .ConfigureTestServices(services =>
            {
                services.Configure<HttpsRedirectionOptions>(options => options.HttpsPort = 443);
                foreach (var worker in services.Where(d => d.ServiceType == typeof(IHostedService) &&
                    d.ImplementationType?.Assembly == typeof(Program).Assembly).ToArray())
                    services.Remove(worker);

                services.RemoveAll<DbContextOptions<StaticXDbContext>>();
                services.RemoveAll<IDbContextOptionsConfiguration<StaticXDbContext>>();
                services.AddDbContext<StaticXDbContext>(options => options.UseSqlite("Data Source=:memory:"));
                services.RemoveAll<IDataProtectionProvider>();
                services.AddSingleton<IDataProtectionProvider, EphemeralDataProtectionProvider>();
                services.RemoveAll<IActivationRepository>();
                services.AddSingleton<IActivationRepository>(Activations);
            });
    }

    private sealed class ActivationRepositoryStub : IActivationRepository
    {
        public int Calls { get; private set; }

        public Task<Account?> ActivateAsync(string tokenHash, string password, DateTimeOffset now,
            CancellationToken cancellationToken)
        {
            Calls++;
            var expectedHash = Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(SampleToken)));
            return Task.FromResult<Account?>(tokenHash == expectedHash && password == SamplePassword
                ? new Account("activated-account", 29, "member@example.invalid", AccountRole.GymAdmin)
                : null);
        }

        public Task<Account?> InviteAsync(int gymId, string key, string email, AccountRole role, int? studentId,
            string tokenHash, Guid invitationId, string protectedToken, DateTimeOffset now, DateTimeOffset expiresAt,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<int> ProvisionAsync(string key, string gymName, string email, string tokenHash,
            Guid invitationId, string protectedToken, DateTimeOffset now, DateTimeOffset expiresAt,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> ReissueAsync(int gymId, string accountId, string tokenHash, Guid invitationId,
            string protectedToken, DateTimeOffset now, DateTimeOffset expiresAt,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
