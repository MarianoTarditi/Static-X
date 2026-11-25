namespace Entity;

public sealed class StudentRoutineDay
{
    public StudentRoutineDay() { }

    public int StudentId { get; set; }
    public int Position { get; set; }
    public string? Name { get; set; }
    public string? Instructions { get; set; }
    public List<StudentRoutineExercise> Exercises { get; set; } = new();
}
