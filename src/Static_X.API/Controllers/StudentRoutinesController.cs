using System.Security.Claims;
using BLL.DTOs.Students;
using BLL.Exceptions;
using BLL.Services.Interfaces;
using Entity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Static_X.API.Auth;

namespace Static_X.API.Controllers;

[ApiController]
[Route("api/students/{studentId:int}/routine")]
[Authorize(Roles = nameof(AccountRole.Trainer))]
public sealed class StudentRoutinesController : ControllerBase
{
    private readonly IRoutineService routines;

    public StudentRoutinesController(IRoutineService routines)
    {
        this.routines = routines;
    }

    /// <summary>Reads the current routine and revision for a profile in the trainer's gym.</summary>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<RoutineEnvelopeResponse>> Read(int studentId, CancellationToken cancellationToken)
    {
        if (!AccountScope.TryGetGymId(User, out var gym) || gym is null) return Forbid();
        var result = await routines.ReadForTrainerAsync(gym.Value, AccountRole.Trainer, studentId, cancellationToken);
        return result is null ? NotFound() : Ok(result);
    }

    /// <summary>Publishes a complete routine if the observed revision is still current.</summary>
    [HttpPut]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<RoutineEnvelopeResponse>> Publish(int studentId, PublishRoutineRequest request,
        CancellationToken cancellationToken)
    {
        var actor = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!AccountScope.TryGetGymId(User, out var gym) || gym is null || string.IsNullOrWhiteSpace(actor)) return Forbid();
        try
        {
            var result = await routines.PublishAsync(gym.Value, AccountRole.Trainer, actor, studentId, request, cancellationToken);
            if (result is null) return NotFound();
            return request.ExpectedRevision is null ? CreatedAtAction(nameof(Read), new { studentId }, result) : Ok(result);
        }
        catch (RoutineRevisionConflictException)
        {
            return Problem(statusCode: StatusCodes.Status409Conflict, title: "The routine has changed. Reload before publishing.");
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
        catch (ArgumentException exception) when (exception.ParamName == nameof(request))
        {
            ModelState.AddModelError(nameof(request), "The routine content is invalid.");
            return ValidationProblem(ModelState);
        }
    }
}
