using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using BLL.Services.Interfaces;
using BLL.DTOs.Accounts;
using BLL.DTOs.Gyms;
using BLL.DTOs.Students;
using DAL;
using Entity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Static_X.Tests;

public sealed class StudentHttpTests
{
    [Theory]
    [InlineData(AccountRole.GymAdmin)]
    [InlineData(AccountRole.Trainer)]
    public async Task List_SearchAndState_CombineBeforeCountAndPagingWithoutTrustingClientGym(AccountRole role)
    {
        await using var app = await TestApp.CreateAsync();
        var gym = await app.GymAsync();
        var other = await app.GymAsync();
        using var client = await app.LoginAsync(gym, role);
        using (var scope = app.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StaticXDbContext>();
            db.Alumnos.AddRange(
                new Alumno { Id = 1, GimnasioId = gym, Nombre = "Ana", Apellido = "One", Email = "ONE@EXAMPLE.COM" },
                new Alumno { Id = 2, GimnasioId = gym, Nombre = "Bea", Apellido = "Anabel", Email = "TWO@EXAMPLE.COM", Activo = false },
                new Alumno { Id = 3, GimnasioId = gym, Nombre = "Bea", Apellido = "Three", Email = "ANA@EXAMPLE.COM", Activo = false },
                new Alumno { Id = 4, GimnasioId = other, Nombre = "Ana", Apellido = "Four", Email = "ANA@EXAMPLE.COM", Activo = false });
            await db.SaveChangesAsync();
        }
        foreach (var (filter, expected, total) in new[]
        {
            ("", 2, 3), ("&activo=false", 3, 2), ("&activo=true", 0, 1)
        })
        {
            var response = await client.GetAsync($"/api/students?search=%20aNa%20&page=2&pageSize=1&gymId={other}&gimnasioId={other}{filter}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
            var body = (await response.Content.ReadFromJsonAsync<StudentListResponse>())!;
            Assert.Equal((2, 1, total), (body.Page, body.PageSize, body.TotalCount));
            if (expected == 0) Assert.Empty(body.Items);
            else Assert.Equal(expected, Assert.Single(body.Items).Id);
        }
    }

    [Fact]
    public async Task List_DefaultAndExplicitPages_ReturnBoundedOrderedEnvelopeAndTotals()
    {
        await using var app = await TestApp.CreateAsync();
        var gym = await app.GymAsync();
        using var client = await app.LoginAsync(gym, AccountRole.Trainer);
        using (var scope = app.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StaticXDbContext>();
            db.Alumnos.AddRange(Enumerable.Range(1, 25).Select(i => new Alumno
            { Id = i, GimnasioId = gym, Nombre = "Ana", Apellido = "Smith", Email = $"A{i}@EXAMPLE.COM", Activo = i % 2 == 0 }));
            await db.SaveChangesAsync();
        }
        foreach (var (query, page, size, ids) in new[]
        {
            ("", 1, 20, Enumerable.Range(1, 20).ToArray()),
            ("?page=2", 2, 20, Enumerable.Range(21, 5).ToArray()),
            ("?page=2&pageSize=1", 2, 1, new[] { 2 }),
            ("?pageSize=100", 1, 100, Enumerable.Range(1, 25).ToArray()),
            ("?page=3", 3, 20, Array.Empty<int>()),
            ("?page=2147483647&pageSize=1", int.MaxValue, 1, Array.Empty<int>()),
            ("?search=" + new string('a', 320), 1, 20, Array.Empty<int>())
        })
        {
            var response = await client.GetAsync("/api/students" + query);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(new[] { "items", "page", "pageSize", "totalCount" }, body.EnumerateObject().Select(p => p.Name).Order());
            Assert.Equal((page, size, query.Contains("search=", StringComparison.Ordinal) ? 0 : 25), (body.GetProperty("page").GetInt32(), body.GetProperty("pageSize").GetInt32(), body.GetProperty("totalCount").GetInt32()));
            Assert.Equal(ids, body.GetProperty("items").EnumerateArray().Select(s => s.GetProperty("id").GetInt32()));
        }
    }

    [Theory]
    [InlineData("page=0")]
    [InlineData("page=-1")]
    [InlineData("page=bad")]
    [InlineData("page=2147483648")]
    [InlineData("page=2147483647&pageSize=100")]
    [InlineData("pageSize=0")]
    [InlineData("pageSize=-1")]
    [InlineData("pageSize=101")]
    [InlineData("pageSize=bad")]
    [InlineData("activo=bad")]
    [InlineData("search-long")]
    [InlineData("search-long-blank")]
    public async Task List_InvalidQuery_Returns400Not500(string query)
    {
        await using var app = await TestApp.CreateAsync();
        using var client = await app.LoginAsync(await app.GymAsync(), AccountRole.Trainer);
        if (query == "search-long") query = "search=" + new string('a', 321);
        if (query == "search-long-blank") query = "search=" + Uri.EscapeDataString(new string(' ', 321));
        var response = await client.GetAsync("/api/students?" + query);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Theory]
    [InlineData(AccountRole.GymAdmin)]
    [InlineData(AccountRole.Trainer)]
    public async Task AllowedRoles_CreateListFind_UseSessionGymAndReturnOnlyFicha(AccountRole role)
    {
        await using var app = await TestApp.CreateAsync();
        var gym = await app.GymAsync();
        var other = await app.GymAsync();
        using var client = await app.LoginAsync(gym, role);
        var empty = await client.GetAsync("/api/students");
        Assert.Equal(HttpStatusCode.OK, empty.StatusCode);
        Assert.True(empty.Headers.CacheControl?.NoStore);
        var emptyPage = await empty.Content.ReadFromJsonAsync<StudentListResponse>();
        Assert.NotNull(emptyPage);
        Assert.Empty(emptyPage.Items);
        Assert.Equal((1, 20, 0), (emptyPage.Page, emptyPage.PageSize, emptyPage.TotalCount));

        var created = await app.PostAsync(client, new
        {
            nombre = " Ana ",
            apellido = " Smith ",
            email = " Ana@Example.com ",
            GymId = other,
            GimnasioId = other
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.True(created.Headers.CacheControl?.NoStore);
        var body = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(new[] { "activo", "apellido", "email", "id", "nombre" }, body.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal("Ana", body.GetProperty("nombre").GetString());
        Assert.Equal("Smith", body.GetProperty("apellido").GetString());
        Assert.Equal("ANA@EXAMPLE.COM", body.GetProperty("email").GetString());
        var id = body.GetProperty("id").GetInt32();
        Assert.True(id > 0);
        Assert.EndsWith($"/api/students/{id}", created.Headers.Location?.ToString());
        var found = await client.GetAsync(created.Headers.Location);
        Assert.Equal(HttpStatusCode.OK, found.StatusCode);
        Assert.True(found.Headers.CacheControl?.NoStore);
        Assert.Equal(body.ToString(), (await found.Content.ReadFromJsonAsync<JsonElement>()).ToString());
        var list = await client.GetAsync("/api/students");
        Assert.True(list.Headers.CacheControl?.NoStore);
        var page = await list.Content.ReadFromJsonAsync<StudentListResponse>();
        Assert.NotNull(page);
        Assert.Equal(id, Assert.Single(page.Items).Id);
        Assert.Equal(1, page.TotalCount);

        using var scope = app.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StaticXDbContext>();
        Assert.Equal(gym, (await db.Alumnos.SingleAsync()).GimnasioId);
        Assert.Equal(1, await db.Users.CountAsync());
        Assert.Empty(await db.ActivationInvitations.ToListAsync());
        Assert.Empty(await db.ActivationOutbox.ToListAsync());
        Assert.Empty(await db.ProvisioningRequests.ToListAsync());
    }

    [Fact]
    public async Task ListAndFind_IsolateGymsAndHideExistence()
    {
        await using var app = await TestApp.CreateAsync();
        using var first = await app.LoginAsync(await app.GymAsync(), AccountRole.Trainer);
        using var second = await app.LoginAsync(await app.GymAsync(), AccountRole.GymAdmin);
        var created = await app.PostAsync(first, Valid());
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Empty((await second.GetFromJsonAsync<StudentListResponse>("/api/students"))!.Items);
        var cross = await second.GetAsync(created.Headers.Location);
        var missing = await second.GetAsync("/api/students/2147483647");
        Assert.Equal(HttpStatusCode.NotFound, cross.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(await missing.Content.ReadAsStringAsync(), await cross.Content.ReadAsStringAsync());
        Assert.True(cross.Headers.CacheControl?.NoStore);
        Assert.Equal(HttpStatusCode.Created, (await app.PostAsync(second, Valid())).StatusCode);
    }

    [Fact]
    public async Task Reads_IgnoreClientGym_ApplyPaginationWithinSessionGym()
    {
        await using var app = await TestApp.CreateAsync();
        var gym = await app.GymAsync();
        var otherGym = await app.GymAsync();
        using var client = await app.LoginAsync(gym, AccountRole.Trainer);
        using var other = await app.LoginAsync(otherGym, AccountRole.GymAdmin);
        var first = await app.PostAsync(client, Valid());
        var second = await app.PostAsync(client, new { nombre = "Ben", apellido = "Jones", email = "ben@example.com" });
        var foreign = await app.PostAsync(other, Valid());
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.Equal(HttpStatusCode.Created, foreign.StatusCode);
        var firstId = (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        var secondId = (await second.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        var foreignId = (await foreign.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

        var response = await client.GetAsync($"/api/students?gymId={otherGym}&gimnasioId={otherGym}&page=2&pageSize=1");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var students = await response.Content.ReadFromJsonAsync<StudentListResponse>();
        Assert.NotNull(students);
        Assert.NotEqual(firstId, secondId);
        Assert.Equal(secondId, Assert.Single(students.Items).Id);
        Assert.Equal((2, 1, 2), (students.Page, students.PageSize, students.TotalCount));
        var crossGym = await client.GetAsync($"/api/students/{foreignId}?gymId={otherGym}&gimnasioId={otherGym}");
        Assert.Equal(HttpStatusCode.NotFound, crossGym.StatusCode);
        Assert.True(crossGym.Headers.CacheControl?.NoStore);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnauthorizedRoles_AllEndpointsAreDenied(bool student)
    {
        await using var app = await TestApp.CreateAsync();
        using var client = student ? await app.LoginAsync(await app.GymAsync(), AccountRole.Student) : app.Client();
        var expected = student ? HttpStatusCode.Forbidden : HttpStatusCode.Unauthorized;
        var list = await client.GetAsync("/api/students");
        var find = await client.GetAsync("/api/students/1");
        var create = await app.PostAsync(client, Valid());
        Assert.Equal(expected, list.StatusCode);
        Assert.Equal(expected, find.StatusCode);
        Assert.Equal(expected, create.StatusCode);
        Assert.True(list.Headers.CacheControl?.NoStore);
        Assert.True(find.Headers.CacheControl?.NoStore);
        Assert.True(create.Headers.CacheControl?.NoStore);
        Assert.Equal(0, await app.CountAsync());
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("bad")]
    [InlineData("prelogin")]
    public async Task Create_InvalidCsrf_DoesNotInsert(string scenario)
    {
        await using var app = await TestApp.CreateAsync();
        using var client = app.Client();
        var beforeLogin = await app.TokenAsync(client);
        await app.SignInAsync(client, await app.GymAsync(), AccountRole.Trainer);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/students") { Content = JsonContent.Create(Valid()) };
        if (scenario != "missing") request.Headers.Add("X-CSRF-TOKEN", scenario == "bad" ? "invalid" : beforeLogin);
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal(0, await app.CountAsync());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/students")).StatusCode);
    }

    [Theory]
    [InlineData("name-blank")]
    [InlineData("surname-blank")]
    [InlineData("email-blank")]
    [InlineData("email-invalid")]
    [InlineData("email-long")]
    [InlineData("missing")]
    public async Task Create_InvalidFields_Returns400WithoutInsertion(string scenario)
    {
        await using var app = await TestApp.CreateAsync();
        using var client = await app.LoginAsync(await app.GymAsync(), AccountRole.Trainer);
        object body = scenario == "missing" ? new { } : new
        {
            nombre = scenario == "name-blank" ? "   " : "Ana",
            apellido = scenario == "surname-blank" ? "   " : "Smith",
            email = scenario switch { "email-blank" => "  ", "email-invalid" => "not-email", "email-long" => new string('a', 309) + "@example.com", _ => "ana@example.com" }
        };
        var response = await app.PostAsync(client, body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(0, await app.CountAsync());
    }

    [Fact]
    public async Task Create_NormalizedDuplicate_ReturnsSafe409AndKeepsSingleStudent()
    {
        await using var app = await TestApp.CreateAsync();
        using var client = await app.LoginAsync(await app.GymAsync(), AccountRole.GymAdmin);
        Assert.Equal(HttpStatusCode.Created, (await app.PostAsync(client, Valid())).StatusCode);
        var duplicate = await app.PostAsync(client, new { nombre = "Ben", apellido = "Jones", email = " ANA@EXAMPLE.COM " });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.True(duplicate.Headers.CacheControl?.NoStore);
        Assert.Equal("application/problem+json", duplicate.Content.Headers.ContentType?.MediaType);
        var problem = await duplicate.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("A student with this email already exists in this gym.", problem.GetProperty("title").GetString());
        Assert.DoesNotContain("Sqlite", problem.ToString());
        Assert.DoesNotContain("IX_", problem.ToString());
        Assert.Equal(1, await app.CountAsync());
    }

    [Fact]
    public async Task Create_EmailAtMaximumLength_Returns201()
    {
        await using var app = await TestApp.CreateAsync();
        using var client = await app.LoginAsync(await app.GymAsync(), AccountRole.Trainer);
        var email = new string('a', 308) + "@example.com";
        var response = await app.PostAsync(client, new { nombre = "Ana", apellido = "Smith", email });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(email.ToUpperInvariant(), (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("email").GetString());
        Assert.Equal(1, await app.CountAsync());
    }

    [Fact]
    public async Task RevokedRole_PreviousCookieIsDeniedForAllStudentOperations()
    {
        await using var app = await TestApp.CreateAsync();
        using var client = await app.LoginAsync(await app.GymAsync(), AccountRole.Trainer);
        var csrf = await app.TokenAsync(client);
        using (var scope = app.Factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<GymAccount>>();
            var user = await scope.ServiceProvider.GetRequiredService<StaticXDbContext>().Users.SingleAsync();
            user.Role = AccountRole.Student;
            Assert.True((await users.UpdateAsync(user)).Succeeded);
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/students")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/students/1")).StatusCode);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/students") { Content = JsonContent.Create(Valid()) };
        request.Headers.Add("X-CSRF-TOKEN", csrf);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(request)).StatusCode);
        var update = await app.PutAsync(client, "/api/students/1", Valid());
        Assert.Equal(HttpStatusCode.Unauthorized, update.StatusCode);
        Assert.True(update.Headers.CacheControl?.NoStore);
        foreach (var action in new[] { "deactivate", "reactivate" })
        {
            var state = await app.PatchAsync(client, $"/api/students/1/{action}");
            Assert.Equal(HttpStatusCode.Unauthorized, state.StatusCode);
            Assert.True(state.Headers.CacheControl?.NoStore);
        }
        Assert.Equal(0, await app.CountAsync());
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("malformed")]
    [InlineData("zero")]
    [InlineData("other-gym")]
    [InlineData("wrong-role")]
    public async Task InvalidSignedIdentity_IsRejectedBeforeStudentAccess(string scenario)
    {
        await using var app = await TestApp.CreateAsync();
        var gym = await app.GymAsync();
        using var normal = await app.LoginAsync(gym, AccountRole.Trainer);
        using var scope = app.Factory.Services.CreateScope();
        var account = await scope.ServiceProvider.GetRequiredService<StaticXDbContext>().Users.SingleAsync();
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, account.Id), new(ClaimTypes.Role, scenario == "wrong-role" ? "GymAdmin" : "Trainer") };
        if (scenario != "missing") claims.Add(new Claim("gym_id", scenario switch { "malformed" => "not-an-int", "zero" => "0", "other-gym" => (gym + 1).ToString(), _ => gym.ToString() }));
        var options = app.Factory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(IdentityConstants.ApplicationScheme);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, IdentityConstants.ApplicationScheme)), IdentityConstants.ApplicationScheme);
        using var client = app.Client();
        client.DefaultRequestHeaders.Add("Cookie", $".AspNetCore.Identity.Application={options.TicketDataFormat.Protect(ticket)}");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/students")).StatusCode);
        var update = await app.PutAsync(client, "/api/students/1", Valid());
        Assert.Equal(HttpStatusCode.Unauthorized, update.StatusCode);
        Assert.True(update.Headers.CacheControl?.NoStore);
        Assert.Equal(0, await app.CountAsync());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Find_NonpositiveId_Returns400(int id)
    {
        await using var app = await TestApp.CreateAsync();
        using var client = await app.LoginAsync(await app.GymAsync(), AccountRole.Trainer);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/api/students/{id}")).StatusCode);
    }

    [Theory]
    [InlineData("create")]
    [InlineData("list")]
    [InlineData("find")]
    [InlineData("update")]
    [InlineData("deactivate")]
    [InlineData("reactivate")]
    public async Task UnexpectedFailure_ReturnsGenericProblemWithoutSecrets(string operation)
    {
        await using var app = await TestApp.CreateAsync(failingStudents: true);
        using var client = await app.LoginAsync(await app.GymAsync(), AccountRole.Trainer);
        var response = operation is "deactivate" or "reactivate" ? await app.PatchAsync(client, $"/api/students/1/{operation}") : operation == "update" ? await app.PutAsync(client, "/api/students/1", Valid()) : operation == "create" ? await app.PostAsync(client, Valid()) : await client.GetAsync(operation == "list" ? "/api/students" : "/api/students/1");
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("An unexpected error occurred.", body.GetProperty("title").GetString());
        Assert.DoesNotContain("secret", body.ToString());
        Assert.DoesNotContain("SELECT", body.ToString());
    }

    [Theory]
    [InlineData(AccountRole.GymAdmin)]
    [InlineData(AccountRole.Trainer)]
    public async Task Update_AllowedRoles_NormalizesPersistsAndDoesNotProvision(AccountRole role)
    {
        await using var app = await TestApp.CreateAsync();
        var gym = await app.GymAsync();
        var otherGym = await app.GymAsync();
        using var client = await app.LoginAsync(gym, role);
        var created = await app.PostAsync(client, Valid());
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        using var beforeScope = app.Factory.Services.CreateScope();
        var beforeAccount = await beforeScope.ServiceProvider.GetRequiredService<StaticXDbContext>().Users.AsNoTracking().SingleAsync();
        var beforeAccountJson = JsonSerializer.Serialize(beforeAccount);
        var response = await app.PutAsync(client, $"/api/students/{id}?gymId={otherGym}&id=2147483647", new
        {
            nombre = " Bea ", apellido = " Jones ", email = " Bea@Example.com ",
            gymId = otherGym, gimnasioId = otherGym, id = int.MaxValue
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(new[] { "activo", "apellido", "email", "id", "nombre" }, body.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal(id, body.GetProperty("id").GetInt32());
        Assert.Equal("Bea", body.GetProperty("nombre").GetString());
        Assert.Equal("Jones", body.GetProperty("apellido").GetString());
        Assert.Equal("BEA@EXAMPLE.COM", body.GetProperty("email").GetString());
        Assert.Equal(body.ToString(), (await client.GetFromJsonAsync<JsonElement>($"/api/students/{id}")).ToString());
        var unchangedEmail = await app.PutAsync(client, $"/api/students/{id}", new { nombre = "Bea", apellido = "Jones", email = " bea@example.com " });
        Assert.Equal(HttpStatusCode.OK, unchangedEmail.StatusCode);
        using var scope = app.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StaticXDbContext>();
        var student = await db.Alumnos.SingleAsync();
        Assert.Equal((id, gym, "Bea", "Jones", "BEA@EXAMPLE.COM"), (student.Id, student.GimnasioId, student.Nombre, student.Apellido, student.Email));
        Assert.Equal(beforeAccountJson, JsonSerializer.Serialize(await db.Users.SingleAsync()));
        Assert.Empty(await db.ActivationInvitations.ToListAsync());
        Assert.Empty(await db.ActivationOutbox.ToListAsync());
        Assert.Empty(await db.ProvisioningRequests.ToListAsync());
    }

    [Fact]
    public async Task Update_DuplicateRollsBackAllFields_OtherGymEmailIsAllowed()
    {
        await using var app = await TestApp.CreateAsync();
        using var client = await app.LoginAsync(await app.GymAsync(), AccountRole.Trainer);
        using var other = await app.LoginAsync(await app.GymAsync(), AccountRole.GymAdmin);
        var created = await app.PostAsync(client, Valid());
        var original = await created.Content.ReadAsStringAsync();
        var url = created.Headers.Location!.ToString();
        Assert.Equal(HttpStatusCode.Created, (await app.PostAsync(client, new { nombre = "Ben", apellido = "Jones", email = "ben@example.com" })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await app.PostAsync(other, new { nombre = "Else", apellido = "Where", email = "foreign@example.com" })).StatusCode);
        var response = await app.PutAsync(client, url, new { nombre = "Changed", apellido = "Changed", email = " BeN@Example.com " });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("A student with this email already exists in this gym.", problem.GetProperty("title").GetString());
        Assert.DoesNotContain("Sqlite", problem.ToString());
        Assert.DoesNotContain("IX_", problem.ToString());
        Assert.Equal(original, await client.GetStringAsync(url));
        using (var scope = app.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StaticXDbContext>();
            var student = await db.Alumnos.SingleAsync(s => s.Email == "ANA@EXAMPLE.COM");
            Assert.Equal(("Ana", "Smith"), (student.Nombre, student.Apellido));
            Assert.Equal(3, await db.Alumnos.CountAsync());
        }
        var allowed = await app.PutAsync(client, url, new { nombre = "New", apellido = "Name", email = " foreign@example.com " });
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.Equal("FOREIGN@EXAMPLE.COM", (await client.GetFromJsonAsync<JsonElement>(url)).GetProperty("email").GetString());
    }

    [Fact]
    public async Task Update_MissingAndCrossGym_AreIndistinguishableAndCannotBeSpoofed()
    {
        await using var app = await TestApp.CreateAsync();
        var gym = await app.GymAsync();
        var otherGym = await app.GymAsync();
        using var client = await app.LoginAsync(gym, AccountRole.Trainer);
        using var other = await app.LoginAsync(otherGym, AccountRole.GymAdmin);
        var own = await app.PostAsync(client, Valid());
        var foreign = await app.PostAsync(other, Valid());
        var original = await foreign.Content.ReadAsStringAsync();
        var foreignUrl = foreign.Headers.Location!.ToString();
        var ownId = (await own.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        var cross = await app.PutAsync(client, $"{foreignUrl}?gymId={otherGym}&gimnasioId={otherGym}&id={ownId}", new
        { nombre = "Changed", apellido = "Changed", email = "changed@example.com", gymId = otherGym, gimnasioId = otherGym, id = ownId });
        var missing = await app.PutAsync(client, "/api/students/2147483647", Valid());
        Assert.Equal(HttpStatusCode.NotFound, cross.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.True(cross.Headers.CacheControl?.NoStore);
        Assert.True(missing.Headers.CacheControl?.NoStore);
        Assert.Equal(await missing.Content.ReadAsStringAsync(), await cross.Content.ReadAsStringAsync());
        Assert.Equal(original, await other.GetStringAsync(foreignUrl));
        Assert.Equal(await own.Content.ReadAsStringAsync(), await client.GetStringAsync(own.Headers.Location));
        Assert.Equal(2, await app.CountAsync());
    }

    [Theory]
    [InlineData("name-blank")]
    [InlineData("surname-blank")]
    [InlineData("email-blank")]
    [InlineData("email-invalid")]
    [InlineData("email-long")]
    [InlineData("missing")]
    [InlineData("null")]
    [InlineData("zero")]
    [InlineData("negative")]
    public async Task Update_InvalidInput_Returns400AndPreservesAllFields(string scenario)
    {
        await using var app = await TestApp.CreateAsync();
        using var client = await app.LoginAsync(await app.GymAsync(), AccountRole.Trainer);
        var created = await app.PostAsync(client, Valid());
        var url = created.Headers.Location!.ToString();
        object body = scenario == "missing" ? new { } : new
        {
            nombre = scenario == "null" ? null : scenario == "name-blank" ? "  " : "Changed",
            apellido = scenario == "surname-blank" ? "  " : "Changed",
            email = scenario switch { "email-blank" => " ", "email-invalid" => "invalid", "email-long" => new string('a', 309) + "@example.com", _ => "changed@example.com" }
        };
        var target = scenario == "zero" ? "/api/students/0" : scenario == "negative" ? "/api/students/-1" : url;
        var response = await app.PutAsync(client, target, body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal(await created.Content.ReadAsStringAsync(), await client.GetStringAsync(url));
        using var scope = app.Factory.Services.CreateScope();
        var student = await scope.ServiceProvider.GetRequiredService<StaticXDbContext>().Alumnos.SingleAsync();
        Assert.Equal(("Ana", "Smith", "ANA@EXAMPLE.COM"), (student.Nombre, student.Apellido, student.Email));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("bad")]
    [InlineData("prelogin")]
    [InlineData("anonymous")]
    [InlineData("student")]
    public async Task Update_UnauthorizedOrInvalidCsrf_DoesNotChangeFicha(string scenario)
    {
        await using var app = await TestApp.CreateAsync();
        var gym = await app.GymAsync();
        using var client = app.Client();
        var prelogin = await app.TokenAsync(client);
        if (scenario != "anonymous")
            await app.SignInAsync(client, gym, scenario == "student" ? AccountRole.Student : AccountRole.Trainer);
        int id;
        using (var scope = app.Factory.Services.CreateScope())
        {
            id = (await scope.ServiceProvider.GetRequiredService<IStudentService>().AddAsync(gym, new CreateStudentRequest { Nombre = "Ana", Apellido = "Smith", Email = "ana@example.com" })).Id;
        }
        var url = $"/api/students/{id}";
        using var request = new HttpRequestMessage(HttpMethod.Put, url) { Content = JsonContent.Create(new { nombre = "Changed", apellido = "Changed", email = "changed@example.com" }) };
        if (scenario != "missing") request.Headers.Add("X-CSRF-TOKEN", scenario == "bad" ? "invalid" : scenario == "prelogin" ? prelogin : await app.TokenAsync(client));
        var response = await client.SendAsync(request);
        Assert.Equal(scenario == "anonymous" ? HttpStatusCode.Unauthorized : scenario == "student" ? HttpStatusCode.Forbidden : HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        using var readScope = app.Factory.Services.CreateScope();
        var actual = await readScope.ServiceProvider.GetRequiredService<StaticXDbContext>().Alumnos.SingleAsync();
        Assert.Equal((id, gym, "Ana", "Smith", "ANA@EXAMPLE.COM"), (actual.Id, actual.GimnasioId, actual.Nombre, actual.Apellido, actual.Email));
    }

    [Fact]
    public async Task Update_EmailAtMaximumLength_SucceedsAndPersists()
    {
        await using var app = await TestApp.CreateAsync();
        using var client = await app.LoginAsync(await app.GymAsync(), AccountRole.Trainer);
        var created = await app.PostAsync(client, Valid());
        var email = new string('a', 308) + "@example.com";
        var response = await app.PutAsync(client, created.Headers.Location!.ToString(), new { nombre = "New", apellido = "Name", email = $" {email} " });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(email.ToUpperInvariant(), (await client.GetFromJsonAsync<JsonElement>(created.Headers.Location)).GetProperty("email").GetString());
    }

    [Theory]
    [InlineData(AccountRole.GymAdmin)]
    [InlineData(AccountRole.Trainer)]
    public async Task State_AllowedRoles_IdempotentTransitionsPreserveFichaAndInactiveEdits(AccountRole role)
    {
        await using var app = await TestApp.CreateAsync();
        var gym = await app.GymAsync();
        using var client = await app.LoginAsync(gym, role);
        var created = await app.PostAsync(client, new { nombre = "Ana", apellido = "Smith", email = "ana@example.com", activo = false });
        var url = created.Headers.Location!.ToString();
        var initial = await created.Content.ReadFromJsonAsync<JsonElement>();
        string accountBefore;
        using (var before = app.Factory.Services.CreateScope())
            accountBefore = JsonSerializer.Serialize(await before.ServiceProvider.GetRequiredService<StaticXDbContext>().Users.AsNoTracking().SingleAsync());
        Assert.True(initial.GetProperty("activo").GetBoolean());
        foreach (var action in new[] { "deactivate", "deactivate" })
        {
            var response = await app.PatchAsync(client, $"{url}/{action}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.False(body.GetProperty("activo").GetBoolean());
            Assert.Equal(initial.GetProperty("id").GetInt32(), body.GetProperty("id").GetInt32());
            Assert.Equal("ANA@EXAMPLE.COM", body.GetProperty("email").GetString());
        }
        var edit = await app.PutAsync(client, url, new { nombre = "Bea", apellido = "Jones", email = "bea@example.com", activo = true });
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        Assert.False((await edit.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("activo").GetBoolean());
        Assert.False((await client.GetFromJsonAsync<JsonElement>(url)).GetProperty("activo").GetBoolean());
        Assert.Equal(HttpStatusCode.Conflict, (await app.PostAsync(client, new { nombre = "Other", apellido = "Name", email = "bea@example.com" })).StatusCode);
        foreach (var action in new[] { "reactivate", "reactivate" })
        {
            var response = await app.PatchAsync(client, $"{url}/{action}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(body.GetProperty("activo").GetBoolean());
            Assert.Equal("Bea", body.GetProperty("nombre").GetString());
            Assert.Equal("Jones", body.GetProperty("apellido").GetString());
            Assert.Equal("BEA@EXAMPLE.COM", body.GetProperty("email").GetString());
        }
        using var scope = app.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StaticXDbContext>();
        var persisted = await db.Alumnos.SingleAsync();
        Assert.Equal((initial.GetProperty("id").GetInt32(), gym, "Bea", "Jones", "BEA@EXAMPLE.COM", true),
            (persisted.Id, persisted.GimnasioId, persisted.Nombre, persisted.Apellido, persisted.Email, persisted.Activo));
        Assert.Equal(1, await db.Users.CountAsync());
        Assert.Equal(accountBefore, JsonSerializer.Serialize(await db.Users.SingleAsync()));
        Assert.Empty(await db.ActivationInvitations.ToListAsync());
        Assert.Empty(await db.ActivationOutbox.ToListAsync());
        Assert.Empty(await db.ProvisioningRequests.ToListAsync());
    }

    [Fact]
    public async Task StateFilter_DefaultAll_ExplicitStatesAreScopedOrderedAndStrict()
    {
        await using var app = await TestApp.CreateAsync();
        using var client = await app.LoginAsync(await app.GymAsync(), AccountRole.Trainer);
        var otherGym = await app.GymAsync();
        using var other = await app.LoginAsync(otherGym, AccountRole.GymAdmin);
        var first = await app.PostAsync(client, Valid());
        var second = await app.PostAsync(client, new { nombre = "Ben", apellido = "Jones", email = "ben@example.com" });
        await app.PostAsync(other, Valid());
        var firstId = (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        var secondId = (await second.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        Assert.Empty((await client.GetFromJsonAsync<StudentListResponse>("/api/students?activo=false"))!.Items);
        Assert.Equal(HttpStatusCode.OK, (await app.PatchAsync(client, $"{first.Headers.Location}/deactivate")).StatusCode);
        foreach (var (query, ids) in new[] { ("", new[] { firstId, secondId }), ("&activo=true", new[] { secondId }), ("&activo=false", new[] { firstId }) })
        {
            var response = await client.GetAsync($"/api/students?gymId={otherGym}{query}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
            var page = (await response.Content.ReadFromJsonAsync<StudentListResponse>())!;
            Assert.Equal(ids, page.Items.Select(s => s.Id));
            Assert.Equal(ids.Length, page.TotalCount);
        }
        foreach (var invalid in new[] { "yes", "0", "1", "invalid" })
        {
            var response = await client.GetAsync($"/api/students?activo={invalid}");
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
        }
    }

    [Theory]
    [InlineData("deactivate", "missing")]
    [InlineData("deactivate", "bad")]
    [InlineData("deactivate", "prelogin")]
    [InlineData("deactivate", "anonymous")]
    [InlineData("deactivate", "student")]
    [InlineData("reactivate", "missing")]
    [InlineData("reactivate", "bad")]
    [InlineData("reactivate", "prelogin")]
    [InlineData("reactivate", "anonymous")]
    [InlineData("reactivate", "student")]
    public async Task State_UnauthorizedOrInvalidCsrf_BodylessPatchCannotWrite(string action, string scenario)
    {
        await using var app = await TestApp.CreateAsync();
        var gym = await app.GymAsync();
        using var client = app.Client();
        var prelogin = await app.TokenAsync(client);
        if (scenario != "anonymous")
            await app.SignInAsync(client, gym, scenario == "student" ? AccountRole.Student : AccountRole.Trainer);
        int id;
        var initialActive = action == "deactivate";
        using (var scope = app.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StaticXDbContext>();
            var row = new Alumno { GimnasioId = gym, Nombre = "Ana", Apellido = "Smith", Email = "ANA@EXAMPLE.COM", Activo = initialActive };
            db.Alumnos.Add(row);
            await db.SaveChangesAsync();
            id = row.Id;
        }
        using var request = new HttpRequestMessage(HttpMethod.Patch, $"/api/students/{id}/{action}");
        if (scenario != "missing") request.Headers.Add("X-CSRF-TOKEN", scenario == "bad" ? "invalid" : scenario == "prelogin" ? prelogin : await app.TokenAsync(client));
        var response = await client.SendAsync(request);
        Assert.Equal(scenario == "anonymous" ? HttpStatusCode.Unauthorized : scenario == "student" ? HttpStatusCode.Forbidden : HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        using var readScope = app.Factory.Services.CreateScope();
        var actual = await readScope.ServiceProvider.GetRequiredService<StaticXDbContext>().Alumnos.SingleAsync();
        Assert.Equal((id, gym, "Ana", "Smith", "ANA@EXAMPLE.COM", initialActive),
            (actual.Id, actual.GimnasioId, actual.Nombre, actual.Apellido, actual.Email, actual.Activo));
    }

    [Theory]
    [InlineData(AccountRole.GymAdmin, "deactivate")]
    [InlineData(AccountRole.Trainer, "deactivate")]
    [InlineData(AccountRole.GymAdmin, "reactivate")]
    [InlineData(AccountRole.Trainer, "reactivate")]
    public async Task State_MissingCrossGymAndInvalidIds_DoNotMutateOrRevealForeignFicha(AccountRole role, string action)
    {
        await using var app = await TestApp.CreateAsync();
        using var client = await app.LoginAsync(await app.GymAsync(), role);
        var otherGym = await app.GymAsync();
        using var other = await app.LoginAsync(otherGym, AccountRole.Trainer);
        var foreign = await app.PostAsync(other, Valid());
        var cross = await app.PatchAsync(client, $"{foreign.Headers.Location}/{action}?gymId={otherGym}&gimnasioId={otherGym}");
        var missing = await app.PatchAsync(client, $"/api/students/2147483647/{action}");
        Assert.Equal(HttpStatusCode.NotFound, cross.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(await missing.Content.ReadAsStringAsync(), await cross.Content.ReadAsStringAsync());
        Assert.True(cross.Headers.CacheControl?.NoStore);
        Assert.True(missing.Headers.CacheControl?.NoStore);
        foreach (var id in new[] { 0, -1 })
        {
            var invalid = await app.PatchAsync(client, $"/api/students/{id}/{action}");
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            Assert.True(invalid.Headers.CacheControl?.NoStore);
        }
        Assert.Equal(await foreign.Content.ReadAsStringAsync(), await other.GetStringAsync(foreign.Headers.Location));
        Assert.Equal(1, await app.CountAsync());
    }

    [Fact]
    public async Task State_InactiveEmailRemainsReservedForUpdatesAndActiveUpdateCannotSpoofState()
    {
        await using var app = await TestApp.CreateAsync();
        using var client = await app.LoginAsync(await app.GymAsync(), AccountRole.Trainer);
        var inactive = await app.PostAsync(client, Valid());
        var active = await app.PostAsync(client, new { nombre = "Ben", apellido = "Jones", email = "ben@example.com" });
        Assert.Equal(HttpStatusCode.OK, (await app.PatchAsync(client, $"{inactive.Headers.Location}/deactivate")).StatusCode);
        var conflict = await app.PutAsync(client, active.Headers.Location!.ToString(), new { nombre = "Changed", apellido = "Changed", email = "ana@example.com", activo = false });
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal(await active.Content.ReadAsStringAsync(), await client.GetStringAsync(active.Headers.Location));
        var edit = await app.PutAsync(client, active.Headers.Location.ToString(), new { nombre = "Bea", apellido = "Jones", email = "bea@example.com", activo = false });
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        Assert.True((await edit.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("activo").GetBoolean());
    }

    private static object Valid() => new { nombre = "Ana", apellido = "Smith", email = "ana@example.com" };

    private sealed class FailingStudents : IStudentService
    {
        public Task<StudentResponse?> DeactivateAsync(int gimnasioId, int studentId, CancellationToken cancellationToken = default) => throw new InvalidOperationException("SELECT secret FROM credentials");
        public Task<StudentResponse?> ReactivateAsync(int gimnasioId, int studentId, CancellationToken cancellationToken = default) => throw new InvalidOperationException("SELECT secret FROM credentials");
        public Task<StudentResponse?> UpdateAsync(int gimnasioId, int studentId, UpdateStudentRequest request, CancellationToken cancellationToken = default) => throw new InvalidOperationException("SELECT secret FROM credentials");
        public Task<StudentResponse> AddAsync(int gimnasioId, CreateStudentRequest request, CancellationToken cancellationToken = default) => throw new InvalidOperationException("SELECT secret FROM credentials");
        public Task<StudentResponse?> FindAsync(int gimnasioId, int studentId, CancellationToken cancellationToken = default) => throw new InvalidOperationException("SELECT secret FROM credentials");
        public Task<StudentListResponse> ListAsync(int gimnasioId, StudentListRequest request, CancellationToken cancellationToken = default) => throw new InvalidOperationException("SELECT secret FROM credentials");
    }

    private sealed class TestApp : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        private readonly string keys;
        public WebApplicationFactory<Program> Factory { get; }

        private TestApp(SqliteConnection connection, string keys, WebApplicationFactory<Program> factory)
        {
            this.connection = connection;
            this.keys = keys;
            Factory = factory;
        }

        public static async Task<TestApp> CreateAsync(bool failingStudents = false)
        {
            var keys = Path.Combine(Path.GetTempPath(), "static-x-student-http", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(keys);
            var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
            await connection.OpenAsync();
            var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder
                .UseEnvironment("Development")
                .UseSetting("ConnectionStrings:Static-X", "Server=unused;Database=unused;Integrated Security=true")
                .UseSetting("Activation:KeyRingPath", keys)
                .ConfigureServices(services =>
                {
                    services.RemoveAll<DbContextOptions<StaticXDbContext>>();
                    services.RemoveAll<IDbContextOptionsConfiguration<StaticXDbContext>>();
                    services.AddDbContext<StaticXDbContext>(options => options.UseSqlite(connection));
                    if (failingStudents)
                    {
                        services.RemoveAll<IStudentService>();
                        services.AddScoped<IStudentService, FailingStudents>();
                    }
                }));
            using var scope = factory.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<StaticXDbContext>().Database.EnsureCreatedAsync();
            return new TestApp(connection, keys, factory);
        }

        public HttpClient Client() => Factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false, HandleCookies = true });

        public async Task<int> GymAsync()
        {
            using var scope = Factory.Services.CreateScope();
            return (await scope.ServiceProvider.GetRequiredService<IGymService>().AddAsync(new CreateGymRequest("Test gym"))).Id;
        }

        public async Task<HttpClient> LoginAsync(int gym, AccountRole role)
        {
            var client = Client();
            await SignInAsync(client, gym, role);
            return client;
        }

        public async Task SignInAsync(HttpClient client, int gym, AccountRole role)
        {
            using var scope = Factory.Services.CreateScope();
            var account = await scope.ServiceProvider.GetRequiredService<IAccountService>().CreateAsync(gym, new CreateAccountRequest("staff@example.com", "CorrectPassword1!"), role);
            var users = scope.ServiceProvider.GetRequiredService<UserManager<GymAccount>>();
            var user = await users.FindByIdAsync(account.Id);
            Assert.NotNull(user);
            user.EmailConfirmed = true;
            Assert.True((await users.UpdateAsync(user)).Succeeded);
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/account/login") { Content = JsonContent.Create(new { gymId = gym, email = "staff@example.com", password = "CorrectPassword1!" }) };
            request.Headers.Add("X-CSRF-TOKEN", await TokenAsync(client));
            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(request)).StatusCode);
        }

        public async Task<string> TokenAsync(HttpClient client)
        {
            var response = await client.GetAsync("/api/account/csrf");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
        }

        public async Task<HttpResponseMessage> PostAsync(HttpClient client, object body)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/students") { Content = JsonContent.Create(body) };
            request.Headers.Add("X-CSRF-TOKEN", await TokenAsync(client));
            return await client.SendAsync(request);
        }

        public async Task<int> CountAsync()
        {
            using var scope = Factory.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<StaticXDbContext>().Alumnos.CountAsync();
        }

        public async Task<HttpResponseMessage> PutAsync(HttpClient client, string url, object body)
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, url) { Content = JsonContent.Create(body) };
            request.Headers.Add("X-CSRF-TOKEN", await TokenAsync(client));
            return await client.SendAsync(request);
        }

        public async Task<HttpResponseMessage> PatchAsync(HttpClient client, string url)
        {
            using var request = new HttpRequestMessage(HttpMethod.Patch, url);
            request.Headers.Add("X-CSRF-TOKEN", await TokenAsync(client));
            return await client.SendAsync(request);
        }

        public async ValueTask DisposeAsync()
        {
            await Factory.DisposeAsync();
            await connection.DisposeAsync();
            Directory.Delete(keys, true);
        }
    }
}
