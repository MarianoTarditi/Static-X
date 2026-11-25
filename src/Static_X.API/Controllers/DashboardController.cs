using System.Globalization;
using System.Security.Claims;
using BLL.DTOs.Students;
using BLL.Services.Interfaces;
using Entity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Static_X.API.Filters;

namespace Static_X.API.Controllers;

[ApiController]
[Route("api/dashboard")]
[Authorize(Roles = "GymAdmin")]
public sealed class DashboardController : ControllerBase
{
    private readonly IDashboardService dashboard;
    private readonly ILogger<DashboardController> logger;

    public DashboardController(IDashboardService dashboard, ILogger<DashboardController> logger)
    {
        this.dashboard = dashboard;
        this.logger = logger;
    }

    [HttpGet]
    [AdministrativeQueryResponse]
    [EndpointSummary("Get student counts for the authenticated administrator's gym.")]
    [EndpointDescription("Counts persisted student fichas by active state. Client-supplied gym selectors are ignored.")]
    [ProducesResponseType<DashboardResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<DashboardResponse>> Get(CancellationToken cancellationToken)
    {
        if (!int.TryParse(User.FindFirstValue("gym_id"), NumberStyles.None, CultureInfo.InvariantCulture, out var gymId) ||
            gymId <= 0 || User.FindFirstValue(ClaimTypes.Role) != nameof(AccountRole.GymAdmin))
            return Forbid();

        try
        {
            return Ok(await dashboard.GetAsync(gymId, AccountRole.GymAdmin, cancellationToken));
        }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError("Dashboard query failed.");
            return Problem(statusCode: 500, title: "Dashboard query failed.");
        }
    }
}
