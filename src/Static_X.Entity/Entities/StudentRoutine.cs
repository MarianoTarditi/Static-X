namespace Entity;

public sealed class StudentRoutine
{
    public StudentRoutine() { }

    public int StudentId { get; set; }
    public Guid Revision { get; set; }
    public string? Title { get; set; }
    public string? Instructions { get; set; }
    public List<StudentRoutineDay> Days { get; set; } = new();
}
