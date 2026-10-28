using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Static_X.DAL.Migrations
{
    /// <inheritdoc />
    public partial class AddActivationDeliveryRecovery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ActivationInvitations_GymId_AccountId",
                table: "ActivationInvitations");

            migrationBuilder.AddColumn<DateTime>(
                name: "LeaseExpiresAt",
                table: "ActivationOutbox",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "LeaseOwner",
                table: "ActivationOutbox",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "RevokedAt",
                table: "ActivationInvitations",
                type: "datetime2",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ActivationOutbox_NextAttemptAt_LeaseExpiresAt",
                table: "ActivationOutbox",
                columns: new[] { "NextAttemptAt", "LeaseExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ActivationInvitations_GymId_AccountId",
                table: "ActivationInvitations",
                columns: new[] { "GymId", "AccountId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ActivationOutbox_NextAttemptAt_LeaseExpiresAt",
                table: "ActivationOutbox");

            migrationBuilder.DropIndex(
                name: "IX_ActivationInvitations_GymId_AccountId",
                table: "ActivationInvitations");

            migrationBuilder.DropColumn(
                name: "LeaseExpiresAt",
                table: "ActivationOutbox");

            migrationBuilder.DropColumn(
                name: "LeaseOwner",
                table: "ActivationOutbox");

            migrationBuilder.DropColumn(
                name: "RevokedAt",
                table: "ActivationInvitations");

            migrationBuilder.CreateIndex(
                name: "IX_ActivationInvitations_GymId_AccountId",
                table: "ActivationInvitations",
                columns: new[] { "GymId", "AccountId" },
                unique: true);
        }
    }
}
