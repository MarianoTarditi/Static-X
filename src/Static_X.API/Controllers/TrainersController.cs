using System.Globalization;
using System.Security.Claims;
using BLL.DTOs.Accounts;
using BLL.Services.Interfaces;
using Entity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Static_X.API.Filters;

namespace Static_X.API.Controllers;

[ApiController]
[Route("api/trainers")]
[Authorize(Roles = "GymAdmin")]
public sealed class TrainersController : ControllerBase
{
    private readonly IAccountService accounts;
    private readonly ILogger<TrainersController> logger;

    public TrainersController(IAccountService accounts, ILogger<TrainersController> logger)
    {
        this.accounts = accounts;
        this.logger = logger;
    }

    [HttpGet]
    [AdministrativeQueryResponse]
    [EndpointSummary("List all Trainer accounts in the authenticated administrator's gym.")]
    [ProducesResponseType<TrainerListResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<TrainerListResponse>> List([FromQuery] TrainerListRequest request, CancellationToken cancellationToken)
    {
        if (!TryGetActorGym(out var gymId)) return Forbid();
        try { return Ok(await accounts.ListTrainersAsync(gymId, AccountRole.GymAdmin, request, cancellationToken)); }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (ArgumentException) { return Problem(statusCode: 400, title: "Invalid Trainer query."); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError("Trainer query failed.");
            return Problem(statusCode: 500, title: "Trainer query failed.");
        }
    }

    [HttpPatch("{accountId}/deactivate")]
    [AdministrativeQueryResponse]
    [EndpointSummary("Disable a same-gym Trainer and invalidate existing application cookies.")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status500InternalServerError)]
    public Task<IActionResult> Deactivate(string accountId, CancellationToken cancellationToken) =>
        SetEnabledAsync(accountId, false, cancellationToken);

    [HttpPatch("{accountId}/reactivate")]
    [AdministrativeQueryResponse]
    [EndpointSummary("Re-enable a same-gym Trainer without restoring old cookies or changing credentials.")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status500InternalServerError)]
    public Task<IActionResult> Reactivate(string accountId, CancellationToken cancellationToken) =>
        SetEnabledAsync(accountId, true, cancellationToken);

    private async Task<IActionResult> SetEnabledAsync(string accountId, bool enabled, CancellationToken cancellationToken)
    {
        if (!TryGetActorGym(out var gymId)) return Forbid();
        try
        {
            return await accounts.SetTrainerEnabledAsync(gymId, AccountRole.GymAdmin, accountId, enabled, cancellationToken)
                ? NoContent() : NotFound(new ProblemDetails { Status = 404, Title = "Trainer not found." });
        }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (ArgumentException) { return Problem(statusCode: 400, title: "Invalid Trainer request."); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError("Trainer state change failed.");
            return Problem(statusCode: 500, title: "Trainer state change failed.");
        }
    }

    private bool TryGetActorGym(out int gymId)
    {
        return int.TryParse(User.FindFirstValue("gym_id"), NumberStyles.None, CultureInfo.InvariantCulture, out gymId) && gymId > 0 &&
            User.FindFirstValue(ClaimTypes.Role) == nameof(AccountRole.GymAdmin);
    }
}
