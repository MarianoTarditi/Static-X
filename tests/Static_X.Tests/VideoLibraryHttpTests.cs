using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DAL;
using Entity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Static_X.Tests;

public sealed class VideoLibraryHttpTests
{
    [Fact]
    public async Task Catalog_TrainerCanReadEmptyGym_StudentRequiresCoverage()
    {
        await using var app = await PaymentHttpApp.CreateAsync();
        using var trainer = await app.LoginAsync(AccountRole.Trainer);
        using var student = await app.LoginAsync(AccountRole.Student);
        using var response = await trainer.GetAsync("/api/routines/videos");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Empty(json.GetProperty("items").EnumerateArray());
        Assert.Equal(HttpStatusCode.Forbidden, (await student.GetAsync("/api/routines/videos")).StatusCode);
    }

    [Fact]
    public async Task Catalog_PopulatedPageUsesTrustedGymAndExposesOnlySafeFields()
    {
        await using var app = await PaymentHttpApp.CreateAsync();
        var videos = await SeedAsync(app);
        using var trainer = await app.LoginAsync(AccountRole.Trainer);
        using var response = await trainer.GetAsync("/api/routines/videos?gymId=2&role=SuperAdmin&category=exercise&pageSize=1&page=2");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(new[] { "items" }, json.EnumerateObject().Select(p => p.Name));
        var item = Assert.Single(json.GetProperty("items").EnumerateArray());
        Assert.Equal(new[] { "category", "description", "id", "length", "revision", "title" }, item.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal(videos[1].Id, item.GetProperty("id").GetGuid());
        Assert.Equal(videos[1].Revision, item.GetProperty("revision").GetGuid());
        Assert.Equal("Older exercise", item.GetProperty("title").GetString());
        Assert.Equal("exercise", item.GetProperty("category").GetString());
        Assert.Equal("Private demonstration", item.GetProperty("description").GetString());
        Assert.Equal(12, item.GetProperty("length").GetInt64());
        var all = await trainer.GetFromJsonAsync<JsonElement>("/api/routines/videos?gymId=2");
        Assert.Equal(videos.Take(3).Select(v => v.Id), all.GetProperty("items").EnumerateArray().Select(v => v.GetProperty("id").GetGuid()));
    }

    [Fact]
    public async Task Catalog_SameStudentCookieObservesPersistedCoverageAndExpiry()
    {
        await using var app = await PaymentHttpApp.CreateAsync();
        var videos = await SeedAsync(app);
        using var student = await app.LoginAsync(AccountRole.Student);
        using var trainer = await app.LoginAsync(AccountRole.Trainer);
        Assert.Equal(HttpStatusCode.Forbidden, (await student.GetAsync("/api/routines/videos")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await app.PostAsync(trainer, "/api/payments/students/1", await app.ConfirmationAsync(trainer))).StatusCode);
        using var response = await student.GetAsync("/api/routines/videos?gymId=2");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(videos.Take(3).Select(v => v.Id), json.GetProperty("items").EnumerateArray().Select(v => v.GetProperty("id").GetGuid()));
        app.Clock.Now = new DateTimeOffset(2026, 12, 15, 3, 0, 0, TimeSpan.Zero);
        Assert.Equal(HttpStatusCode.Forbidden, (await student.GetAsync("/api/routines/videos")).StatusCode);
    }

    [Theory]
    [InlineData(AccountRole.GymAdmin)]
    [InlineData(AccountRole.SuperAdmin)]
    public async Task Catalog_OtherRolesCannotReadEvenWithClientRoleAndGym(AccountRole role)
    {
        await using var app = await PaymentHttpApp.CreateAsync();
        using var client = await app.LoginAsync(role);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/routines/videos?gymId=1&role=Trainer")).StatusCode);
    }

    [Fact]
    public async Task Catalog_AnonymousRequiresAuthentication()
    {
        await using var app = await PaymentHttpApp.CreateAsync();
        using var client = app.Factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/routines/videos")).StatusCode);
    }

    [Theory]
    [InlineData("?page=0")]
    [InlineData("?page=100001")]
    [InlineData("?pageSize=0")]
    [InlineData("?pageSize=101")]
    [InlineData("?category=external")]
    [InlineData("?page=invalid")]
    public async Task Catalog_InvalidQueryReturnsBadRequest(string query)
    {
        await using var app = await PaymentHttpApp.CreateAsync();
        using var trainer = await app.LoginAsync(AccountRole.Trainer);
        Assert.Equal(HttpStatusCode.BadRequest, (await trainer.GetAsync("/api/routines/videos" + query)).StatusCode);
    }

    private static async Task<LibraryVideo[]> SeedAsync(PaymentHttpApp app)
    {
        using var scope = app.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StaticXDbContext>();
        var videos = new[]
        {
            Entry(1, "Newest exercise", "exercise", 4),
            Entry(1, "Older exercise", "exercise", 3),
            Entry(1, "Information", "informational", 2),
            Entry(2, "Foreign exercise", "exercise", 5)
        };
        db.Set<VideoObject>().AddRange(videos.Select(v => new VideoObject { Key = v.ObjectKey, GymId = v.GymId, State = "Active" }));
        db.Set<LibraryVideo>().AddRange(videos);
        await db.SaveChangesAsync();
        return videos;
    }

    private static LibraryVideo Entry(int gym, string title, string category, int day) => new LibraryVideo
    {
        Id = Guid.NewGuid(),
        Revision = Guid.NewGuid(),
        GymId = gym,
        Title = title,
        Category = category,
        Description = "Private demonstration",
        Length = 12,
        ObjectKey = $"video/{gym}/{Guid.NewGuid():N}",
        CreatedAt = new DateTime(2026, 11, day, 0, 0, 0, DateTimeKind.Utc)
    };
}
