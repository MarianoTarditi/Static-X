using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BLL.DTOs.Payments;
using BLL.Services.Interfaces;
using DAL;
using Entity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Static_X.Tests;

public sealed class PaymentHttpTests
{
    [Theory]
    [InlineData(AccountRole.GymAdmin, HttpStatusCode.OK, HttpStatusCode.OK)]
    [InlineData(AccountRole.Trainer, HttpStatusCode.OK, HttpStatusCode.Forbidden)]
    [InlineData(AccountRole.Student, HttpStatusCode.Forbidden, HttpStatusCode.Forbidden)]
    [InlineData(AccountRole.SuperAdmin, HttpStatusCode.Forbidden, HttpStatusCode.Forbidden)]
    public async Task Analytics_RoleMatrixUsesAuthenticatedGym(AccountRole role, HttpStatusCode summary, HttpStatusCode collections)
    {
        await using var app = await PaymentHttpApp.CreateAsync();
        using var client = await app.LoginAsync(role);
        var response = await client.GetAsync("/api/payments/summary?gymId=2");
        Assert.Equal(summary, response.StatusCode);
        Assert.Equal(collections, (await client.GetAsync("/api/payments/collections?from=2026-11-15&to=2026-11-15&gymId=2")).StatusCode);
        if (summary == HttpStatusCode.OK)
        {
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(1, json.GetProperty("activeStudents").GetInt32());
            Assert.Equal(1, json.GetProperty("noValidPayment").GetInt32());
            Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("?from=2026-11-15")]
    [InlineData("?to=2026-11-15")]
    [InlineData("?from=2026-11-16&to=2026-11-15")]
    [InlineData("?from=2025-01-01&to=2026-11-15")]
    [InlineData("?from=invalid&to=2026-11-15")]
    public async Task Analytics_CollectionsRejectsMissingMalformedReversedAndExcessiveRange(string query)
    {
        await using var app = await PaymentHttpApp.CreateAsync();
        using var client = await app.LoginAsync(AccountRole.GymAdmin);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/payments/collections" + query)).StatusCode);
    }

    [Fact]
    public async Task Analytics_AnonymousRequiresAuthentication()
    {
        await using var app = await PaymentHttpApp.CreateAsync();
        using var client = app.Factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/payments/summary")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/payments/collections?from=2026-11-15&to=2026-11-15")).StatusCode);
    }

    [Fact]
    public async Task Analytics_CollectionsReturnsOnlySameGymAggregateContractWithoutPersonalDetails()
    {
        await using var app = await PaymentHttpApp.CreateAsync();
        using var client = await app.LoginAsync(AccountRole.GymAdmin);
        Assert.Equal(HttpStatusCode.Created, (await app.PostAsync(client, "/api/payments/students/1", await app.ConfirmationAsync(client))).StatusCode);
        using (var scope = app.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StaticXDbContext>();
            db.GymPassPrices.Add(new GymPassPrice { GymId = 2, AmountCents = 999, CreatedBy = "foreign", EffectiveFrom = new DateOnly(2026, 11, 1) });
            await db.SaveChangesAsync();
            db.Payments.Add(new Payment { GymId = 2, StudentId = 2, PriceId = (await db.GymPassPrices.SingleAsync(p => p.GymId == 2)).Id,
                Sequence = 1, PaidOn = new DateOnly(2026, 11, 15), CoverageStart = new DateOnly(2026, 11, 15), ExtensionStart = new DateOnly(2026, 11, 15),
                CoverageEndExclusive = new DateOnly(2026, 12, 15), AmountCents = 999, Method = PaymentMethod.Card,
                ActorId = "foreign", IdempotencyKey = "foreign", RequestFingerprint = new string('A', 64) });
            await db.SaveChangesAsync();
        }
        var response = await client.GetAsync("/api/payments/collections?from=2026-11-15&to=2026-11-15&gymId=2");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(new[] { "businessDate", "currency", "from", "methods", "to", "validCents", "validCount", "voidCents", "voidCount" }, json.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal(100099, json.GetProperty("validCents").GetInt64());
        Assert.Equal(1, json.GetProperty("validCount").GetInt32());
        Assert.Equal(0, json.GetProperty("voidCount").GetInt32());
        Assert.Equal(3, json.GetProperty("methods").GetArrayLength());
        foreach (var method in json.GetProperty("methods").EnumerateArray())
            Assert.Equal(new[] { "method", "validCents", "validCount", "voidCents", "voidCount" }, method.EnumerateObject().Select(p => p.Name).Order());
    }

    [Fact]
    public async Task SameCookie_ObservesPaymentExpiryVoidAndDefaultGateWhileSelfAndSecurityRemainAvailable()
    {
        await using var app = await PaymentHttpApp.CreateAsync();
        using var student = await app.LoginAsync(AccountRole.Student);
        using var admin = await app.LoginAsync(AccountRole.GymAdmin);
        await AssertSelfAllowed(student);
        Assert.Equal(HttpStatusCode.Forbidden, (await student.GetAsync("/test/payment-probe")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await student.GetAsync("/test/payment-probe-alternate")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/test/payment-probe")).StatusCode);
        var request = await app.ConfirmationAsync(admin);
        var registered = await app.PostAsync(admin, "/api/payments/students/1", request);
        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);
        Assert.EndsWith("/api/payments/students/1", registered.Headers.Location!.ToString());
        var payment = (await registered.Content.ReadFromJsonAsync<PaymentResponse>())!;
        Assert.Equal(HttpStatusCode.OK, (await student.GetAsync("/test/payment-probe")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await student.GetAsync("/test/payment-probe-alternate")).StatusCode);
        app.Clock.Now = new DateTimeOffset(2026, 12, 15, 3, 0, 0, TimeSpan.Zero);
        Assert.Equal(HttpStatusCode.Forbidden, (await student.GetAsync("/test/payment-probe")).StatusCode);
        await AssertSelfAllowed(student);
        app.Clock.Now = new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(HttpStatusCode.NoContent, (await app.PostAsync(admin, $"/api/payments/students/1/{payment.Id}/void", new { reason = "Internal correction" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await student.GetAsync("/test/payment-probe")).StatusCode);
        var history = await student.GetStringAsync("/api/student/me/payments");
        Assert.Contains("\"isVoid\":true", history);
        Assert.DoesNotContain("Internal correction", history);
        Assert.DoesNotContain("voidedBy", history, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.Created, (await app.PostAsync(admin, "/api/payments/students/1", request)).StatusCode);
        using var scope = app.Factory.Services.CreateScope();
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<StaticXDbContext>().Payments.CountAsync());
        Assert.Equal(HttpStatusCode.NoContent, (await app.PostAsync(student, "/api/account/logout", null)).StatusCode);
    }

    [Fact]
    public async Task CoverageInfrastructureFailure_Returns503WithoutLogoutOrFakeNoPayments()
    {
        await using var app = await PaymentHttpApp.CreateAsync();
        using var student = await app.LoginAsync(AccountRole.Student);
        app.Fault.FailReads = true;
        var response = await student.GetAsync("/test/payment-probe");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.DoesNotContain("NoPayments", await response.Content.ReadAsStringAsync());
        Assert.False(response.Headers.Contains("Set-Cookie"));
        Assert.Equal(HttpStatusCode.OK, (await student.GetAsync("/api/account/me")).StatusCode);
        app.Fault.FailReads = false;
        Assert.Equal(HttpStatusCode.Forbidden, (await student.GetAsync("/test/payment-probe")).StatusCode);
    }

    [Theory]
    [InlineData(AccountRole.GymAdmin)]
    [InlineData(AccountRole.Trainer)]
    public async Task Staff_RegisterHistoryAndTableAreTenantScopedAndCsrfProtected(AccountRole role)
    {
        await using var app = await PaymentHttpApp.CreateAsync();
        using var staff = await app.LoginAsync(role);
        Assert.Equal(HttpStatusCode.BadRequest, (await staff.PostAsJsonAsync("/api/payments/students/1/preview", new { paidOn = "2026-11-15", method = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.PostAsync(staff, "/api/payments/students/2/preview", new { paidOn = "2026-11-15", method = 1, gymId = 2 })).StatusCode);
        var request = await app.ConfirmationAsync(staff);
        Assert.Equal(HttpStatusCode.BadRequest, (await staff.PostAsJsonAsync("/api/payments/students/1", request)).StatusCode);
        var saved = await app.PostAsync(staff, "/api/payments/students/1", request);
        Assert.Equal(HttpStatusCode.Created, saved.StatusCode);
        var payment = (await saved.Content.ReadFromJsonAsync<PaymentResponse>())!;
        Assert.Equal(100099, payment.AmountCents);
        Assert.Single((await staff.GetFromJsonAsync<PaymentResponse[]>("/api/payments/students/1"))!);
        Assert.Equal(HttpStatusCode.NotFound, (await staff.GetAsync("/api/payments/students/2?gymId=2")).StatusCode);
        var table = (await staff.GetFromJsonAsync<PaymentListResponse>("/api/payments?search=Ana&pageSize=1&gymId=2"))!;
        Assert.Equal(1, table.TotalCount);
        Assert.Equal(1, Assert.Single(table.Items).StudentId);
        Assert.Equal(role == AccountRole.GymAdmin ? HttpStatusCode.BadRequest : HttpStatusCode.Forbidden,
            (await staff.PostAsJsonAsync($"/api/payments/students/1/{payment.Id}/void", new { reason = "correction" })).StatusCode);
        var expected = role == AccountRole.GymAdmin ? HttpStatusCode.NoContent : HttpStatusCode.Forbidden;
        Assert.Equal(expected, (await app.PostAsync(staff, $"/api/payments/students/1/{payment.Id}/void", new { reason = "correction" })).StatusCode);
        Assert.Equal(role == AccountRole.GymAdmin ? HttpStatusCode.OK : HttpStatusCode.Forbidden,
            (await app.PostAsync(staff, "/api/payments/prices", new { amountCents = 200000 })).StatusCode);
    }

    [Theory]
    [InlineData(AccountRole.Student)]
    [InlineData(AccountRole.SuperAdmin)]
    public async Task StudentAndGlobal_HaveNoStaffPaymentAuthority(AccountRole role)
    {
        await using var app = await PaymentHttpApp.CreateAsync();
        using var client = await app.LoginAsync(role);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/payments")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/payments/prices")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/payments/students/1")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await app.PostAsync(client, "/api/payments/prices", new { amountCents = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await app.PostAsync(client, "/api/payments/students/1/preview", new { paidOn = "2026-11-15", method = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await app.PostAsync(client, "/api/payments/students/1", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await app.PostAsync(client, "/api/payments/students/1/1/void", new { reason = "test" })).StatusCode);
        if (role == AccountRole.SuperAdmin) Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/student/me/profile")).StatusCode);
    }

    [Fact]
    public async Task MissingOrIncorrectLink_DoesNotAdoptFichaByMatchingEmail()
    {
        await using var app = await PaymentHttpApp.CreateAsync();
        using var student = await app.LoginAsync(AccountRole.Student);
        using (var scope = app.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StaticXDbContext>();
            await db.Alumnos.Where(s => s.Id == 1).ExecuteUpdateAsync(s => s.SetProperty(x => x.AccountId, (string?)null));
        }
        foreach (var url in new[] { "/api/student/me/profile", "/api/student/me/coverage", "/api/student/me/payments" })
            Assert.Equal(HttpStatusCode.NotFound, (await student.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await student.GetAsync("/test/payment-probe")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await student.GetAsync("/api/account/me")).StatusCode);
    }

    [Fact]
    public async Task RegisterFailure_RollsBackRevisionAndLedgerAndCanRetryOriginalPreview()
    {
        await using var app = await PaymentHttpApp.CreateAsync();
        using var staff = await app.LoginAsync(AccountRole.Trainer);
        var request = await app.ConfirmationAsync(staff);
        app.Fault.FailInserts = true;
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await app.PostAsync(staff, "/api/payments/students/1", request)).StatusCode);
        using (var scope = app.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StaticXDbContext>();
            Assert.Empty(await db.Payments.ToArrayAsync());
            Assert.Equal(0, (await db.Alumnos.SingleAsync(s => s.Id == 1)).PaymentRevision);
        }
        app.Fault.FailInserts = false;
        Assert.Equal(HttpStatusCode.Created, (await app.PostAsync(staff, "/api/payments/students/1", request)).StatusCode);
    }

    [Fact]
    public async Task StalePreviewAndDifferentKeyPayload_Return409NotSilentRepricingOrExtension()
    {
        await using var app = await PaymentHttpApp.CreateAsync();
        using var staff = await app.LoginAsync(AccountRole.Trainer);
        var first = await app.ConfirmationAsync(staff);
        var stale = await app.ConfirmationAsync(staff);
        Assert.Equal(HttpStatusCode.Created, (await app.PostAsync(staff, "/api/payments/students/1", first)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await app.PostAsync(staff, "/api/payments/students/1", stale)).StatusCode);
        first.Method = PaymentMethod.Card;
        Assert.Equal(HttpStatusCode.Conflict, (await app.PostAsync(staff, "/api/payments/students/1", first)).StatusCode);
    }

    [Fact]
    public async Task ClientPriceRoleGymAndDuration_CannotOverrideTrustedTerms_AndFractionalTariffIsRejected()
    {
        await using var app = await PaymentHttpApp.CreateAsync();
        using var admin = await app.LoginAsync(AccountRole.GymAdmin);
        Assert.Equal(HttpStatusCode.BadRequest, (await app.PostAsync(admin, "/api/payments/prices", new { amountCents = 1.5m })).StatusCode);
        var request = await app.ConfirmationAsync(admin);
        var response = await app.PostAsync(admin, "/api/payments/students/1", new
        {
            request.PaidOn,
            request.Method,
            request.PreviewToken,
            request.IdempotencyKey,
            amountCents = 1,
            priceId = 999,
            gymId = 2,
            role = "SuperAdmin",
            days = 365
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var payment = (await response.Content.ReadFromJsonAsync<PaymentResponse>())!;
        Assert.Equal(100099, payment.AmountCents);
        Assert.Equal(new DateOnly(2026, 12, 15), payment.CoverageEndExclusive);
        Assert.Equal("ARS", payment.Currency);
    }

    [Fact]
    public async Task Table_DatabaseCountOrderingPaginationAndThreeQueriesDoNotGrowPerRow()
    {
        await using var app = await PaymentHttpApp.CreateAsync();
        using var scope = app.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StaticXDbContext>();
        db.Alumnos.AddRange(new Student { Id = 3, GymId = 1, FirstName = "Bea", LastName = "Before", Email = "BEA@EXAMPLE.INVALID" },
            new Student { Id = 4, GymId = 1, FirstName = "Zoe", LastName = "After", Email = "ZOE@EXAMPLE.INVALID" });
        await db.SaveChangesAsync(); db.ChangeTracker.Clear(); app.Fault.Commands.Clear();
        var page = await scope.ServiceProvider.GetRequiredService<IPaymentService>().ListAsync(1, AccountRole.Trainer,
            new PaymentListRequest { Page = 2, PageSize = 1 }, default);
        Assert.Equal(3, page.TotalCount);
        Assert.Equal(3, Assert.Single(page.Items).StudentId);
        Assert.Equal(3, app.Fault.Commands.Count);
        Assert.Contains("COUNT(*)", app.Fault.Commands[0]);
        Assert.Contains("LIMIT", app.Fault.Commands[1]);
        Assert.Contains("OFFSET", app.Fault.Commands[1]);
        Assert.Empty(db.ChangeTracker.Entries());
    }

    [Fact]
    public async Task AnonymousPaymentAndSelfRoutes_RequireAuthentication()
    {
        await using var app = await PaymentHttpApp.CreateAsync();
        using var anonymous = app.Factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        foreach (var route in new[] { "/api/payments", "/api/payments/prices", "/api/payments/students/1", "/api/student/me/profile", "/api/student/me/coverage", "/api/student/me/payments" })
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(route)).StatusCode);
    }

    private static async Task AssertSelfAllowed(HttpClient client)
    {
        foreach (var path in new[] { "/api/student/me/profile", "/api/student/me/coverage", "/api/student/me/payments", "/api/account/me", "/api/account/csrf" })
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(path)).StatusCode);
    }
}

// Test-only endpoints deliberately have no coverage exception, including the alternate route.
[ApiController]
[Authorize]
public sealed class PaymentProbeController : ControllerBase
{
    [HttpGet("/test/payment-probe")]
    [HttpGet("/test/payment-probe-alternate")]
    public IActionResult Probe() => Ok();
}

internal sealed class PaymentFault : DbCommandInterceptor
{
    public List<string> Commands { get; } = new List<string>();
    public bool FailReads { get; set; }
    public bool FailInserts { get; set; }
    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        Commands.Add(command.CommandText);
        if (FailReads && command.CommandText.Contains("\"Payments\"", StringComparison.Ordinal) || FailInserts && command.CommandText.StartsWith("INSERT INTO \"Payments\"", StringComparison.Ordinal))
            throw new IOException("Injected payment persistence failure.");
        return ValueTask.FromResult(result);
    }
}

internal sealed class PaymentHttpApp : IAsyncDisposable
{
    private readonly SqliteConnection connection;
    public WebApplicationFactory<Program> Factory { get; }
    public PaymentClock Clock { get; }
    public PaymentFault Fault { get; }

    private PaymentHttpApp(SqliteConnection connection, WebApplicationFactory<Program> factory, PaymentClock clock, PaymentFault fault)
    { this.connection = connection; Factory = factory; Clock = clock; Fault = fault; }

    public static async Task<PaymentHttpApp> CreateAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True"); await connection.OpenAsync();
        var clock = new PaymentClock(); var fault = new PaymentFault();
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder
            .UseSetting("ConnectionStrings:Static-X", "Server=unused;Database=unused;Integrated Security=true")
            .UseSetting("Activation:KeyRingPath", "")
            .ConfigureServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.RemoveAll<DbContextOptions<StaticXDbContext>>();
                services.RemoveAll<IDbContextOptionsConfiguration<StaticXDbContext>>();
                services.AddDbContext<StaticXDbContext>(options => options.UseSqlite(connection).AddInterceptors(fault));
                services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider>(clock);
                // Keep authentication valid across the coverage boundary; cookie expiry is tested separately.
                services.ConfigureApplicationCookie(options => options.ExpireTimeSpan = TimeSpan.FromDays(90));
                services.AddControllers().AddApplicationPart(typeof(PaymentProbeController).Assembly);
            }));
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StaticXDbContext>();
        await db.Database.EnsureCreatedAsync();
        db.Gimnasios.AddRange(new Gym { Id = 1, Name = "First" }, new Gym { Id = 2, Name = "Other" });
        await db.SaveChangesAsync();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityAccount>>();
        foreach (var role in new[] { AccountRole.GymAdmin, AccountRole.Trainer, AccountRole.Student, AccountRole.SuperAdmin })
        {
            var user = new IdentityAccount
            {
                Id = role.ToString(),
                GymId = role == AccountRole.SuperAdmin ? null : 1,
                Role = role,
                UserName = role.ToString(),
                Email = role + "@example.invalid",
                EmailConfirmed = true
            };
            Assert.True((await users.CreateAsync(user, "CorrectPassword1!")).Succeeded);
        }
        db.Alumnos.AddRange(new Student { Id = 1, GymId = 1, AccountId = "Student", FirstName = "Ana", LastName = "Own", Email = "STUDENT@EXAMPLE.INVALID" },
            new Student { Id = 2, GymId = 2, FirstName = "Ana", LastName = "Foreign", Email = "STUDENT@EXAMPLE.INVALID" });
        await db.SaveChangesAsync();
        await scope.ServiceProvider.GetRequiredService<IPaymentService>().SetPriceAsync(1, AccountRole.GymAdmin, "GymAdmin", new SetPassPriceRequest { AmountCents = 100099, InitialEffectiveFrom = new DateOnly(2026, 11, 1) }, default);
        return new PaymentHttpApp(connection, factory, clock, fault);
    }

    public async Task<HttpClient> LoginAsync(AccountRole role)
    {
        var client = Factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        var response = await PostAsync(client, "/api/account/login", new { gymId = role == AccountRole.SuperAdmin ? (int?)null : 1, email = role + "@example.invalid", password = "CorrectPassword1!" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }

    public async Task<HttpResponseMessage> PostAsync(HttpClient client, string path, object? body)
    {
        var token = (await client.GetFromJsonAsync<JsonElement>("/api/account/csrf")).GetProperty("token").GetString();
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = body is null ? null : JsonContent.Create(body) };
        request.Headers.Add("X-CSRF-TOKEN", token);
        return await client.SendAsync(request);
    }

    public async Task<RegisterPaymentRequest> ConfirmationAsync(HttpClient staff)
    {
        var result = await PostAsync(staff, "/api/payments/students/1/preview", new { paidOn = "2026-11-15", method = 1 });
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        var preview = (await result.Content.ReadFromJsonAsync<PaymentPreviewResponse>())!;
        return new RegisterPaymentRequest { PaidOn = new DateOnly(2026, 11, 15), Method = PaymentMethod.Cash, PreviewToken = preview.PreviewToken, IdempotencyKey = Guid.NewGuid().ToString("N") };
    }

    public async ValueTask DisposeAsync() { await Factory.DisposeAsync(); await connection.DisposeAsync(); }
}
