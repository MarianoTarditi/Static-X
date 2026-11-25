using System.Security.Claims;
using BLL.DTOs.Students;
using BLL.Services.Interfaces;
using Entity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Static_X.API.Auth;

namespace Static_X.API.Controllers;

[ApiController]
[Route("api/student/me/routine")]
[Authorize(Roles = nameof(AccountRole.Student))]
public sealed class MyRoutineController : ControllerBase
{
    private readonly IRoutineService routines;

    public MyRoutineController(IRoutineService routines)
    {
        this.routines = routines;
    }

    /// <summary>Reads only the current routine of the covered student's linked profile.</summary>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<RoutineEnvelopeResponse>> Read(CancellationToken cancellationToken)
    {
        var actor = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!AccountScope.TryGetGymId(User, out var gym) || gym is null || string.IsNullOrWhiteSpace(actor)) return Forbid();
        var result = await routines.ReadForStudentAsync(gym.Value, AccountRole.Student, actor, cancellationToken);
        return result is null ? Forbid() : Ok(result);
    }
}
