using BLL.DTOs.Activations;
using BLL.DTOs.Gyms;
using BLL.Services.Interfaces;
using Static_X.API.Filters;
using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Static_X.API.Controllers;
// Administra el alta de gimnasios y las invitaciones de sus administradores. Solo puede usarla un usuario con rol SuperAdmin

[ApiController]
[Authorize(Roles = "SuperAdmin")]
[Route("api/gyms")]
public sealed class GymRegistrationController : ControllerBase
{
    private readonly IActivationService activation;
    private readonly ILogger<GymRegistrationController> logger;
    private readonly IGymService gyms;

    public GymRegistrationController(IActivationService activation, ILogger<GymRegistrationController> logger, IGymService gyms)
    {
        this.activation = activation;
        this.logger = logger;
        this.gyms = gyms;
    }

    [HttpGet]
    [AdministrativeQueryResponse]
    [EndpointSummary("List all gyms for global administration.")]
    [ProducesResponseType<GymListResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<GymListResponse>> ListGyms([FromQuery] GymListRequest request, CancellationToken cancellationToken)
    {
        try { return Ok(await gyms.ListAsync(request, cancellationToken)); }
        catch (ArgumentException) { return BadRequest(new ProblemDetails { Status = 400, Title = "Invalid page request." }); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError("Administrative gym query failed.");
            return Problem(statusCode: 500, title: "Administrative query failed.");
        }
    }

    [HttpGet("{gymId}/administrators/pending")]
    [AdministrativeQueryResponse]
    [EndpointSummary("List pending gym administrators eligible for invitation reissue.")]
    [ProducesResponseType<PendingAdministratorListResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<PendingAdministratorListResponse>> ListPendingAdministrators(
        [Range(1, int.MaxValue)] int gymId, [FromQuery] GymListRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var result = await gyms.ListPendingAdministratorsAsync(gymId, request, cancellationToken);
            return result is null ? NotFound(new ProblemDetails { Status = 404, Title = "Gym not found." }) : Ok(result);
        }
        catch (ArgumentException) { return BadRequest(new ProblemDetails { Status = 400, Title = "Invalid administrative query." }); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError("Pending administrator query failed.");
            return Problem(statusCode: 500, title: "Administrative query failed.");
        }
    }

    [HttpPost("provision")]
    [ProducesResponseType<int>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<int>> RegisterGym(ProvisionGymRequest request, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        try { return Ok(await activation.ProvisionAsync(request, cancellationToken)); }
        catch (ArgumentException) { return BadRequest(new ProblemDetails { Status = 400, Title = "Invalid provisioning request." }); }
        catch (BLL.Exceptions.ProvisioningConflictException)
        { return Conflict(new ProblemDetails { Status = 409, Title = "Provisioning key belongs to another request." }); }
        catch (InvalidOperationException)
        { return StatusCode(503, new ProblemDetails { Status = 503, Title = "Provisioning is unavailable." }); }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogError("Gym provisioning failed. Inspect restricted database and service health without exposing request data.");
            return Problem(statusCode: 500, title: "Gym provisioning failed.");
        }
    }

    [HttpPost("{gymId:int:min(1)}/administrators/{accountId}/invitation")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> ReissueAdministratorInvitation(int gymId, string accountId, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        try
        {
            return await activation.ReissueAsync(gymId, accountId, cancellationToken)
                ? NoContent()
                : NotFound(new ProblemDetails { Status = 404, Title = "Pending administrator not found." });
        }
        catch (ArgumentException) { return BadRequest(new ProblemDetails { Status = 400, Title = "Invalid invitation request." }); }
        catch (InvalidOperationException)
        { return StatusCode(503, new ProblemDetails { Status = 503, Title = "Invitation delivery is unavailable." }); }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogError("Administrator invitation reissue failed. Inspect restricted database and service health without exposing request data.");
            return Problem(statusCode: 500, title: "Invitation reissue failed.");
        }
    }
}
