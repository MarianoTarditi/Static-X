using System.Globalization;
using System.Security.Claims;
using BLL.DTOs.Students;
using BLL.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Static_X.API.Controllers;

[ApiController]
[Route("api/students")]
[Authorize(Roles = "GymAdmin,Trainer")]
public sealed class StudentsController : ControllerBase
{
    private readonly IStudentService students;

    public StudentsController(IStudentService students)
    {
        this.students = students;
    }

    /// <summary>Creates a student ficha in the authenticated account's gym.</summary>
    [HttpPost]
    [ProducesResponseType<StudentResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<StudentResponse>> CreateStudent(CreateStudentRequest request, CancellationToken cancellationToken)
    {
        if (!TryGetCurrentGymId(out var gymId)) return Forbid();
        var student = await students.AddAsync(gymId, request, cancellationToken);
        return CreatedAtAction(nameof(GetStudentById), new { id = student.Id }, student);
    }

    /// <summary>Returns a student page in the session gym, optionally filtered by state and substring.</summary>
    [HttpGet]
    [ProducesResponseType<StudentListResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<StudentListResponse>> GetStudents([FromQuery] StudentListRequest request, CancellationToken cancellationToken)
    {
        if (!TryGetCurrentGymId(out var gymId)) return Forbid();
        var result = await students.ListAsync(gymId, request, cancellationToken);
        return Ok(result);
    }

    /// <summary>Finds a student in the session gym; nonpositive identifiers return 400.</summary>
    [HttpGet("{id:int}")]
    [ProducesResponseType<StudentResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<StudentResponse>> GetStudentById(int id, CancellationToken cancellationToken)
    {
        if (!TryGetCurrentGymId(out var gymId)) return Forbid();
        if (id <= 0) return Problem(statusCode: 400, title: "A positive student identifier is required.");
        var student = await students.FindAsync(gymId, id, cancellationToken);
        return student is null
            ? NotFound(new ProblemDetails { Status = 404, Title = "Student not found." })
            : Ok(student);
    }

    /// <summary>Updates only the ficha fields of an existing student in the session gym.</summary>
    [HttpPut("{id:int}")]
    [ProducesResponseType<StudentResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<StudentResponse>> UpdateStudent(int id, UpdateStudentRequest request, CancellationToken cancellationToken)
    {
        if (!TryGetCurrentGymId(out var gymId)) return Forbid();
        if (id <= 0) return Problem(statusCode: 400, title: "A positive student identifier is required.");
        var student = await students.UpdateAsync(gymId, id, request, cancellationToken);
        return student is null
            ? NotFound(new ProblemDetails { Status = 404, Title = "Student not found." })
            : Ok(student);
    }

    /// <summary>Deactivates a student ficha without deleting it or changing account access.</summary>
    [HttpPatch("{id:int}/deactivate")]
    [ProducesResponseType<StudentResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<StudentResponse>> DeactivateStudent(int id, CancellationToken cancellationToken)
    {
        if (!TryGetCurrentGymId(out var gymId)) return Forbid();
        if (id <= 0) return Problem(statusCode: 400, title: "A positive student identifier is required.");
        var student = await students.DeactivateAsync(gymId, id, cancellationToken);
        return student is null
            ? NotFound(new ProblemDetails { Status = 404, Title = "Student not found." })
            : Ok(student);
    }

    /// <summary>Reactivates a student ficha in the authenticated session gym.</summary>
    [HttpPatch("{id:int}/reactivate")]
    [ProducesResponseType<StudentResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<StudentResponse>> ReactivateStudent(int id, CancellationToken cancellationToken)
    {
        if (!TryGetCurrentGymId(out var gymId)) return Forbid();
        if (id <= 0) return Problem(statusCode: 400, title: "A positive student identifier is required.");
        var student = await students.ReactivateAsync(gymId, id, cancellationToken);
        return student is null
            ? NotFound(new ProblemDetails { Status = 404, Title = "Student not found." })
            : Ok(student);
    }

    private bool TryGetCurrentGymId(out int gymId) =>
        int.TryParse(User.FindFirstValue("gym_id"), NumberStyles.None, CultureInfo.InvariantCulture, out gymId) && gymId > 0;

}
