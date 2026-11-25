namespace Entity;

public sealed class StudentRoutineExercise
{
    public StudentRoutineExercise() { }

    public int StudentId { get; set; }
    public int DayPosition { get; set; }
    public int Position { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Sets { get; set; }
    public int? Repetitions { get; set; }
    public int? DurationSeconds { get; set; }
    public int? RestSeconds { get; set; }
    public string? Notes { get; set; }
}
