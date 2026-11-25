namespace BLL.Exceptions;

public sealed class RoutineRevisionConflictException : Exception
{
    public RoutineRevisionConflictException() : base("The routine revision has changed.") { }
}
