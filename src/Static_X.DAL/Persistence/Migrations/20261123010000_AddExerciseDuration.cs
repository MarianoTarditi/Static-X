using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DAL.Migrations;

public partial class AddExerciseDuration : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint(name: "CK_StudentRoutineExercises_Values", table: "StudentRoutineExercises");
        migrationBuilder.AlterColumn<int>(
            name: "Repetitions", table: "StudentRoutineExercises", type: "int", nullable: true,
            oldClrType: typeof(int), oldType: "int");
        migrationBuilder.AddColumn<int>(
            name: "DurationSeconds", table: "StudentRoutineExercises", type: "int", nullable: true);
        migrationBuilder.AddCheckConstraint(name: "CK_StudentRoutineExercises_Values", table: "StudentRoutineExercises",
            sql: "[Position] >= 0 AND [Sets] > 0 AND (([Repetitions] IS NOT NULL AND [Repetitions] > 0 AND [DurationSeconds] IS NULL) OR ([DurationSeconds] IS NOT NULL AND [DurationSeconds] > 0 AND [Repetitions] IS NULL)) AND ([RestSeconds] IS NULL OR [RestSeconds] >= 0)");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        if (ActiveProvider == "Microsoft.EntityFrameworkCore.SqlServer")
        {
            // Hold the table lock until the migration transaction ends, including schema changes.
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM [StudentRoutineExercises] WITH (TABLOCKX, HOLDLOCK)
                    WHERE [DurationSeconds] IS NOT NULL OR [Repetitions] IS NULL)
                    THROW 51000, 'Cannot roll back exercise duration while timed exercises exist. Explicitly republish or remove them before retrying.', 1;
                """);
        }

        migrationBuilder.DropCheckConstraint(name: "CK_StudentRoutineExercises_Values", table: "StudentRoutineExercises");
        migrationBuilder.AlterColumn<int>(
            name: "Repetitions", table: "StudentRoutineExercises", type: "int", nullable: false,
            oldClrType: typeof(int), oldType: "int", oldNullable: true);
        migrationBuilder.DropColumn(name: "DurationSeconds", table: "StudentRoutineExercises");
        migrationBuilder.AddCheckConstraint(name: "CK_StudentRoutineExercises_Values", table: "StudentRoutineExercises",
            sql: "[Position] >= 0 AND [Sets] > 0 AND [Repetitions] > 0 AND ([RestSeconds] IS NULL OR [RestSeconds] >= 0)");
    }
}
