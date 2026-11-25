using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DAL.Migrations;

public partial class AddRoutineMetadata : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "Instructions", table: "StudentRoutineDays", type: "nvarchar(2000)", maxLength: 2000, nullable: true);
        migrationBuilder.AddColumn<string>(
            name: "Name", table: "StudentRoutineDays", type: "nvarchar(200)", maxLength: 200, nullable: true);
        migrationBuilder.AddColumn<string>(
            name: "Instructions", table: "StudentRoutines", type: "nvarchar(2000)", maxLength: 2000, nullable: true);
        migrationBuilder.AddColumn<string>(
            name: "Title", table: "StudentRoutines", type: "nvarchar(200)", maxLength: 200, nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "Instructions", table: "StudentRoutineDays");
        migrationBuilder.DropColumn(name: "Name", table: "StudentRoutineDays");
        migrationBuilder.DropColumn(name: "Instructions", table: "StudentRoutines");
        migrationBuilder.DropColumn(name: "Title", table: "StudentRoutines");
    }
}
