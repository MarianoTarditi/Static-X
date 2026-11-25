namespace BLL.DTOs.Students;

/// <summary>Represents a published day with exercises in saved order.</summary>
public sealed class RoutineDayResponse
{
    public RoutineDayResponse(string? name, string? instructions, IReadOnlyList<RoutineExerciseResponse> exercises)
    {
        Name = name;
        Instructions = instructions;
        Exercises = exercises;
    }

    public string? Name { get; }
    public string? Instructions { get; }
    public IReadOnlyList<RoutineExerciseResponse> Exercises { get; }
}
