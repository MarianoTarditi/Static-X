using Entity;
using Microsoft.EntityFrameworkCore;

namespace DAL;

internal static class StudentRoutineConfiguration
{
    public static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<StudentRoutine>(routine =>
        {
            routine.HasKey(r => r.StudentId);
            routine.Property(r => r.StudentId).ValueGeneratedNever();
            routine.Property(r => r.Revision).IsConcurrencyToken();
            routine.Property(r => r.Title).HasMaxLength(200);
            routine.Property(r => r.Instructions).HasMaxLength(2000);
            routine.HasOne<Alumno>().WithOne().HasForeignKey<StudentRoutine>(r => r.StudentId)
                .OnDelete(DeleteBehavior.Restrict);
            routine.HasMany(r => r.Days).WithOne().HasForeignKey(d => d.StudentId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<StudentRoutineDay>(day =>
        {
            day.HasKey(d => new { d.StudentId, d.Position });
            day.Property(d => d.Name).HasMaxLength(200);
            day.Property(d => d.Instructions).HasMaxLength(2000);
            day.ToTable("StudentRoutineDays", table => table.HasCheckConstraint("CK_StudentRoutineDays_Position", "[Position] >= 0"));
            day.HasMany(d => d.Exercises).WithOne().HasForeignKey(e => new { e.StudentId, e.DayPosition })
                .OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<StudentRoutineExercise>(exercise =>
        {
            exercise.HasKey(e => new { e.StudentId, e.DayPosition, e.Position });
            exercise.Property(e => e.Name).HasMaxLength(200).IsRequired();
            exercise.Property(e => e.Notes).HasMaxLength(2000);
            exercise.ToTable("StudentRoutineExercises", table => table.HasCheckConstraint("CK_StudentRoutineExercises_Values",
                "[Position] >= 0 AND [Sets] > 0 AND (([Repetitions] IS NOT NULL AND [Repetitions] > 0 AND [DurationSeconds] IS NULL) OR ([DurationSeconds] IS NOT NULL AND [DurationSeconds] > 0 AND [Repetitions] IS NULL)) AND ([RestSeconds] IS NULL OR [RestSeconds] >= 0)"));
        });
    }
}
