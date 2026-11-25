using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DAL.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantAccountInvitations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsEnabled",
                table: "AspNetUsers",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "StudentAccessSuspended",
                table: "AspNetUsers",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "AccountId",
                table: "Alumnos",
                type: "nvarchar(450)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExpectedRole",
                table: "ActivationInvitations",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "GymAdmin");

            migrationBuilder.CreateTable(
                name: "TenantInvitationRequests",
                columns: table => new
                {
                    GymId = table.Column<int>(type: "int", nullable: false),
                    Key = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    NormalizedEmail = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Role = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    StudentId = table.Column<int>(type: "int", nullable: true),
                    AccountId = table.Column<string>(type: "nvarchar(450)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantInvitationRequests", x => new { x.GymId, x.Key });
                    table.CheckConstraint("CK_TenantInvitationRequests_Target", "([Role] = 'Trainer' AND [StudentId] IS NULL) OR ([Role] = 'Student' AND [StudentId] > 0 AND [StudentId] IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_TenantInvitationRequests_Alumnos_StudentId",
                        column: x => x.StudentId,
                        principalTable: "Alumnos",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TenantInvitationRequests_AspNetUsers_AccountId",
                        column: x => x.AccountId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TenantInvitationRequests_Gimnasios_GymId",
                        column: x => x.GymId,
                        principalTable: "Gimnasios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_AspNetUsers_StudentSuspension",
                table: "AspNetUsers",
                sql: "[StudentAccessSuspended] = 0 OR ([IsEnabled] = 0 AND [Role] = 'Student')");

            migrationBuilder.CreateIndex(
                name: "IX_Alumnos_AccountId",
                table: "Alumnos",
                column: "AccountId",
                unique: true,
                filter: "[AccountId] IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ActivationInvitations_ExpectedRole",
                table: "ActivationInvitations",
                sql: "[ExpectedRole] IN ('GymAdmin', 'Trainer', 'Student')");

            migrationBuilder.CreateIndex(
                name: "IX_TenantInvitationRequests_AccountId",
                table: "TenantInvitationRequests",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_TenantInvitationRequests_StudentId",
                table: "TenantInvitationRequests",
                column: "StudentId");

            migrationBuilder.AddForeignKey(
                name: "FK_Alumnos_AspNetUsers_AccountId",
                table: "Alumnos",
                column: "AccountId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM [TenantInvitationRequests])
                   OR EXISTS (SELECT 1 FROM [Alumnos] WHERE [AccountId] IS NOT NULL)
                   OR EXISTS (SELECT 1 FROM [AspNetUsers] WHERE [IsEnabled] = 0 OR [StudentAccessSuspended] = 1)
                   OR EXISTS (SELECT 1 FROM [ActivationInvitations] WHERE [ExpectedRole] <> 'GymAdmin')
                    THROW 51001, 'Tenant account lifecycle data requires separately authorized preservation and remediation before rollback.', 1;
                """);
            migrationBuilder.DropForeignKey(
                name: "FK_Alumnos_AspNetUsers_AccountId",
                table: "Alumnos");

            migrationBuilder.DropTable(
                name: "TenantInvitationRequests");

            migrationBuilder.DropCheckConstraint(
                name: "CK_AspNetUsers_StudentSuspension",
                table: "AspNetUsers");

            migrationBuilder.DropIndex(
                name: "IX_Alumnos_AccountId",
                table: "Alumnos");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ActivationInvitations_ExpectedRole",
                table: "ActivationInvitations");

            migrationBuilder.DropColumn(
                name: "IsEnabled",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "StudentAccessSuspended",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "AccountId",
                table: "Alumnos");

            migrationBuilder.DropColumn(
                name: "ExpectedRole",
                table: "ActivationInvitations");
        }
    }
}
