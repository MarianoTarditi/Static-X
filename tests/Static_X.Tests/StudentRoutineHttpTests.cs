using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BLL.DTOs.Students;
using BLL.Services.Interfaces;
using DAL;
using Entity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Static_X.Tests;

public sealed class StudentRoutineHttpTests
{
    private const string TrainerPath = "/api/students/1/routine";
    private const string StudentPath = "/api/student/me/routine";

    [Fact]
    public async Task Publish_DurationOnlyAndLegacyRepetitions_RoundTripInOrderForBothRoles()
    {
        await using var app = await PaymentHttpApp.CreateAsync();
        using var trainer = await app.LoginAsync(AccountRole.Trainer);
        using var student = await app.LoginAsync(AccountRole.Student);
        var confirmation = await app.ConfirmationAsync(trainer);
        using var payment = await app.PostAsync(trainer, "/api/payments/students/1", confirmation);
        Assert.Equal(HttpStatusCode.Created, payment.StatusCode);
        using var response = await PutAsync(trainer, TrainerPath, new
        {
            expectedRevision = (Guid?)null,
            days = new[]
            {
                new { exercises = new object[] { new { name = "Plank", sets = 3, durationSeconds = 45, restSeconds = 20 }, Exercise("Row") } },
                new { exercises = new object[] { new { name = "Carry", sets = 1, repetitions = (int?)null, durationSeconds = 1 } } }
            }
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var published = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("routine");
        foreach (var routine in new[] { published, await ReadAsync(trainer, TrainerPath), await ReadAsync(student, StudentPath) })
        {
            Assert.Equal(published.GetProperty("revision").GetGuid(), routine.GetProperty("revision").GetGuid());
            Assert.Equal(new[] { "Plank", "Row" }, Names(routine.GetProperty("days")[0]));
            Assert.Equal("Carry", Assert.Single(Names(routine.GetProperty("days")[1])));
            var timed = routine.GetProperty("days")[0].GetProperty("exercises")[0];
            Assert.Equal(JsonValueKind.Null, timed.GetProperty("repetitions").ValueKind);
            Assert.Equal(45, timed.GetProperty("durationSeconds").GetInt32());
            Assert.Equal(3, timed.GetProperty("sets").GetInt32());
            Assert.Equal(20, timed.GetProperty("restSeconds").GetInt32());
            var legacy = routine.GetProperty("days")[0].GetProperty("exercises")[1];
            Assert.Equal(8, legacy.GetProperty("repetitions").GetInt32());
            Assert.Equal(JsonValueKind.Null, legacy.GetProperty("durationSeconds").ValueKind);
            Assert.Equal(1, routine.GetProperty("days")[1].GetProperty("exercises")[0].GetProperty("durationSeconds").GetInt32());
        }
        using var replacement = await PutAsync(trainer, TrainerPath, Publication(published.GetProperty("revision").GetGuid()));
        Assert.Equal(HttpStatusCode.OK, replacement.StatusCode);
        var replaced = await ReadAsync(student, StudentPath);
        Assert.NotEqual(published.GetProperty("revision").GetGuid(), replaced.GetProperty("revision").GetGuid());
        var remaining = Assert.Single(Assert.Single(replaced.GetProperty("days").EnumerateArray()).GetProperty("exercises").EnumerateArray());
        Assert.Equal("Squat", remaining.GetProperty("name").GetString());
        Assert.Equal(8, remaining.GetProperty("repetitions").GetInt32());
        Assert.Equal(JsonValueKind.Null, remaining.GetProperty("durationSeconds").ValueKind);
    }

    [Theory]
    [InlineData(8, 45)]
    [InlineData(null, null)]
    [InlineData(0, null)]
    [InlineData(-1, null)]
    [InlineData(null, 0)]
    [InlineData(null, -1)]
    [InlineData(0, 45)]
    [InlineData(8, 0)]
    public async Task Publish_InvalidExerciseTarget_DoesNotReplaceRoutine(int? repetitions, int? durationSeconds)
    {
        await using var app = await PaymentHttpApp.CreateAsync();
        using var trainer = await app.LoginAsync(AccountRole.Trainer);
        using var first = await PutAsync(trainer, TrainerPath, Publication(null));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var original = await ReadAsync(trainer, TrainerPath);
        var invalidBody = new
        {
            expectedRevision = original.GetProperty("revision").GetGuid(),
            days = new[] { new { exercises = new[] { new { name = "Plank", sets = 3, repetitions, durationSeconds } } } }
        };
        using var scope = app.Factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IRoutineService>();
        var request = JsonSerializer.Deserialize<PublishRoutineRequest>(JsonSerializer.Serialize(invalidBody),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        await Assert.ThrowsAsync<ArgumentException>(() => service.PublishAsync(1, AccountRole.Trainer, "Trainer", 1, request, default));
        using var invalid = await PutAsync(trainer, TrainerPath, invalidBody);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal("application/problem+json", invalid.Content.Headers.ContentType?.MediaType);
        Assert.Equal(original.GetRawText(), (await ReadAsync(trainer, TrainerPath)).GetRawText());
    }

    [Fact]
    public async Task Publish_ReplacesEntireRoutineOnlyAtCurrentRevision_WithoutChangingProfile()
    {
        await using var app = await PaymentHttpApp.CreateAsync();
        using var trainer = await app.LoginAsync(AccountRole.Trainer);
        Assert.Equal(JsonValueKind.Null, (await ReadAsync(trainer, TrainerPath)).ValueKind);
        var first = await PutAsync(trainer, TrainerPath, Publication(null));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.EndsWith(TrainerPath, first.Headers.Location!.ToString());
        var original = (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("routine");
        var revision = original.GetProperty("revision").GetGuid();
        Assert.NotEqual(Guid.Empty, revision);
        Assert.Equal(original.GetRawText(), (await ReadAsync(trainer, TrainerPath)).GetRawText());

        Assert.Equal(HttpStatusCode.Conflict, (await PutAsync(trainer, TrainerPath, Publication(null))).StatusCode);
        var replacement = await PutAsync(trainer, TrainerPath, Publication(revision, "Row"));
        Assert.Equal(HttpStatusCode.OK, replacement.StatusCode);
        var current = (await replacement.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("routine");
        Assert.NotEqual(revision, current.GetProperty("revision").GetGuid());
        Assert.Equal("Row", Assert.Single(Assert.Single(current.GetProperty("days").EnumerateArray())
            .GetProperty("exercises").EnumerateArray()).GetProperty("name").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await PutAsync(trainer, TrainerPath, Publication(revision))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PutAsync(trainer, TrainerPath,
            new { expectedRevision = current.GetProperty("revision").GetGuid(), days = Array.Empty<object>() })).StatusCode);
        Assert.Equal(current.GetRawText(), (await ReadAsync(trainer, TrainerPath)).GetRawText());

        using var scope = app.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StaticXDbContext>();
        Assert.Equal(2, await db.Alumnos.CountAsync());
        var profile = await db.Alumnos.SingleAsync(s => s.Id == 1);
        Assert.Equal("Own", profile.Apellido);
        Assert.Equal("Student", profile.AccountId);
    }

    [Fact]
    public async Task Publish_LegacyPayloadThenMetadataReplacement_RoundTripsOnBothReads()
    {
        await using var app = await PaymentHttpApp.CreateAsync();
        using var trainer = await app.LoginAsync(AccountRole.Trainer);
        using var student = await app.LoginAsync(AccountRole.Student);
        var confirmation = await app.ConfirmationAsync(trainer);
        using var payment = await app.PostAsync(trainer, "/api/payments/students/1", confirmation);
        Assert.Equal(HttpStatusCode.Created, payment.StatusCode);

        using var first = await PutAsync(trainer, TrainerPath, Publication(null));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var original = (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("routine");
        var revision = original.GetProperty("revision").GetGuid();
        Assert.Equal("Squat", Assert.Single(Names(original.GetProperty("days")[0])));

        using var response = await PutAsync(trainer, TrainerPath, new
        {
            expectedRevision = revision,
            title = "  Strength block  ",
            instructions = "  Warm up first.\nLog each set.  ",
            days = new[]
            {
                new { name = "  Lower body  ", instructions = "\tStart light. ", exercises = new[] { Exercise("Squat") } },
                new { name = "  Upper body  ", instructions = " Rest between sets. ", exercises = new[] { Exercise("Row") } }
            }
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var published = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("routine");
        Assert.NotEqual(revision, published.GetProperty("revision").GetGuid());

        foreach (var routine in new[] { published, await ReadAsync(trainer, TrainerPath), await ReadAsync(student, StudentPath) })
        {
            Assert.True(routine.TryGetProperty("title", out var title), "The routine response must include title.");
            Assert.Equal("Strength block", title.GetString());
            Assert.Equal("Warm up first.\nLog each set.", routine.GetProperty("instructions").GetString());
            Assert.Equal(published.GetProperty("revision").GetGuid(), routine.GetProperty("revision").GetGuid());
            Assert.Collection(routine.GetProperty("days").EnumerateArray(),
                day => AssertDay(day, "Lower body", "Start light.", "Squat"),
                day => AssertDay(day, "Upper body", "Rest between sets.", "Row"));
        }

        AssertMetadataCleared(original);
    }

    [Theory]
    [InlineData("omitted")]
    [InlineData("null")]
    [InlineData("blank")]
    public async Task Publish_ReplacementClearsPreviouslySavedMetadata(string mode)
    {
        await using var app = await PaymentHttpApp.CreateAsync();
        using var trainer = await app.LoginAsync(AccountRole.Trainer);
        using var first = await PutAsync(trainer, TrainerPath, MetadataPublication(null, "Title", "Routine notes", "Day", "Day notes"));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var original = await ReadAsync(trainer, TrainerPath);
        Assert.Equal("Title", original.GetProperty("title").GetString());
        Assert.Equal("Routine notes", original.GetProperty("instructions").GetString());
        AssertDay(original.GetProperty("days")[0], "Day", "Day notes", "Squat");
        var revision = original.GetProperty("revision").GetGuid();
        var blank = mode == "blank" ? " \t\n " : null;
        using var replacement = await PutAsync(trainer, TrainerPath, mode == "omitted"
            ? Publication(revision) : MetadataPublication(revision, blank, blank, blank, blank));
        Assert.Equal(HttpStatusCode.OK, replacement.StatusCode);
        AssertMetadataCleared((await replacement.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("routine"));
        var saved = await ReadAsync(trainer, TrainerPath);
        AssertMetadataCleared(saved);
        Assert.NotEqual(revision, saved.GetProperty("revision").GetGuid());
    }

    [Theory]
    [InlineData("title", 201)]
    [InlineData("instructions", 2001)]
    [InlineData("days[0].name", 201)]
    [InlineData("days[0].instructions", 2001)]
    public async Task Publish_AcceptsExactMetadataLimitsButRejectsOverflowWithoutReplacing(string field, int rejectedLength)
    {
        await using var app = await PaymentHttpApp.CreateAsync();
        using var trainer = await app.LoginAsync(AccountRole.Trainer);
        var label = new string('n', 200);
        var instructions = new string('i', 2000);
        using var first = await PutAsync(trainer, TrainerPath, MetadataPublication(null, label, instructions, label, instructions));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var original = await ReadAsync(trainer, TrainerPath);
        var revision = original.GetProperty("revision").GetGuid();
        var overflow = new string('x', rejectedLength);
        using var invalid = await PutAsync(trainer, TrainerPath, MetadataPublication(revision,
            field == "title" ? overflow : label, field == "instructions" ? overflow : instructions,
            field == "days[0].name" ? overflow : label, field == "days[0].instructions" ? overflow : instructions));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal("application/problem+json", invalid.Content.Headers.ContentType?.MediaType);
        var saved = await ReadAsync(trainer, TrainerPath);
        Assert.Equal(revision, saved.GetProperty("revision").GetGuid());
        Assert.Equal(label, saved.GetProperty("title").GetString());
        Assert.Equal(instructions, saved.GetProperty("instructions").GetString());
        AssertDay(Assert.Single(saved.GetProperty("days").EnumerateArray()), label, instructions, "Squat");
    }

    private static void AssertDay(JsonElement day, string name, string instructions, string exercise)
    {
        Assert.Equal(name, day.GetProperty("name").GetString());
        Assert.Equal(instructions, day.GetProperty("instructions").GetString());
        Assert.Equal(exercise, Assert.Single(Names(day)));
    }

    private static void AssertMetadataCleared(JsonElement routine)
    {
        Assert.Equal(JsonValueKind.Null, routine.GetProperty("title").ValueKind);
        Assert.Equal(JsonValueKind.Null, routine.GetProperty("instructions").ValueKind);
        var day = Assert.Single(routine.GetProperty("days").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, day.GetProperty("name").ValueKind);
        Assert.Equal(JsonValueKind.Null, day.GetProperty("instructions").ValueKind);
    }

    [Fact]
    public async Task Student_RequiresLiveCoverageAndReadsOnlyLinkedRoutineInSavedOrder()
    {
        await using var app = await PaymentHttpApp.CreateAsync();
        using var student = await app.LoginAsync(AccountRole.Student);
        using var trainer = await app.LoginAsync(AccountRole.Trainer);
        Assert.Equal(HttpStatusCode.Forbidden, (await student.GetAsync(StudentPath)).StatusCode);
        var confirmation = await app.ConfirmationAsync(trainer);
        Assert.Equal(HttpStatusCode.Created, (await app.PostAsync(trainer, "/api/payments/students/1", confirmation)).StatusCode);
        Assert.Equal(JsonValueKind.Null, (await ReadAsync(student, StudentPath)).ValueKind);

        using (var scope = app.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StaticXDbContext>();
            db.Alumnos.Add(new Alumno { Id = 3, GimnasioId = 1, Nombre = "Other", Apellido = "Profile", Email = "other@example.invalid" });
            await db.SaveChangesAsync();
        }
        Assert.Equal(HttpStatusCode.Created, (await PutAsync(trainer, "/api/students/3/routine", Publication(null, "Other"))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await PutAsync(trainer, TrainerPath, new
        {
            expectedRevision = (Guid?)null,
            days = new[] { new { exercises = new[] { Exercise("Squat"), Exercise("Press") } }, new { exercises = new[] { Exercise("Carry") } } }
        })).StatusCode);

        var routine = await ReadAsync(student, StudentPath + "?studentId=3&gymId=2");
        Assert.Collection(routine.GetProperty("days").EnumerateArray(),
            day => Assert.Equal(new[] { "Squat", "Press" }, Names(day)),
            day => Assert.Equal(new[] { "Carry" }, Names(day)));
        var exercise = routine.GetProperty("days")[0].GetProperty("exercises")[0];
        Assert.Equal(3, exercise.GetProperty("sets").GetInt32());
        Assert.Equal(8, exercise.GetProperty("repetitions").GetInt32());
        Assert.Equal(60, exercise.GetProperty("restSeconds").GetInt32());
        Assert.Equal("Controlled", exercise.GetProperty("notes").GetString());

        app.Clock.Now = new DateTimeOffset(2026, 12, 15, 3, 0, 0, TimeSpan.Zero);
        Assert.Equal(HttpStatusCode.Forbidden, (await student.GetAsync(StudentPath)).StatusCode);
        app.Clock.Now = new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(HttpStatusCode.OK, (await student.GetAsync(StudentPath)).StatusCode);

        using (var scope = app.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StaticXDbContext>();
            await db.Alumnos.Where(s => s.Id == 1).ExecuteUpdateAsync(s => s.SetProperty(x => x.AccountId, (string?)null));
        }
        Assert.Equal(HttpStatusCode.Forbidden, (await student.GetAsync(StudentPath)).StatusCode);
    }

    [Fact]
    public async Task Routes_EnforceAuthenticationRoleTenantAndGlobalCsrf()
    {
        await using var app = await PaymentHttpApp.CreateAsync();
        using var anonymous = app.Factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(TrainerPath)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(StudentPath)).StatusCode);
        using var trainer = await app.LoginAsync(AccountRole.Trainer);
        using var admin = await app.LoginAsync(AccountRole.GymAdmin);
        using var global = await app.LoginAsync(AccountRole.SuperAdmin);
        using var student = await app.LoginAsync(AccountRole.Student);
        foreach (var other in new[] { admin, global, student })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync(TrainerPath)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await PutAsync(other, TrainerPath, Publication(null))).StatusCode);
        }
        Assert.Equal(HttpStatusCode.Forbidden, (await trainer.GetAsync(StudentPath)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await trainer.PutAsJsonAsync(TrainerPath, Publication(null))).StatusCode);
        foreach (var id in new[] { 2, 999 })
        {
            var path = $"/api/students/{id}/routine?gymId=2";
            Assert.Equal(HttpStatusCode.NotFound, (await trainer.GetAsync(path)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await PutAsync(trainer, path, Publication(null))).StatusCode);
        }
        using var scope = app.Factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<StaticXDbContext>().Users.Where(u => u.Id == "Trainer")
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.IsEnabled, false));
        Assert.Equal(HttpStatusCode.Unauthorized, (await trainer.GetAsync(TrainerPath)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await trainer.PutAsJsonAsync(TrainerPath, Publication(null))).StatusCode);
    }

    [Theory]
    [InlineData("{\"days\":[]}")]
    [InlineData("{\"expectedRevision\":null,\"days\":[]}")]
    [InlineData("{\"expectedRevision\":null,\"days\":[{\"exercises\":[]}]}")]
    [InlineData("{\"expectedRevision\":null,\"days\":[{\"exercises\":[null]}]}")]
    [InlineData("{\"expectedRevision\":null,\"days\":[{\"exercises\":[{\"name\":\" \",\"sets\":1,\"repetitions\":1}]}]}")]
    [InlineData("{\"expectedRevision\":null,\"days\":[{\"exercises\":[{\"name\":\"Row\",\"sets\":0,\"repetitions\":1}]}]}")]
    [InlineData("{\"expectedRevision\":null,\"days\":[{\"exercises\":[{\"name\":\"Row\",\"sets\":1,\"repetitions\":-1}]}]}")]
    public async Task InvalidPublication_ReturnsValidationErrorAndDoesNotCreateRoutine(string json)
    {
        await using var app = await PaymentHttpApp.CreateAsync();
        using var trainer = await app.LoginAsync(AccountRole.Trainer);
        var body = JsonSerializer.Deserialize<JsonElement>(json);
        var response = await PutAsync(trainer, TrainerPath, body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal(JsonValueKind.Null, (await ReadAsync(trainer, TrainerPath)).ValueKind);
    }

    [Fact]
    public async Task OmittedRevision_WithOtherwiseValidContent_ReturnsBadRequest()
    {
        await using var app = await PaymentHttpApp.CreateAsync();
        using var trainer = await app.LoginAsync(AccountRole.Trainer);
        Assert.Equal(HttpStatusCode.BadRequest, (await PutAsync(trainer, TrainerPath,
            new { days = new[] { new { exercises = new[] { Exercise("Row") } } } })).StatusCode);
    }

    [Fact]
    public async Task OpenApi_ListsAllThreeRoutineOperations()
    {
        await using var app = await PaymentHttpApp.CreateAsync();
        using var client = app.Factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        var paths = (await client.GetFromJsonAsync<JsonElement>("/openapi/v1.json")).GetProperty("paths");
        var trainer = paths.GetProperty("/api/students/{studentId}/routine");
        Assert.True(trainer.TryGetProperty("get", out _));
        Assert.True(trainer.TryGetProperty("put", out _));
        Assert.True(paths.GetProperty(StudentPath).TryGetProperty("get", out _));
    }

    private static object Publication(Guid? expectedRevision, string name = "Squat") =>
        new { expectedRevision, days = new[] { new { exercises = new[] { Exercise(name) } } } };

    private static object MetadataPublication(Guid? expectedRevision, string? title, string? instructions,
        string? dayName, string? dayInstructions) => new
        {
            expectedRevision, title, instructions,
            days = new[] { new { name = dayName, instructions = dayInstructions, exercises = new[] { Exercise("Squat") } } }
        };

    private static object Exercise(string name) => new { name, sets = 3, repetitions = 8, restSeconds = 60, notes = "Controlled" };

    private static IEnumerable<string?> Names(JsonElement day) => day.GetProperty("exercises").EnumerateArray()
        .Select(exercise => exercise.GetProperty("name").GetString());

    private static async Task<JsonElement> ReadAsync(HttpClient client, string path) =>
        (await client.GetFromJsonAsync<JsonElement>(path)).GetProperty("routine");

    private static async Task<HttpResponseMessage> PutAsync(HttpClient client, string path, object body)
    {
        var token = (await client.GetFromJsonAsync<JsonElement>("/api/account/csrf")).GetProperty("token").GetString();
        using var request = new HttpRequestMessage(HttpMethod.Put, path) { Content = JsonContent.Create(body) };
        request.Headers.Add("X-CSRF-TOKEN", token);
        return await client.SendAsync(request);
    }
}
