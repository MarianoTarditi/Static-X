using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DAL.Migrations;

[DbContext(typeof(StaticXDbContext))]
[Migration("20261123010000_AddExerciseDuration")]
partial class AddExerciseDuration
{
    protected override void BuildTargetModel(ModelBuilder modelBuilder) => BuildExerciseDurationModel(modelBuilder);

    internal static void BuildExerciseDurationModel(ModelBuilder modelBuilder)
    {
        AddRoutineMetadata.BuildRoutineMetadataModel(modelBuilder);
        modelBuilder.Entity("Entity.StudentRoutineExercise", exercise =>
        {
            exercise.Property<int?>("Repetitions").IsRequired(false).HasColumnType("int");
            exercise.Property<int?>("DurationSeconds").HasColumnType("int");
            exercise.ToTable("StudentRoutineExercises", table => table.HasCheckConstraint("CK_StudentRoutineExercises_Values",
                "[Position] >= 0 AND [Sets] > 0 AND (([Repetitions] IS NOT NULL AND [Repetitions] > 0 AND [DurationSeconds] IS NULL) OR ([DurationSeconds] IS NOT NULL AND [DurationSeconds] > 0 AND [Repetitions] IS NULL)) AND ([RestSeconds] IS NULL OR [RestSeconds] >= 0)"));
        });
    }
}
