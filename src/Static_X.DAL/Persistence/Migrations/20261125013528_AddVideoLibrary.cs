using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DAL.Migrations
{
    /// <inheritdoc />
    public partial class AddVideoLibrary : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "VideoObjects",
                columns: table => new
                {
                    Key = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    GymId = table.Column<int>(type: "int", nullable: false),
                    State = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    PendingUntil = table.Column<DateTime>(type: "datetime2", nullable: false),
                    NextAttemptAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VideoObjects", x => x.Key);
                    table.CheckConstraint("CK_VideoObjects_State", "[State] IN ('Pending', 'Active', 'Cleanup')");
                    table.ForeignKey(
                        name: "FK_VideoObjects_Gimnasios_GymId",
                        column: x => x.GymId,
                        principalTable: "Gimnasios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "LibraryVideos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    GymId = table.Column<int>(type: "int", nullable: false),
                    Revision = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Category = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    ObjectKey = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Length = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LibraryVideos", x => x.Id);
                    table.CheckConstraint("CK_LibraryVideos_Category", "[Category] IN ('informational', 'exercise')");
                    table.CheckConstraint("CK_LibraryVideos_Length", "[Length] > 0 AND [Length] <= 100000000");
                    table.ForeignKey(
                        name: "FK_LibraryVideos_Gimnasios_GymId",
                        column: x => x.GymId,
                        principalTable: "Gimnasios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LibraryVideos_VideoObjects_ObjectKey",
                        column: x => x.ObjectKey,
                        principalTable: "VideoObjects",
                        principalColumn: "Key",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LibraryVideos_GymId_CreatedAt_Id",
                table: "LibraryVideos",
                columns: new[] { "GymId", "CreatedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_LibraryVideos_ObjectKey",
                table: "LibraryVideos",
                column: "ObjectKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VideoObjects_GymId",
                table: "VideoObjects",
                column: "GymId");

            migrationBuilder.CreateIndex(
                name: "IX_VideoObjects_State_NextAttemptAt",
                table: "VideoObjects",
                columns: new[] { "State", "NextAttemptAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            if (ActiveProvider == "Microsoft.EntityFrameworkCore.SqlServer")
            {
                // Keep both table locks until the migration transaction ends; cleanup tombstones are durable data.
                migrationBuilder.Sql("""
                    IF EXISTS (SELECT 1 FROM [LibraryVideos] WITH (TABLOCKX, HOLDLOCK))
                        OR EXISTS (SELECT 1 FROM [VideoObjects] WITH (TABLOCKX, HOLDLOCK))
                        THROW 51000, 'Cannot roll back video library while catalog entries or durable object cleanup records exist. Reconcile external objects and explicitly archive/remove all video records before retrying.', 1;
                    """);
            }

            migrationBuilder.DropTable(
                name: "LibraryVideos");

            migrationBuilder.DropTable(
                name: "VideoObjects");
        }
    }
}
