using Entity;

namespace DAL.Repositories.Models;

public sealed class RoutineWriteResult
{
    public RoutineWriteResult(RoutineWriteOutcome outcome, StudentRoutine? routine = null)
    {
        Outcome = outcome;
        Routine = routine;
    }

    public RoutineWriteOutcome Outcome { get; }
    public StudentRoutine? Routine { get; }
}
