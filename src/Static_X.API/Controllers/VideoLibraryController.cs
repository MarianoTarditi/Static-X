using System.Security.Claims;
using BLL.DTOs.Students;
using BLL.Services.Interfaces;
using Entity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Static_X.API.Auth;
using Static_X.API.Integrations.VideoLibrary;
using Microsoft.Net.Http.Headers;

namespace Static_X.API.Controllers;

[ApiController]
[Route("api/routines/videos")]
[Authorize(Roles = "Trainer,Student")]
public sealed class VideoLibraryController : ControllerBase
{
    private readonly IVideoLibraryService videos;
    private readonly ILogger<VideoLibraryController> logger;

    public VideoLibraryController(IVideoLibraryService videos, ILogger<VideoLibraryController> logger)
    {
        this.videos = videos;
        this.logger = logger;
    }

    [HttpPost]
    [Authorize(Roles = "Trainer")]
    [RequestSizeLimit(101000000)]
    [RequestFormLimits(MultipartBodyLengthLimit = 101000000)]
    [Consumes("multipart/form-data")]
    [EndpointSummary("Upload one private MP4 video to the authenticated Trainer's gym.")]
    [ProducesResponseType<VideoResponse>(StatusCodes.Status201Created)]
    public Task<IActionResult> Upload([FromForm] CreateVideoRequest metadata, IFormFile file, CancellationToken cancellationToken) =>
        ExecuteAsync(async (gym, role, actor) =>
        {
            if (file.Length > 100000000) return Problem(statusCode: 413, title: "Video exceeds 100 MB.");
            if (Request.Form.Files.Count != 1) throw new ArgumentException();
            await using var stream = file.OpenReadStream();
            await VideoFileValidator.ValidateAsync(stream, file.Length, cancellationToken);
            var result = await videos.UploadAsync(gym, role, actor, metadata, stream, file.Length, cancellationToken);
            return Created($"/api/routines/videos/{result.Id}/content", result);
        });

    [HttpPut("{videoId:guid}/content")]
    [Authorize(Roles = "Trainer")]
    [RequestSizeLimit(101000000)]
    [RequestFormLimits(MultipartBodyLengthLimit = 101000000)]
    [Consumes("multipart/form-data")]
    [EndpointSummary("Replace private video bytes using the current catalog revision.")]
    [ProducesResponseType<VideoResponse>(StatusCodes.Status200OK)]
    public Task<IActionResult> Replace(Guid videoId, [FromQuery] Guid expectedRevision, IFormFile file, CancellationToken cancellationToken) =>
        ExecuteAsync(async (gym, role, actor) =>
        {
            if (file.Length > 100000000) return Problem(statusCode: 413, title: "Video exceeds 100 MB.");
            if (Request.Form.Files.Count != 1) throw new ArgumentException();
            await using var stream = file.OpenReadStream();
            await VideoFileValidator.ValidateAsync(stream, file.Length, cancellationToken);
            return Ok(await videos.ReplaceAsync(gym, role, actor, videoId, expectedRevision, stream, file.Length, cancellationToken));
        });

    [HttpDelete("{videoId:guid}")]
    [Authorize(Roles = "Trainer")]
    [EndpointSummary("Remove a video and durably schedule private object cleanup.")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public Task<IActionResult> Delete(Guid videoId, [FromQuery] Guid expectedRevision, CancellationToken cancellationToken) =>
        ExecuteAsync(async (gym, role, actor) =>
        {
            await videos.DeleteAsync(gym, role, actor, videoId, expectedRevision, cancellationToken);
            return NoContent();
        });

    [HttpGet("{videoId:guid}/content")]
    [EndpointSummary("Stream the current private MP4, optionally using one byte range.")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status206PartialContent)]
    [ProducesResponseType(StatusCodes.Status416RangeNotSatisfiable)]
    public Task<IActionResult> Content(Guid videoId, CancellationToken cancellationToken) =>
        ExecuteAsync(async (gym, role, _) =>
        {
            var range = new VideoRangeRequest();
            if (Request.Headers.ContainsKey(HeaderNames.Range))
            {
                if (!RangeHeaderValue.TryParse(Request.Headers.Range.ToString(), out var parsed) ||
                    !string.Equals(parsed.Unit.ToString(), "bytes", StringComparison.OrdinalIgnoreCase) || parsed.Ranges.Count != 1)
                    range.Suffix = 0; // Resolve against the authorized entry to return its total in 416.
                else
                {
                    var item = parsed.Ranges.Single();
                    if (item.From.HasValue) { range.Start = item.From; range.End = item.To; }
                    else range.Suffix = item.To;
                }
            }
            var result = await videos.ReadAsync(gym, role, videoId, range, cancellationToken);
            Response.Headers.AcceptRanges = "bytes";
            if (!result.Satisfiable)
            {
                await result.DisposeAsync();
                Response.Headers.ContentRange = FormattableString.Invariant($"bytes */{result.TotalLength}");
                return StatusCode(416);
            }
            Response.StatusCode = result.Partial ? 206 : 200;
            Response.ContentLength = result.Length;
            Response.Headers.ETag = $"\"{result.Revision}\"";
            if (result.Partial) Response.Headers.ContentRange = FormattableString.Invariant($"bytes {result.Start}-{result.Start + result.Length - 1}/{result.TotalLength}");
            // Storage has already sliced the stream. MVC must not seek or apply the range a second time.
            return File(result.Content!, "video/mp4", enableRangeProcessing: false);
        });

    private async Task<IActionResult> ExecuteAsync(Func<int, AccountRole, string, Task<IActionResult>> operation)
    {
        Response.Headers.CacheControl = "no-store";
        if (!AccountScope.TryGetGymId(User, out var gym) || gym is null ||
            !Enum.TryParse<AccountRole>(User.FindFirstValue(ClaimTypes.Role), out var role) ||
            string.IsNullOrWhiteSpace(User.FindFirstValue(ClaimTypes.NameIdentifier))) return Forbid();
        try { return await operation(gym.Value, role, User.FindFirstValue(ClaimTypes.NameIdentifier)!); }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (ArgumentException) { return Problem(statusCode: 400, title: "Invalid video request or MP4 file."); }
        catch (InvalidOperationException) { return Problem(statusCode: 409, title: "Video revision or upload state conflict."); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError("Video operation failed.");
            return Problem(statusCode: 500, title: "Video operation failed.");
        }
    }

    [HttpGet]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [EndpointSummary("List available videos in the authenticated Trainer or eligible Student's gym.")]
    [ProducesResponseType<VideoListResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<VideoListResponse>> List([FromQuery] VideoListRequest request, CancellationToken cancellationToken)
    {
        if (!AccountScope.TryGetGymId(User, out var gym) || gym is null ||
            !Enum.TryParse<AccountRole>(User.FindFirstValue(ClaimTypes.Role), out var role) ||
            role is not (AccountRole.Trainer or AccountRole.Student)) return Forbid();
        try
        {
            return Ok(await videos.ListAsync(gym.Value, role, request, cancellationToken));
        }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (ArgumentException) { return Problem(statusCode: 400, title: "Invalid video catalog query."); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError("Video catalog query failed.");
            return Problem(statusCode: 500, title: "Video catalog query failed.");
        }
    }
}
