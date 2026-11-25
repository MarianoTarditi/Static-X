using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DAL.Migrations;

public partial class AddPayments : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<long>(name: "PaymentRevision", table: "Alumnos", type: "bigint", nullable: false, defaultValue: 0L);
        migrationBuilder.AddUniqueConstraint(name: "AK_Alumnos_GimnasioId_Id", table: "Alumnos", columns: new[] { "GimnasioId", "Id" });
        migrationBuilder.CreateTable(name: "GymPassPrices", columns: table => new
        {
            Id = table.Column<long>(type: "bigint", nullable: false).Annotation("SqlServer:Identity", "1, 1"),
            GymId = table.Column<int>(type: "int", nullable: false),
            EffectiveFrom = table.Column<DateOnly>(type: "date", nullable: false),
            AmountCents = table.Column<long>(type: "bigint", nullable: false),
            CreatedBy = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
            CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
        }, constraints: table =>
        {
            table.PrimaryKey("PK_GymPassPrices", x => x.Id);
            table.UniqueConstraint("AK_GymPassPrices_GymId_Id", x => new { x.GymId, x.Id });
            table.CheckConstraint("CK_GymPassPrices_Amount", "[AmountCents] > 0 AND [AmountCents] <= 10000000000");
            table.ForeignKey("FK_GymPassPrices_Gimnasios_GymId", x => x.GymId, "Gimnasios", "Id", onDelete: ReferentialAction.Restrict);
        });
        migrationBuilder.CreateTable(name: "Payments", columns: table => new
        {
            Id = table.Column<long>(type: "bigint", nullable: false).Annotation("SqlServer:Identity", "1, 1"),
            GymId = table.Column<int>(type: "int", nullable: false),
            StudentId = table.Column<int>(type: "int", nullable: false),
            Sequence = table.Column<long>(type: "bigint", nullable: false),
            PreviousPaymentId = table.Column<long>(type: "bigint", nullable: true),
            PriceId = table.Column<long>(type: "bigint", nullable: false),
            AmountCents = table.Column<long>(type: "bigint", nullable: false),
            PaidOn = table.Column<DateOnly>(type: "date", nullable: false),
            Method = table.Column<int>(type: "int", nullable: false),
            ExtensionStart = table.Column<DateOnly>(type: "date", nullable: false),
            CoverageStart = table.Column<DateOnly>(type: "date", nullable: false),
            CoverageEndExclusive = table.Column<DateOnly>(type: "date", nullable: false),
            ActorId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
            IdempotencyKey = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
            RequestFingerprint = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
            CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
            VoidedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
            VoidedBy = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
            VoidReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
        }, constraints: table =>
        {
            table.PrimaryKey("PK_Payments", x => x.Id);
            table.UniqueConstraint("AK_Payments_GymId_StudentId_Id", x => new { x.GymId, x.StudentId, x.Id });
            table.CheckConstraint("CK_Payments_Amount", "[AmountCents] > 0 AND [AmountCents] <= 10000000000");
            table.CheckConstraint("CK_Payments_Method", "[Method] IN (1, 2, 3)");
            table.CheckConstraint("CK_Payments_Interval", "[Sequence] > 0 AND [CoverageStart] <= [ExtensionStart] AND [PaidOn] <= [ExtensionStart] AND [ExtensionStart] < [CoverageEndExclusive]");
            table.CheckConstraint("CK_Payments_Void", "([VoidedAt] IS NULL AND [VoidedBy] IS NULL AND [VoidReason] IS NULL) OR ([VoidedAt] IS NOT NULL AND [VoidedBy] IS NOT NULL AND [VoidReason] IS NOT NULL)");
            table.ForeignKey("FK_Payments_Alumnos_GymId_StudentId", x => new { x.GymId, x.StudentId }, "Alumnos", new[] { "GimnasioId", "Id" }, onDelete: ReferentialAction.Restrict);
            table.ForeignKey("FK_Payments_GymPassPrices_GymId_PriceId", x => new { x.GymId, x.PriceId }, "GymPassPrices", new[] { "GymId", "Id" }, onDelete: ReferentialAction.Restrict);
            table.ForeignKey("FK_Payments_Payments_GymId_StudentId_PreviousPaymentId", x => new { x.GymId, x.StudentId, x.PreviousPaymentId }, "Payments", new[] { "GymId", "StudentId", "Id" }, onDelete: ReferentialAction.Restrict);
        });
        migrationBuilder.CreateIndex("IX_GymPassPrices_GymId_EffectiveFrom", "GymPassPrices", new[] { "GymId", "EffectiveFrom" }, unique: true);
        migrationBuilder.CreateIndex("IX_Payments_GymId_IdempotencyKey", "Payments", new[] { "GymId", "IdempotencyKey" }, unique: true);
        migrationBuilder.CreateIndex("IX_Payments_GymId_StudentId_Sequence", "Payments", new[] { "GymId", "StudentId", "Sequence" }, unique: true);
        migrationBuilder.CreateIndex("IX_Payments_GymId_PriceId", "Payments", new[] { "GymId", "PriceId" });
        migrationBuilder.CreateIndex("IX_Payments_GymId_StudentId_PreviousPaymentId", "Payments", new[] { "GymId", "StudentId", "PreviousPaymentId" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // A rollback must not silently destroy a financial ledger.
        migrationBuilder.Sql("IF EXISTS (SELECT 1 FROM [Payments]) OR EXISTS (SELECT 1 FROM [GymPassPrices]) THROW 51000, 'Payments rollback requires an approved archival operation.', 1;");
        migrationBuilder.DropTable("Payments");
        migrationBuilder.DropTable("GymPassPrices");
        migrationBuilder.DropUniqueConstraint("AK_Alumnos_GimnasioId_Id", "Alumnos");
        migrationBuilder.DropColumn("PaymentRevision", "Alumnos");
    }
}
