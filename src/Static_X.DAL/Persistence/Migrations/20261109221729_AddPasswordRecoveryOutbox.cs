using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DAL.Migrations
{
    /// <inheritdoc />
    public partial class AddPasswordRecoveryOutbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PasswordRecoveryOutbox",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AccountId = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    GymId = table.Column<int>(type: "int", nullable: true),
                    Role = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    StampBinding = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ConsumedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RevokedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ProtectedPayload = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    LeaseOwner = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LeaseExpiresAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    NextAttemptAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    SentAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    AttemptCount = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PasswordRecoveryOutbox", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PasswordRecoveryOutbox_AspNetUsers_AccountId",
                        column: x => x.AccountId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PasswordRecoveryOutbox_Gimnasios_GymId",
                        column: x => x.GymId,
                        principalTable: "Gimnasios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PasswordRecoveryOutbox_AccountId_SentAt_ExpiresAt",
                table: "PasswordRecoveryOutbox",
                columns: new[] { "AccountId", "SentAt", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PasswordRecoveryOutbox_GymId",
                table: "PasswordRecoveryOutbox",
                column: "GymId");

            migrationBuilder.CreateIndex(
                name: "IX_PasswordRecoveryOutbox_NextAttemptAt_LeaseExpiresAt",
                table: "PasswordRecoveryOutbox",
                columns: new[] { "NextAttemptAt", "LeaseExpiresAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PasswordRecoveryOutbox");
        }
    }
}
