using System.Collections.Concurrent;
using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using AutoMapper;
using BLL.Services.Interfaces;
using DAL;
using Entity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Static_X.Tests;

public sealed class DashboardHttpTests
{
    [Fact]
    public async Task Get_MixedFichas_UsesSessionGymIgnoringQueryAndReturnsOnlyCountsInOneAggregate()
    {
        await using var app = await TestApp.CreateAsync();
        var gym = await app.GymAsync();
        var other = await app.GymAsync();
        await app.StudentsAsync(gym, true, true, false);
        await app.StudentsAsync(other, false, false, false, true);
        var pendingEmail = await app.CreateAccountAsync(gym, AccountRole.Student);
        using (var seedScope = app.Factory.Services.CreateScope())
        {
            var db = seedScope.ServiceProvider.GetRequiredService<StaticXDbContext>();
            var pending = await db.Users.SingleAsync(account => account.Email == pendingEmail);
            pending.EmailConfirmed = false;
            var activeFichas = await db.Alumnos.Where(student => student.GymId == gym && student.IsActive)
                .OrderBy(student => student.Id).ToArrayAsync();
            activeFichas[0].AccountId = pending.Id;
            activeFichas[1].Email = pendingEmail;
            await db.SaveChangesAsync();
        }
        using var admin = await app.LoginAsync(gym, AccountRole.GymAdmin);
        app.Commands.Clear();

        using var response = await admin.GetAsync($"/api/dashboard?gymId={other}&gym_id={other}");

        await AssertCountsAsync(response, 3, 2, 1, 1);
        var sql = Assert.Single(app.Commands.StudentReads);
        Assert.Contains("COUNT(", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("WHERE", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"Nombre\"", sql);
        Assert.DoesNotContain("\"Apellido\"", sql);
        Assert.DoesNotContain("\"Email\"", sql);
        using var scope = app.Factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IMapper>().ConfigurationProvider.AssertConfigurationIsValid();
    }

    [Fact]
    public async Task Get_EmptyGymWithForeignFichas_ReturnsZeros()
    {
        await using var app = await TestApp.CreateAsync();
        var gym = await app.GymAsync();
        await app.StudentsAsync(await app.GymAsync(), true, false);
        using var admin = await app.LoginAsync(gym, AccountRole.GymAdmin);
        app.Commands.Clear();

        using var response = await admin.GetAsync("/api/dashboard");

        await AssertCountsAsync(response, 0, 0, 0, 0);
        Assert.Single(app.Commands.StudentReads);
    }

    [Theory]
    [InlineData(true, 2, 0)]
    [InlineData(false, 0, 2)]
    public async Task Get_UniformFichas_CountsPersistedActiveState(bool active, int expectedActive, int expectedInactive)
    {
        await using var app = await TestApp.CreateAsync();
        var gym = await app.GymAsync();
        await app.StudentsAsync(gym, active, active);
        using var admin = await app.LoginAsync(gym, AccountRole.GymAdmin);

        using var response = await admin.GetAsync("/api/dashboard");

        await AssertCountsAsync(response, 2, expectedActive, expectedInactive, expectedActive);
    }

    [Theory]
    [InlineData("anonymous", 401)]
    [InlineData("Trainer", 403)]
    [InlineData("Student", 403)]
    [InlineData("SuperAdmin", 403)]
    public async Task Get_UnauthorizedCaller_ReturnsExpectedStatusWithoutStudentQuery(string role, int expectedStatus)
    {
        await using var app = await TestApp.CreateAsync();
        var gym = await app.GymAsync();
        using var client = role == "anonymous" ? app.Client() :
            await app.LoginAsync(role == "SuperAdmin" ? null : gym, Enum.Parse<AccountRole>(role));
        app.Commands.Clear();

        using var response = await client.GetAsync($"/api/dashboard?gymId={gym}");

        Assert.Equal((HttpStatusCode)expectedStatus, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Empty(app.Commands.StudentReads);
    }

    [Fact]
    public async Task Get_RevokedAdministratorCookie_Returns401WithoutStudentQuery()
    {
        await using var app = await TestApp.CreateAsync();
        var gym = await app.GymAsync();
        using var admin = await app.LoginAsync(gym, AccountRole.GymAdmin);
        using (var scope = app.Factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityAccount>>();
            var user = await users.Users.SingleAsync();
            Assert.True((await users.UpdateSecurityStampAsync(user)).Succeeded);
        }
        app.Commands.Clear();

        using var response = await admin.GetAsync("/api/dashboard");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Empty(app.Commands.StudentReads);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("invalid")]
    public async Task Get_InvalidGymClaim_IsRejectedByExistingCookieValidation(string? claim)
    {
        await using var app = await TestApp.CreateAsync();
        var gym = await app.GymAsync();
        await app.CreateAccountAsync(gym, AccountRole.GymAdmin);
        using var scope = app.Factory.Services.CreateScope();
        var user = await scope.ServiceProvider.GetRequiredService<UserManager<IdentityAccount>>().Users.SingleAsync();
        var principal = await scope.ServiceProvider.GetRequiredService<IUserClaimsPrincipalFactory<IdentityAccount>>().CreateAsync(user);
        var identity = Assert.IsType<ClaimsIdentity>(principal.Identity);
        foreach (var existing in identity.FindAll("gym_id").ToArray()) identity.RemoveClaim(existing);
        if (claim is not null) identity.AddClaim(new Claim("gym_id", claim));
        var options = scope.ServiceProvider.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(IdentityConstants.ApplicationScheme);
        var ticket = new AuthenticationTicket(principal, new AuthenticationProperties
        {
            IssuedUtc = DateTimeOffset.UtcNow, ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(5)
        }, IdentityConstants.ApplicationScheme);
        using var client = app.Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"), HandleCookies = false, AllowAutoRedirect = false
        });
        client.DefaultRequestHeaders.Add("Cookie", options.Cookie.Name + "=" + options.TicketDataFormat.Protect(ticket));
        app.Commands.Clear();

        using var response = await client.GetAsync($"/api/dashboard?gymId={gym}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Empty(app.Commands.StudentReads);
    }

    [Theory]
    [InlineData(0, AccountRole.GymAdmin)]
    [InlineData(-1, AccountRole.GymAdmin)]
    [InlineData(1, AccountRole.Trainer)]
    [InlineData(1, AccountRole.Student)]
    [InlineData(1, AccountRole.SuperAdmin)]
    public async Task Service_InvalidTrustedScopeOrRole_RejectsWithoutStudentQuery(int gym, AccountRole role)
    {
        await using var app = await TestApp.CreateAsync();
        using var scope = app.Factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IDashboardService>();
        app.Commands.Clear();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetAsync(gym, role));

        Assert.Empty(app.Commands.StudentReads);
    }

    [Fact]
    public async Task Get_QueryFailure_ReturnsGenericNoStore500InsteadOfZeroCounts()
    {
        await using var app = await TestApp.CreateAsync();
        using var admin = await app.LoginAsync(await app.GymAsync(), AccountRole.GymAdmin);
        app.Commands.FailStudentRead = true;

        using var response = await admin.GetAsync("/api/dashboard");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(500, body.GetProperty("status").GetInt32());
        Assert.Equal("Dashboard query failed.", body.GetProperty("title").GetString());
        Assert.False(body.TryGetProperty("students", out _));
        Assert.DoesNotContain("private-database-detail", body.GetRawText());
    }

    private static async Task AssertCountsAsync(HttpResponseMessage response, int total, int active, int inactive,
        int activeWithoutAccount)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal($"{{\"students\":{{\"total\":{total},\"active\":{active},\"inactive\":{inactive},\"activeWithoutAccount\":{activeWithoutAccount}}}}}",
            await response.Content.ReadAsStringAsync());
    }

    private sealed class StudentCommands : DbCommandInterceptor
    {
        public ConcurrentQueue<string> StudentReads { get; } = new();
        public bool FailStudentRead { get; set; }

        public void Clear() => StudentReads.Clear();

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) &&
                command.CommandText.Contains("\"Alumnos\"", StringComparison.Ordinal))
            {
                StudentReads.Enqueue(command.CommandText);
                if (FailStudentRead) throw new InvalidOperationException("private-database-detail");
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class TestApp : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        public WebApplicationFactory<Program> Factory { get; }
        public StudentCommands Commands { get; }

        private TestApp(SqliteConnection connection, WebApplicationFactory<Program> factory, StudentCommands commands)
        {
            this.connection = connection;
            Factory = factory;
            Commands = commands;
        }

        public static async Task<TestApp> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
            await connection.OpenAsync();
            var commands = new StudentCommands();
            var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder
                .UseEnvironment("Testing")
                .UseSetting("ConnectionStrings:Static-X", "Server=unused;Database=unused;Integrated Security=true")
                .UseSetting("Activation:KeyRingPath", "")
                .ConfigureServices(services =>
                {
                    foreach (var worker in services.Where(d => d.ImplementationType == typeof(Static_X.API.Integrations.PasswordRecoveryOutboxWorker) ||
                        d.ImplementationType == typeof(Static_X.API.Integrations.ActivationOutboxWorker)).ToArray())
                        services.Remove(worker);
                    services.AddDataProtection().UseEphemeralDataProtectionProvider();
                    services.RemoveAll<DbContextOptions<StaticXDbContext>>();
                    services.RemoveAll<IDbContextOptionsConfiguration<StaticXDbContext>>();
                    services.AddDbContext<StaticXDbContext>(options => options.UseSqlite(connection).AddInterceptors(commands));
                }));
            using var scope = factory.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<StaticXDbContext>().Database.EnsureCreatedAsync();
            return new TestApp(connection, factory, commands);
        }

        public HttpClient Client() => Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false, HandleCookies = true
        });

        public async Task<int> GymAsync()
        {
            using var scope = Factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<StaticXDbContext>();
            var gym = new Gym { Name = "Dashboard test gym" };
            db.Gimnasios.Add(gym);
            await db.SaveChangesAsync();
            return gym.Id;
        }

        public async Task StudentsAsync(int gym, params bool[] states)
        {
            using var scope = Factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<StaticXDbContext>();
            foreach (var active in states)
                db.Alumnos.Add(new Student { GymId = gym, IsActive = active, FirstName = "Private", LastName = "Student",
                    Email = Guid.NewGuid().ToString("N") + "@example.invalid" });
            await db.SaveChangesAsync();
        }

        public async Task<string> CreateAccountAsync(int? gym, AccountRole role)
        {
            using var scope = Factory.Services.CreateScope();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityAccount>>();
            var email = Guid.NewGuid().ToString("N") + "@example.invalid";
            var user = new IdentityAccount { GymId = gym, Role = role, UserName = email, Email = email, EmailConfirmed = true, IsEnabled = true };
            Assert.True((await users.CreateAsync(user, "CorrectPassword1!")).Succeeded);
            return email;
        }

        public async Task<HttpClient> LoginAsync(int? gym, AccountRole role)
        {
            var email = await CreateAccountAsync(gym, role);
            var client = Client();
            var csrf = await client.GetFromJsonAsync<JsonElement>("/api/account/csrf");
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/account/login")
            {
                Content = JsonContent.Create(new { gymId = gym, email, password = "CorrectPassword1!" })
            };
            request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString());
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return client;
        }

        public async ValueTask DisposeAsync()
        {
            await Factory.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
