namespace BLL.DTOs.Students;

/// <summary>Represents the current routine and its revision.</summary>
public sealed class RoutineResponse
{
    public RoutineResponse(Guid revision, string? title, string? instructions, IReadOnlyList<RoutineDayResponse> days)
    {
        Revision = revision;
        Title = title;
        Instructions = instructions;
        Days = days;
    }

    public Guid Revision { get; }
    public string? Title { get; }
    public string? Instructions { get; }
    public IReadOnlyList<RoutineDayResponse> Days { get; }
}
