namespace BLL.DTOs.Students;

/// <summary>Represents the current publication or the authorized empty state.</summary>
public sealed class RoutineEnvelopeResponse
{
    public RoutineEnvelopeResponse(RoutineResponse? routine)
    {
        Routine = routine;
    }

    public RoutineResponse? Routine { get; }
}
