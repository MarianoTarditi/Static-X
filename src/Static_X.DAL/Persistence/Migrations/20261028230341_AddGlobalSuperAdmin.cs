using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Static_X.DAL.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGlobalSuperAdmin : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AspNetUsers_GymId_NormalizedEmail",
                table: "AspNetUsers");

            migrationBuilder.AlterColumn<int>(
                name: "GymId",
                table: "AspNetUsers",
                type: "int",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "int");

            migrationBuilder.CreateIndex(
                name: "GlobalEmailIndex",
                table: "AspNetUsers",
                column: "NormalizedEmail",
                unique: true,
                filter: "[GymId] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUsers_GymId_NormalizedEmail",
                table: "AspNetUsers",
                columns: new[] { "GymId", "NormalizedEmail" },
                unique: true,
                filter: "[GymId] IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_AspNetUsers_RoleGym",
                table: "AspNetUsers",
                sql: "([Role] = 'SuperAdmin' AND [GymId] IS NULL) OR ([Role] IN ('GymAdmin', 'Trainer', 'Student') AND [GymId] IS NOT NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // A global identity must never be assigned a fabricated tenant during rollback.
            migrationBuilder.Sql("IF EXISTS (SELECT 1 FROM [AspNetUsers] WHERE [GymId] IS NULL) THROW 51000, 'Remove or separately migrate global accounts before rollback.', 1;");
            migrationBuilder.DropIndex(
                name: "GlobalEmailIndex",
                table: "AspNetUsers");

            migrationBuilder.DropIndex(
                name: "IX_AspNetUsers_GymId_NormalizedEmail",
                table: "AspNetUsers");

            migrationBuilder.DropCheckConstraint(
                name: "CK_AspNetUsers_RoleGym",
                table: "AspNetUsers");

            migrationBuilder.AlterColumn<int>(
                name: "GymId",
                table: "AspNetUsers",
                type: "int",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "int",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUsers_GymId_NormalizedEmail",
                table: "AspNetUsers",
                columns: new[] { "GymId", "NormalizedEmail" },
                unique: true);
        }
    }
}
