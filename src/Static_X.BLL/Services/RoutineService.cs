using AutoMapper;
using BLL.DTOs.Students;
using BLL.Exceptions;
using BLL.Services.Interfaces;
using DAL.Repositories.Interfaces;
using DAL.Repositories.Models;
using Entity;

namespace BLL;

/// <summary>Valida el acceso, prepara publicaciones y devuelve las rutinas persistidas.</summary>
public sealed class RoutineService : IRoutineService
{
    private readonly IRoutineRepository repository;
    private readonly IMapper mapper;

    /// <summary>Recibe el repositorio de rutinas y el conversor de respuestas.</summary>
    public RoutineService(IRoutineRepository repository, IMapper mapper)
    {
        this.repository = repository;
        this.mapper = mapper;
    }

    /// <summary>Autoriza al entrenador y consulta la ficha solicitada en su gimnasio.</summary>
    public async Task<RoutineEnvelopeResponse?> ReadForTrainerAsync(int gymId, AccountRole role, int studentId,
        CancellationToken cancellationToken)
    {
        RequireRole(gymId, role, AccountRole.Trainer);
        var result = await repository.FindForTrainerAsync(gymId, studentId, cancellationToken);
        return result is null ? null : Map(result.Routine);
    }

    /// <summary>Autoriza al alumno y consulta únicamente su ficha vinculada.</summary>
    public async Task<RoutineEnvelopeResponse?> ReadForStudentAsync(int gymId, AccountRole role, string accountId,
        CancellationToken cancellationToken)
    {
        RequireRole(gymId, role, AccountRole.Student);
        if (string.IsNullOrWhiteSpace(accountId)) throw new UnauthorizedAccessException();
        var result = await repository.FindForStudentAsync(gymId, accountId, cancellationToken);
        return result is null ? null : Map(result.Routine);
    }

    /// <summary>Valida toda la rutina antes de solicitar su publicación con la revisión esperada.</summary>
    public async Task<RoutineEnvelopeResponse?> PublishAsync(int gymId, AccountRole role, string trainerId, int studentId,
        PublishRoutineRequest request, CancellationToken cancellationToken)
    {
        RequireRole(gymId, role, AccountRole.Trainer);
        if (string.IsNullOrWhiteSpace(trainerId)) throw new UnauthorizedAccessException();
        var publication = BuildPublication(studentId, request);
        var result = await repository.PublishAsync(gymId, trainerId, publication, request.ExpectedRevision, cancellationToken);
        return result.Outcome switch
        {
            RoutineWriteOutcome.Published => Map(result.Routine),
            RoutineWriteOutcome.NotFound => null,
            RoutineWriteOutcome.Forbidden => throw new UnauthorizedAccessException(),
            RoutineWriteOutcome.RevisionConflict => throw new RoutineRevisionConflictException(),
            _ => throw new InvalidOperationException("Unknown routine publication outcome.")
        };
    }

    /// <summary>Comprueba el rol requerido y el ámbito de gimnasio de la sesión.</summary>
    private static void RequireRole(int gymId, AccountRole role, AccountRole requiredRole)
    {
        if (gymId <= 0 || role != requiredRole) throw new UnauthorizedAccessException();
    }

    /// <summary>Valida el contenido completo y asigna posiciones según el orden de entrada.</summary>
    private static StudentRoutine BuildPublication(int studentId, PublishRoutineRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Days is not { Length: >= 1 and <= 31 })
            throw new ArgumentException("Between 1 and 31 days are required.", nameof(request));

        var routine = new StudentRoutine
        {
            StudentId = studentId,
            Title = NormalizeMetadata(request.Title, PublishRoutineRequest.TitleMaxLength, "Routine title"),
            Instructions = NormalizeMetadata(request.Instructions, PublishRoutineRequest.InstructionsMaxLength, "Routine instructions")
        };
        foreach (var day in request.Days)
        {
            if (day?.Exercises is not { Length: >= 1 and <= 50 })
                throw new ArgumentException("Each day must have between 1 and 50 exercises.", nameof(request));

            var targetDay = new StudentRoutineDay
            {
                StudentId = studentId,
                Position = routine.Days.Count,
                Name = NormalizeMetadata(day.Name, RoutineDayRequest.NameMaxLength, "Day name"),
                Instructions = NormalizeMetadata(day.Instructions, RoutineDayRequest.InstructionsMaxLength, "Day instructions")
            };
            foreach (var exercise in day.Exercises)
            {
                if (exercise is null || string.IsNullOrWhiteSpace(exercise.Name) || exercise.Name.Length > 200 ||
                    exercise.Sets <= 0 ||
                    !((exercise.Repetitions is > 0 && exercise.DurationSeconds is null) ||
                      (exercise.DurationSeconds is > 0 && exercise.Repetitions is null)) ||
                    exercise.RestSeconds < 0 || exercise.Notes?.Length > 2000)
                    throw new ArgumentException("Each exercise requires a valid name, positive sets, exactly one positive target (repetitions or durationSeconds), nonnegative rest, and valid notes.", nameof(request));

                targetDay.Exercises.Add(new StudentRoutineExercise
                {
                    StudentId = studentId,
                    DayPosition = targetDay.Position,
                    Position = targetDay.Exercises.Count,
                    Name = exercise.Name.Trim(),
                    Sets = exercise.Sets,
                    Repetitions = exercise.Repetitions,
                    DurationSeconds = exercise.DurationSeconds,
                    RestSeconds = exercise.RestSeconds,
                    Notes = exercise.Notes
                });
            }
            routine.Days.Add(targetDay);
        }
        return routine;
    }

    /// <summary>Valida la longitud del texto opcional y normaliza los valores en blanco a nulo.</summary>
    private static string? NormalizeMetadata(string? value, int maxLength, string field)
    {
        if (value?.Length > maxLength)
            throw new ArgumentException($"{field} must be at most {maxLength} characters.");
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    /// <summary>Convierte una rutina persistida o representa la ausencia de publicación.</summary>
    private RoutineEnvelopeResponse Map(StudentRoutine? routine) =>
        new RoutineEnvelopeResponse(routine is null ? null : mapper.Map<RoutineResponse>(routine));
}
