namespace BLL.Exceptions;

public sealed class StudentEmailConflictException : Exception
{
    public StudentEmailConflictException() : base("A student with this email already exists in this gym.")
    {
    }
}
