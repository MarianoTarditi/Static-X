using Entity;

namespace DAL.Repositories.Models;

public sealed class RoutineReadResult
{
    public RoutineReadResult(StudentRoutine? routine)
    {
        Routine = routine;
    }

    public StudentRoutine? Routine { get; }
}
