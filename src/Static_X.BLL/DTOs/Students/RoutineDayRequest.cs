using System.ComponentModel.DataAnnotations;

namespace BLL.DTOs.Students;

/// <summary>Defines one day in publication order.</summary>
public sealed class RoutineDayRequest
{
    internal const int NameMaxLength = 200;
    internal const int InstructionsMaxLength = 2000;

    public RoutineDayRequest() { }

    [MaxLength(NameMaxLength)]
    public string? Name { get; init; }

    [MaxLength(InstructionsMaxLength)]
    public string? Instructions { get; init; }

    [Required, MinLength(1), MaxLength(50)]
    public RoutineExerciseRequest?[] Exercises { get; init; } = [];
}
