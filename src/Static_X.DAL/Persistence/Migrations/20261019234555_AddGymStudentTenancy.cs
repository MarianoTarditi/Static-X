using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Static_X.DAL.Migrations
{
    /// <inheritdoc />
    public partial class AddGymStudentTenancy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Email",
                table: "Alumnos",
                type: "nvarchar(320)",
                maxLength: 320,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)");

            migrationBuilder.AddColumn<int>(
                name: "GimnasioId",
                table: "Alumnos",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "Gimnasios",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Nombre = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Gimnasios", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Alumnos_GimnasioId_Email",
                table: "Alumnos",
                columns: new[] { "GimnasioId", "Email" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Alumnos_Gimnasios_GimnasioId",
                table: "Alumnos",
                column: "GimnasioId",
                principalTable: "Gimnasios",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Alumnos_Gimnasios_GimnasioId",
                table: "Alumnos");

            migrationBuilder.DropTable(
                name: "Gimnasios");

            migrationBuilder.DropIndex(
                name: "IX_Alumnos_GimnasioId_Email",
                table: "Alumnos");

            migrationBuilder.DropColumn(
                name: "GimnasioId",
                table: "Alumnos");

            migrationBuilder.AlterColumn<string>(
                name: "Email",
                table: "Alumnos",
                type: "nvarchar(max)",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(320)",
                oldMaxLength: 320);
        }
    }
}
