namespace BLL.DTOs.Students;

/// <summary>Represents a published exercise prescription.</summary>
public sealed class RoutineExerciseResponse
{
    public RoutineExerciseResponse(string name, int sets, int? repetitions, int? restSeconds, string? notes, int? durationSeconds = null)
    {
        Name = name;
        Sets = sets;
        Repetitions = repetitions;
        DurationSeconds = durationSeconds;
        RestSeconds = restSeconds;
        Notes = notes;
    }

    public string Name { get; }
    public int Sets { get; }
    public int? Repetitions { get; }
    public int? DurationSeconds { get; }
    public int? RestSeconds { get; }
    public string? Notes { get; }
}
