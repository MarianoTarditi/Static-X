using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DAL.Migrations
{
    /// <inheritdoc />
    public partial class AddStudentRoutines : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "StudentRoutines",
                columns: table => new
                {
                    StudentId = table.Column<int>(type: "int", nullable: false),
                    Revision = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StudentRoutines", x => x.StudentId);
                    table.ForeignKey(
                        name: "FK_StudentRoutines_Alumnos_StudentId",
                        column: x => x.StudentId,
                        principalTable: "Alumnos",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "StudentRoutineDays",
                columns: table => new
                {
                    StudentId = table.Column<int>(type: "int", nullable: false),
                    Position = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StudentRoutineDays", x => new { x.StudentId, x.Position });
                    table.CheckConstraint("CK_StudentRoutineDays_Position", "[Position] >= 0");
                    table.ForeignKey(
                        name: "FK_StudentRoutineDays_StudentRoutines_StudentId",
                        column: x => x.StudentId,
                        principalTable: "StudentRoutines",
                        principalColumn: "StudentId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "StudentRoutineExercises",
                columns: table => new
                {
                    StudentId = table.Column<int>(type: "int", nullable: false),
                    DayPosition = table.Column<int>(type: "int", nullable: false),
                    Position = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Sets = table.Column<int>(type: "int", nullable: false),
                    Repetitions = table.Column<int>(type: "int", nullable: false),
                    RestSeconds = table.Column<int>(type: "int", nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StudentRoutineExercises", x => new { x.StudentId, x.DayPosition, x.Position });
                    table.CheckConstraint("CK_StudentRoutineExercises_Values", "[Position] >= 0 AND [Sets] > 0 AND [Repetitions] > 0 AND ([RestSeconds] IS NULL OR [RestSeconds] >= 0)");
                    table.ForeignKey(
                        name: "FK_StudentRoutineExercises_StudentRoutineDays_StudentId_DayPosition",
                        columns: x => new { x.StudentId, x.DayPosition },
                        principalTable: "StudentRoutineDays",
                        principalColumns: new[] { "StudentId", "Position" },
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StudentRoutineExercises");

            migrationBuilder.DropTable(
                name: "StudentRoutineDays");

            migrationBuilder.DropTable(
                name: "StudentRoutines");
        }
    }
}
