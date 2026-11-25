using System.Globalization;
using System.Security.Claims;
using BLL.DTOs.Activations;
using BLL.Services.Interfaces;
using Entity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Static_X.API.Controllers;

[ApiController]
[Route("api/accounts/invitations")]
[Authorize(Roles = "GymAdmin,Trainer")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AccountInvitationsController : ControllerBase
{
    private readonly IActivationService activations;

    public AccountInvitationsController(IActivationService activations)
    {
        this.activations = activations;
    }

    /// <summary>Lists the latest persisted invitation state for an explicitly selected permitted role.</summary>
    [HttpGet]
    [EndpointSummary("List pending, expired and cancelled tenant invitations.")]
    [ProducesResponseType<InvitationListResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<InvitationListResponse>> List([FromQuery] AccountRole role,
        [FromQuery] InvitationListRequest request, CancellationToken cancellationToken)
    {
        if (!TryGym(out var gymId) || !Enum.TryParse<AccountRole>(User.FindFirstValue(ClaimTypes.Role), out var actor)) return Forbid();
        return Ok(await activations.ListInvitationsAsync(gymId, actor, role, request, cancellationToken));
    }

    /// <summary>Queues a replacement link after the persisted one-hour interval.</summary>
    [HttpPost("{accountId}/resend")]
    [EndpointSummary("Resend a tenant invitation after one hour.")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Resend(string accountId, [FromQuery] AccountRole role, CancellationToken cancellationToken)
    {
        if (!TryGym(out var gymId) || !Enum.TryParse<AccountRole>(User.FindFirstValue(ClaimTypes.Role), out var actor)) return Forbid();
        return await activations.ResendInvitationAsync(gymId, actor, role, accountId, cancellationToken)
            ? NoContent() : NotFound(new ProblemDetails { Status = 404, Title = "Invitation target not found." });
    }

    /// <summary>Revokes pending links without removing the account or linked student.</summary>
    [HttpPost("{accountId}/cancel")]
    [EndpointSummary("Cancel a tenant invitation immediately and idempotently.")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> Cancel(string accountId, [FromQuery] AccountRole role, CancellationToken cancellationToken)
    {
        if (!TryGym(out var gymId) || !Enum.TryParse<AccountRole>(User.FindFirstValue(ClaimTypes.Role), out var actor)) return Forbid();
        return await activations.CancelInvitationAsync(gymId, actor, role, accountId, cancellationToken)
            ? NoContent() : NotFound(new ProblemDetails { Status = 404, Title = "Invitation target not found." });
    }

    private bool TryGym(out int gymId)
    {
        return int.TryParse(User.FindFirstValue("gym_id"), NumberStyles.None, CultureInfo.InvariantCulture, out gymId) && gymId > 0;
    }

    /// <summary>Queues a tenant invitation for an explicitly selected pending account target.</summary>
    [HttpPost]
    [ProducesResponseType<AccountInvitationResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<AccountInvitationResponse>> Invite(InviteAccountRequest request, CancellationToken cancellationToken)
    {
        if (!int.TryParse(User.FindFirstValue("gym_id"), NumberStyles.None, CultureInfo.InvariantCulture, out var gymId) || gymId <= 0 ||
            !Enum.TryParse<AccountRole>(User.FindFirstValue(ClaimTypes.Role), out var actorRole)) return Forbid();
        try
        {
            var result = await activations.InviteAsync(gymId, actorRole, request, cancellationToken);
            return result is null
                ? NotFound(new ProblemDetails { Status = 404, Title = "Invitation target not found." })
                : Ok(result);
        }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (ArgumentException) { return Problem(statusCode: 400, title: "Invalid invitation request."); }
        catch (BLL.Exceptions.ProvisioningConflictException) { return Problem(statusCode: 409, title: "Invitation conflicts with existing state."); }
        catch (BLL.Exceptions.InvitationCooldownException) { return Problem(statusCode: 409, title: "Invitation resend is available one hour after last queueing."); }
        catch (InvalidOperationException) { return Problem(statusCode: 503, title: "Invitation service unavailable."); }
    }
}
