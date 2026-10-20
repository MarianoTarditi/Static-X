using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Static_X.DAL.Migrations
{
    /// <inheritdoc />
    public partial class AddActivationProvisioning : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ActivationInvitations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    GymId = table.Column<int>(type: "int", nullable: false),
                    AccountId = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    TokenHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ConsumedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ActivationInvitations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ActivationInvitations_AspNetUsers_AccountId",
                        column: x => x.AccountId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ActivationInvitations_Gimnasios_GymId",
                        column: x => x.GymId,
                        principalTable: "Gimnasios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ProvisioningRequests",
                columns: table => new
                {
                    Key = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    GymId = table.Column<int>(type: "int", nullable: false),
                    AccountId = table.Column<string>(type: "nvarchar(450)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProvisioningRequests", x => x.Key);
                    table.ForeignKey(
                        name: "FK_ProvisioningRequests_AspNetUsers_AccountId",
                        column: x => x.AccountId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProvisioningRequests_Gimnasios_GymId",
                        column: x => x.GymId,
                        principalTable: "Gimnasios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ActivationOutbox",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InvitationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProtectedToken = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    SentAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    AttemptCount = table.Column<int>(type: "int", nullable: false),
                    NextAttemptAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ActivationOutbox", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ActivationOutbox_ActivationInvitations_InvitationId",
                        column: x => x.InvitationId,
                        principalTable: "ActivationInvitations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ActivationInvitations_AccountId",
                table: "ActivationInvitations",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_ActivationInvitations_GymId_AccountId",
                table: "ActivationInvitations",
                columns: new[] { "GymId", "AccountId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ActivationInvitations_TokenHash",
                table: "ActivationInvitations",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ActivationOutbox_InvitationId",
                table: "ActivationOutbox",
                column: "InvitationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProvisioningRequests_AccountId",
                table: "ProvisioningRequests",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_ProvisioningRequests_GymId",
                table: "ProvisioningRequests",
                column: "GymId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ActivationOutbox");

            migrationBuilder.DropTable(
                name: "ProvisioningRequests");

            migrationBuilder.DropTable(
                name: "ActivationInvitations");
        }
    }
}
