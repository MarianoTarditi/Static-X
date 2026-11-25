using System.ComponentModel.DataAnnotations;

namespace BLL.DTOs.Students;

/// <summary>Defines one exercise and its prescription.</summary>
public sealed class RoutineExerciseRequest
{
    public RoutineExerciseRequest() { }

    [Required, MaxLength(200)]
    public string Name { get; init; } = string.Empty;

    [Range(1, int.MaxValue)]
    public int Sets { get; init; }

    [Range(1, int.MaxValue)]
    public int? Repetitions { get; init; }

    /// <summary>Gets the duration of each set in seconds, as an alternative to repetitions.</summary>
    [Range(1, int.MaxValue)]
    public int? DurationSeconds { get; init; }

    [Range(0, int.MaxValue)]
    public int? RestSeconds { get; init; }

    [MaxLength(2000)]
    public string? Notes { get; init; }
}
