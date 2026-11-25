using Entity;
using Microsoft.EntityFrameworkCore;

namespace DAL;

public static class PaymentConfiguration
{
    public static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Alumno>().HasAlternateKey(s => new { s.GimnasioId, s.Id });
        modelBuilder.Entity<Alumno>().Property(s => s.PaymentRevision).IsConcurrencyToken().HasDefaultValue(0L);
        modelBuilder.Entity<GymPassPrice>(price =>
        {
            price.HasKey(p => p.Id);
            price.HasAlternateKey(p => new { p.GymId, p.Id });
            price.HasIndex(p => new { p.GymId, p.EffectiveFrom }).IsUnique();
            price.Property(p => p.CreatedBy).HasMaxLength(450).IsRequired();
            price.HasOne<Gimnasio>().WithMany().HasForeignKey(p => p.GymId).OnDelete(DeleteBehavior.Restrict);
            price.ToTable("GymPassPrices", t => t.HasCheckConstraint("CK_GymPassPrices_Amount", "[AmountCents] > 0 AND [AmountCents] <= 10000000000"));
        });
        modelBuilder.Entity<Payment>(payment =>
        {
            payment.HasKey(p => p.Id);
            payment.HasAlternateKey(p => new { p.GymId, p.StudentId, p.Id });
            payment.HasIndex(p => new { p.GymId, p.StudentId, p.Sequence }).IsUnique();
            payment.HasIndex(p => new { p.GymId, p.IdempotencyKey }).IsUnique();
            payment.Property(p => p.ActorId).HasMaxLength(450).IsRequired();
            payment.Property(p => p.IdempotencyKey).HasMaxLength(128).IsRequired();
            payment.Property(p => p.RequestFingerprint).HasMaxLength(64).IsRequired();
            payment.Property(p => p.VoidedBy).HasMaxLength(450);
            payment.Property(p => p.VoidReason).HasMaxLength(500);
            payment.HasOne<Alumno>().WithMany().HasForeignKey(p => new { p.GymId, p.StudentId })
                .HasPrincipalKey(s => new { s.GimnasioId, s.Id }).OnDelete(DeleteBehavior.Restrict);
            payment.HasOne<GymPassPrice>().WithMany().HasForeignKey(p => new { p.GymId, p.PriceId })
                .HasPrincipalKey(p => new { p.GymId, p.Id }).OnDelete(DeleteBehavior.Restrict);
            payment.HasOne<Payment>().WithMany().HasForeignKey(p => new { p.GymId, p.StudentId, p.PreviousPaymentId })
                .HasPrincipalKey(p => new { p.GymId, p.StudentId, p.Id }).OnDelete(DeleteBehavior.Restrict);
            payment.ToTable("Payments", t =>
            {
                t.HasCheckConstraint("CK_Payments_Amount", "[AmountCents] > 0 AND [AmountCents] <= 10000000000");
                t.HasCheckConstraint("CK_Payments_Method", "[Method] IN (1, 2, 3)");
                t.HasCheckConstraint("CK_Payments_Interval", "[Sequence] > 0 AND [CoverageStart] <= [ExtensionStart] AND [PaidOn] <= [ExtensionStart] AND [ExtensionStart] < [CoverageEndExclusive]");
                t.HasCheckConstraint("CK_Payments_Void", "([VoidedAt] IS NULL AND [VoidedBy] IS NULL AND [VoidReason] IS NULL) OR ([VoidedAt] IS NOT NULL AND [VoidedBy] IS NOT NULL AND [VoidReason] IS NOT NULL)");
            });
        });
    }
}
