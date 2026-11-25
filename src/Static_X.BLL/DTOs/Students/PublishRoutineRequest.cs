using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace BLL.DTOs.Students;

/// <summary>Replaces the full routine using the revision observed when editing began.</summary>
public sealed class PublishRoutineRequest
{
    internal const int TitleMaxLength = 200;
    internal const int InstructionsMaxLength = 2000;

    public PublishRoutineRequest() { }

    [JsonRequired]
    public Guid? ExpectedRevision { get; init; }

    [MaxLength(TitleMaxLength)]
    public string? Title { get; init; }

    [MaxLength(InstructionsMaxLength)]
    public string? Instructions { get; init; }

    [Required, MinLength(1), MaxLength(31)]
    public RoutineDayRequest?[] Days { get; init; } = [];
}
