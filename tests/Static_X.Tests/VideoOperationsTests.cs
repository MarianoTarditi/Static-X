using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BLL.Interfaces;
using DAL;
using Entity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Static_X.Tests;

public sealed class VideoOperationsTests
{
    // A minimal ISO BMFF container with a video track, not a filename/MIME assertion.
    internal static byte[] Mp4(byte marker = 1)
    {
        static byte[] Box(string type, byte[] payload) => BitConverter.GetBytes(IPAddress.HostToNetworkOrder(payload.Length + 8))
            .Concat(System.Text.Encoding.ASCII.GetBytes(type)).Concat(payload).ToArray();
        return Box("ftyp", System.Text.Encoding.ASCII.GetBytes("isom\0\0\0\0isommp42"))
            .Concat(Box("moov", Box("trak", Box("mdia", Box("hdlr", new byte[8].Concat(System.Text.Encoding.ASCII.GetBytes("vide")).ToArray())))))
            .Concat(Box("mdat", new byte[] { marker, 2, 3, 4 })).ToArray();
    }

    [Fact]
    public async Task UploadReplaceDelete_PreserveMetadataRevisionAndDurableCleanup()
    {
        await using var app = await PaymentHttpApp.CreateAsync();
        var store = new MemoryVideos();
        await using var factory = app.Factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        { services.RemoveAll<IVideoObjectStore>(); services.AddSingleton<IVideoObjectStore>(store); }));
        using var trainer = await LoginAsync(app, factory, AccountRole.Trainer);
        using var upload = await SendAsync(trainer, HttpMethod.Post, "/api/routines/videos", Mp4());
        Assert.Equal(HttpStatusCode.Created, upload.StatusCode);
        var original = await upload.Content.ReadFromJsonAsync<JsonElement>();
        var id = original.GetProperty("id").GetGuid();
        var revision = original.GetProperty("revision").GetGuid();
        Assert.NotNull(upload.Headers.Location);
        Assert.Equal(Mp4(), await trainer.GetByteArrayAsync($"/api/routines/videos/{id}/content"));
        using var replace = await SendAsync(trainer, HttpMethod.Put, $"/api/routines/videos/{id}/content?expectedRevision={revision}", Mp4(9));
        Assert.Equal(HttpStatusCode.OK, replace.StatusCode);
        var updated = await replace.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(id, updated.GetProperty("id").GetGuid());
        Assert.Equal("Exercise", updated.GetProperty("title").GetString());
        Assert.Equal("exercise", updated.GetProperty("category").GetString());
        Assert.Equal("Demo", updated.GetProperty("description").GetString());
        Assert.NotEqual(revision, updated.GetProperty("revision").GetGuid());
        using var stale = await SendAsync(trainer, HttpMethod.Delete, $"/api/routines/videos/{id}?expectedRevision={revision}");
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(Mp4(9), await trainer.GetByteArrayAsync($"/api/routines/videos/{id}/content"));
        using var deleted = await SendAsync(trainer, HttpMethod.Delete, $"/api/routines/videos/{id}?expectedRevision={updated.GetProperty("revision").GetGuid()}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await trainer.GetAsync($"/api/routines/videos/{id}/content")).StatusCode);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StaticXDbContext>();
        Assert.Empty(await db.Set<LibraryVideo>().ToListAsync());
        Assert.Equal(2, await db.Set<VideoObject>().CountAsync(o => o.State == "Cleanup"));
        Assert.Equal(2, store.Objects.Count); // HTTP success never depends on synchronous deletion.
    }

    [Theory]
    [InlineData("bytes=2-5", 206, 2, 4)]
    [InlineData("bytes=2-", 206, 2, -1)]
    [InlineData("bytes=-5", 206, -5, 5)]
    [InlineData("bytes=0-99999", 206, 0, -1)]
    [InlineData("bytes=99999-", 416, 0, 0)]
    [InlineData("bytes=-0", 416, 0, 0)]
    [InlineData("bytes=7-2", 416, 0, 0)]
    [InlineData("bytes=0-1,4-5", 416, 0, 0)]
    [InlineData("bytes=9223372036854775808-", 416, 0, 0)]
    public async Task Playback_ResolvesRangesWithoutDoubleSeeking(string range, int status, int offset, int count)
    {
        await using var app = await PaymentHttpApp.CreateAsync();
        var store = new MemoryVideos();
        await using var factory = app.Factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        { services.RemoveAll<IVideoObjectStore>(); services.AddSingleton<IVideoObjectStore>(store); }));
        using var trainer = await LoginAsync(app, factory, AccountRole.Trainer);
        using var upload = await SendAsync(trainer, HttpMethod.Post, "/api/routines/videos", Mp4());
        Assert.Equal(HttpStatusCode.Created, upload.StatusCode);
        var id = (await upload.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/routines/videos/{id}/content");
        request.Headers.TryAddWithoutValidation("Range", range);
        using var response = await trainer.SendAsync(request);
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
        if (status == 416)
        {
            Assert.Equal($"bytes */{Mp4().Length}", response.Content.Headers.ContentRange!.ToString());
            Assert.Equal(0, store.Reads);
        }
        else
        {
            var start = offset < 0 ? Mp4().Length + offset : offset;
            var length = count < 0 ? Mp4().Length - start : count;
            Assert.Equal(Mp4().Skip(start).Take(length), await response.Content.ReadAsByteArrayAsync());
            Assert.Equal($"bytes {start}-{start + length - 1}/{Mp4().Length}", response.Content.Headers.ContentRange!.ToString());
            Assert.Equal(length, response.Content.Headers.ContentLength);
        }
    }

    [Fact]
    public async Task InvalidFileAndMissingCsrf_DoNotWriteStorage()
    {
        await using var app = await PaymentHttpApp.CreateAsync();
        var store = new MemoryVideos();
        await using var factory = app.Factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        { services.RemoveAll<IVideoObjectStore>(); services.AddSingleton<IVideoObjectStore>(store); }));
        using var trainer = await LoginAsync(app, factory, AccountRole.Trainer);
        using var invalid = await SendAsync(trainer, HttpMethod.Post, "/api/routines/videos", new byte[32]);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using var csrf = await trainer.PostAsync("/api/routines/videos", Form(Mp4()));
        Assert.Equal(HttpStatusCode.BadRequest, csrf.StatusCode);
        Assert.Empty(store.Objects);
    }

    [Theory]
    [InlineData("audio")]
    [InlineData("truncated")]
    [InlineData("brand-only")]
    public async Task InvalidMp4Structure_IsRejectedBeforeDurableUpload(string variant)
    {
        await using var app = await PaymentHttpApp.CreateAsync();
        var store = new MemoryVideos();
        await using var factory = app.Factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        { services.RemoveAll<IVideoObjectStore>(); services.AddSingleton<IVideoObjectStore>(store); }));
        using var trainer = await LoginAsync(app, factory, AccountRole.Trainer);
        var bytes = Mp4();
        if (variant == "audio")
        {
            var index = System.Text.Encoding.ASCII.GetString(bytes).IndexOf("vide", StringComparison.Ordinal);
            System.Text.Encoding.ASCII.GetBytes("soun").CopyTo(bytes, index);
        }
        if (variant == "truncated") bytes = bytes[..^1];
        if (variant == "brand-only") bytes = bytes[..24];
        using var response = await SendAsync(trainer, HttpMethod.Post, "/api/routines/videos", bytes);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(store.Objects);
        using var scope = factory.Services.CreateScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<StaticXDbContext>().Set<VideoObject>().ToListAsync());
    }

    [Fact]
    public async Task OversizedFile_Returns413WithoutOpeningOrPersistingTheFile()
    {
        await using var app = await PaymentHttpApp.CreateAsync();
        using var scope = app.Factory.Services.CreateScope();
        var controller = new Static_X.API.Controllers.VideoLibraryController(
            scope.ServiceProvider.GetRequiredService<BLL.Services.Interfaces.IVideoLibraryService>(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Static_X.API.Controllers.VideoLibraryController>.Instance);
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        context.User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(new[]
        {
            new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, "Trainer"),
            new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, "Trainer"),
            new System.Security.Claims.Claim("gym_id", "1")
        }, "test"));
        controller.ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext { HttpContext = context };
        using var content = new MemoryStream();
        var file = new Microsoft.AspNetCore.Http.FormFile(content, 0, 100000001, "file", "fake.mp4");
        var result = Assert.IsType<Microsoft.AspNetCore.Mvc.ObjectResult>(await controller.Upload(new BLL.DTOs.Students.CreateVideoRequest(), file, default));
        Assert.Equal(413, result.StatusCode);
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<StaticXDbContext>().Set<VideoObject>().ToListAsync());
    }

    [Fact]
    public async Task PlaybackAndWrites_EnforceRolesLiveCoverageAndGymIsolation()
    {
        await using var app = await PaymentHttpApp.CreateAsync();
        var store = new MemoryVideos();
        await using var factory = app.Factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        { services.RemoveAll<IVideoObjectStore>(); services.AddSingleton<IVideoObjectStore>(store); }));
        using var trainer = await LoginAsync(app, factory, AccountRole.Trainer);
        using var student = await LoginAsync(app, factory, AccountRole.Student);
        using var upload = await SendAsync(trainer, HttpMethod.Post, "/api/routines/videos", Mp4());
        var entry = await upload.Content.ReadFromJsonAsync<JsonElement>();
        var id = entry.GetProperty("id").GetGuid();
        var revision = entry.GetProperty("revision").GetGuid();
        var contentUrl = $"/api/routines/videos/{id}/content";
        Assert.Equal(HttpStatusCode.Forbidden, (await student.GetAsync(contentUrl)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await app.PostAsync(trainer, "/api/payments/students/1", await app.ConfirmationAsync(trainer))).StatusCode);
        using (var request = new HttpRequestMessage(HttpMethod.Get, contentUrl))
        {
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(1, 3);
            using var response = await student.SendAsync(request);
            Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
            Assert.Equal(Mp4().Skip(1).Take(3), await response.Content.ReadAsByteArrayAsync());
        }
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(student, HttpMethod.Post, "/api/routines/videos", Mp4())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(student, HttpMethod.Put, contentUrl + $"?expectedRevision={revision}", Mp4())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(student, HttpMethod.Delete, $"/api/routines/videos/{id}?expectedRevision={revision}")).StatusCode);
        app.Clock.Now = new DateTimeOffset(2026, 12, 15, 3, 0, 0, TimeSpan.Zero);
        using (var request = new HttpRequestMessage(HttpMethod.Get, contentUrl))
        {
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(1, 3);
            Assert.Equal(HttpStatusCode.Forbidden, (await student.SendAsync(request)).StatusCode);
        }
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StaticXDbContext>();
        await db.Set<LibraryVideo>().Where(v => v.Id == id).ExecuteUpdateAsync(update => update.SetProperty(v => v.GymId, 2));
        Assert.Equal(HttpStatusCode.NotFound, (await trainer.GetAsync(contentUrl)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(trainer, HttpMethod.Put, contentUrl + $"?expectedRevision={revision}", Mp4())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(trainer, HttpMethod.Delete, $"/api/routines/videos/{id}?expectedRevision={revision}")).StatusCode);
        Assert.Single(store.Objects);
    }

    [Fact]
    public async Task FailedAndStaleReplacement_LeaveCurrentBytesPlayableAndPendingCleanupDurable()
    {
        await using var app = await PaymentHttpApp.CreateAsync();
        var store = new MemoryVideos();
        await using var factory = app.Factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        { services.RemoveAll<IVideoObjectStore>(); services.AddSingleton<IVideoObjectStore>(store); }));
        using var trainer = await LoginAsync(app, factory, AccountRole.Trainer);
        using var upload = await SendAsync(trainer, HttpMethod.Post, "/api/routines/videos", Mp4());
        var entry = await upload.Content.ReadFromJsonAsync<JsonElement>();
        var id = entry.GetProperty("id").GetGuid();
        var revision = entry.GetProperty("revision").GetGuid();
        var url = $"/api/routines/videos/{id}/content";
        store.FailPut = true;
        Assert.Equal(HttpStatusCode.InternalServerError, (await SendAsync(trainer, HttpMethod.Put, url + $"?expectedRevision={revision}", Mp4(8))).StatusCode);
        Assert.Equal(Mp4(), await trainer.GetByteArrayAsync(url));
        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(trainer, HttpMethod.Put, url + $"?expectedRevision={Guid.NewGuid()}", Mp4(8))).StatusCode);
        Assert.Equal(Mp4(), await trainer.GetByteArrayAsync(url));
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StaticXDbContext>();
        Assert.Equal(revision, (await db.Set<LibraryVideo>().SingleAsync()).Revision);
        Assert.Equal(1, await db.Set<VideoObject>().CountAsync(o => o.State == "Pending"));
        Assert.Single(store.Objects);
    }

    private static async Task<HttpClient> LoginAsync(PaymentHttpApp app, WebApplicationFactory<Program> factory, AccountRole role)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        using var login = await app.PostAsync(client, "/api/account/login", new { gymId = 1, email = role + "@example.invalid", password = "CorrectPassword1!" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return client;
    }

    private static MultipartFormDataContent Form(byte[] bytes)
    {
        var form = new MultipartFormDataContent();
        form.Add(new StringContent("Exercise"), "title");
        form.Add(new StringContent("exercise"), "category");
        form.Add(new StringContent("Demo"), "description");
        form.Add(new ByteArrayContent(bytes), "file", "untrusted.bin");
        return form;
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, byte[]? bytes = null)
    {
        var token = (await client.GetFromJsonAsync<JsonElement>("/api/account/csrf")).GetProperty("token").GetString();
        using var request = new HttpRequestMessage(method, path) { Content = bytes is null ? null : Form(bytes) };
        request.Headers.Add("X-CSRF-TOKEN", token);
        return await client.SendAsync(request);
    }

    private sealed class MemoryVideos : IVideoObjectStore
    {
        public Dictionary<string, byte[]> Objects { get; } = new();
        public int Reads { get; private set; }
        public bool FailPut { get; set; }
        public async Task PutAsync(string key, Stream content, long length, CancellationToken cancellationToken)
        { if (FailPut) throw new IOException("Injected PUT failure."); using var buffer = new MemoryStream(); await content.CopyToAsync(buffer, cancellationToken); Objects.Add(key, buffer.ToArray()); }
        public Task<Stream> OpenReadAsync(string key, long offset, long length, CancellationToken cancellationToken)
        { Reads++; return Task.FromResult<Stream>(new MemoryStream(Objects[key].Skip((int)offset).Take((int)length).ToArray())); }
        public Task DeleteAsync(string key, CancellationToken cancellationToken) { Objects.Remove(key); return Task.CompletedTask; }
    }
}
