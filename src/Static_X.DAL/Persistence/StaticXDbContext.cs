using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Text;
using Entity;

namespace DAL
{
    public class StaticXDbContext : IdentityUserContext<IdentityAccount>
    {
        public StaticXDbContext(DbContextOptions<StaticXDbContext> options) : base(options)
        {
        }

        public DbSet<Student> Alumnos => Set<Student>();
        public DbSet<Gym> Gimnasios => Set<Gym>();
        public DbSet<ActivationInvitation> ActivationInvitations => Set<ActivationInvitation>();
        public DbSet<ActivationOutbox> ActivationOutbox => Set<ActivationOutbox>();
        public DbSet<GymProvisioningRecord> ProvisioningRequests => Set<GymProvisioningRecord>();
        public DbSet<PasswordRecoveryOutbox> PasswordRecoveryOutbox => Set<PasswordRecoveryOutbox>();
        public DbSet<TenantInvitationRequest> TenantInvitationRequests => Set<TenantInvitationRequest>();
        public DbSet<Payment> Payments => Set<Payment>();
        public DbSet<GymPassPrice> GymPassPrices => Set<GymPassPrice>();
        public DbSet<StudentRoutine> StudentRoutines => Set<StudentRoutine>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            PaymentConfiguration.Configure(modelBuilder);
            StudentRoutineConfiguration.Configure(modelBuilder);
            VideoLibraryConfiguration.Configure(modelBuilder);
            modelBuilder.Entity<IdentityAccount>(account =>
            {
                account.Property(a => a.Email).IsRequired();
                account.Property(a => a.NormalizedEmail).IsRequired();
                account.Property(a => a.IsEnabled).HasDefaultValue(true);
                account.ToTable("AspNetUsers", table => table.HasCheckConstraint("CK_AspNetUsers_StudentSuspension",
                    "[StudentAccessSuspended] = 0 OR ([IsEnabled] = 0 AND [Role] = 'Student')"));
                account.Property(a => a.Role).HasConversion<string>().HasMaxLength(16).IsRequired();
                account.HasOne<Gym>().WithMany().HasForeignKey(a => a.GymId)
                    .OnDelete(DeleteBehavior.Restrict);
                account.HasIndex(a => new { a.GymId, a.NormalizedEmail }).IsUnique();
                account.HasIndex(a => a.NormalizedEmail, "GlobalEmailIndex").IsUnique()
                    .HasFilter("[GymId] IS NULL");
                account.ToTable("AspNetUsers", table => table.HasCheckConstraint("CK_AspNetUsers_RoleGym",
                    "([Role] = 'SuperAdmin' AND [GymId] IS NULL) OR ([Role] IN ('GymAdmin', 'Trainer', 'Student') AND [GymId] IS NOT NULL)"));
            });

            modelBuilder.Entity<Gym>(gym =>
            {
                gym.ToTable("Gimnasios");
                gym.Property(g => g.Name).HasColumnName("Nombre").HasMaxLength(200).IsRequired();
            });

            modelBuilder.Entity<Student>(student =>
            {
                student.ToTable("Alumnos");
                student.Property(s => s.GymId).HasColumnName("GimnasioId");
                student.Property(s => s.FirstName).HasColumnName("Nombre");
                student.Property(s => s.LastName).HasColumnName("Apellido");
                student.Property(s => s.IsActive).HasColumnName("Activo").IsRequired().HasDefaultValue(true);
                student.Property(s => s.Email).HasMaxLength(320).IsRequired();
                student.Property(s => s.AccountId).IsConcurrencyToken();
                student.HasOne<IdentityAccount>().WithMany().HasForeignKey(s => s.AccountId).OnDelete(DeleteBehavior.Restrict);
                student.HasIndex(s => s.AccountId).IsUnique().HasFilter("[AccountId] IS NOT NULL");
                student.HasOne<Gym>().WithMany().HasForeignKey(s => s.GymId)
                    .OnDelete(DeleteBehavior.Restrict);
                student.HasIndex(s => new { s.GymId, s.Email }).IsUnique().HasDatabaseName("IX_Alumnos_GimnasioId_Email");
            });
            modelBuilder.Entity<GymProvisioningRecord>(request =>
            {
                request.ToTable("ProvisioningRequests");
                request.HasKey(r => r.IdempotencyKey);
                request.Property(r => r.IdempotencyKey).HasColumnName("Key").HasMaxLength(128);
                request.Property(r => r.InitialAdminAccountId).HasColumnName("AccountId");
                request.HasIndex(r => r.GymId).IsUnique();
                request.HasOne<Gym>().WithMany().HasForeignKey(r => r.GymId).OnDelete(DeleteBehavior.Restrict);
                request.HasOne<IdentityAccount>().WithMany().HasForeignKey(r => r.InitialAdminAccountId).OnDelete(DeleteBehavior.Restrict);
            });
            modelBuilder.Entity<ActivationInvitation>(invite =>
            {
                invite.HasKey(i => i.Id);
                invite.Property(i => i.ExpectedRole).HasConversion<string>().HasMaxLength(16).HasDefaultValue(AccountRole.GymAdmin);
                invite.ToTable("ActivationInvitations", table => table.HasCheckConstraint("CK_ActivationInvitations_ExpectedRole",
                    "[ExpectedRole] IN ('GymAdmin', 'Trainer', 'Student')"));
                invite.Property(i => i.TokenHash).HasMaxLength(64).IsRequired();
                invite.HasIndex(i => i.TokenHash).IsUnique();
                invite.HasIndex(i => new { i.GymId, i.AccountId });
                invite.HasOne<Gym>().WithMany().HasForeignKey(i => i.GymId).OnDelete(DeleteBehavior.Restrict);
                invite.HasOne<IdentityAccount>().WithMany().HasForeignKey(i => i.AccountId).OnDelete(DeleteBehavior.Restrict);
            });
            modelBuilder.Entity<ActivationOutbox>(outbox =>
            {
                outbox.HasKey(o => o.Id);
                outbox.Property(o => o.ProtectedToken).IsRequired();
                outbox.HasIndex(o => o.InvitationId).IsUnique();
                outbox.HasIndex(o => new { o.NextAttemptAt, o.LeaseExpiresAt });
                outbox.HasOne<ActivationInvitation>().WithMany().HasForeignKey(o => o.InvitationId)
                    .OnDelete(DeleteBehavior.Restrict);
            });
            modelBuilder.Entity<PasswordRecoveryOutbox>(outbox =>
            {
                outbox.HasKey(o => o.Id);
                outbox.Property(o => o.Role).HasConversion<string>().HasMaxLength(16);
                outbox.Property(o => o.StampBinding).HasMaxLength(64).IsRequired();
                outbox.Property(o => o.ProtectedPayload).IsRequired();
                outbox.HasIndex(o => new { o.AccountId, o.SentAt, o.ExpiresAt });
                outbox.HasIndex(o => new { o.NextAttemptAt, o.LeaseExpiresAt });
                outbox.HasOne<IdentityAccount>().WithMany().HasForeignKey(o => o.AccountId).OnDelete(DeleteBehavior.Restrict);
                outbox.HasOne<Gym>().WithMany().HasForeignKey(o => o.GymId).OnDelete(DeleteBehavior.Restrict);
            });
            modelBuilder.Entity<TenantInvitationRequest>(request =>
            {
                request.HasKey(r => new { r.GymId, r.Key });
                request.Property(r => r.Key).HasMaxLength(128);
                request.Property(r => r.NormalizedEmail).HasMaxLength(256).IsRequired();
                request.Property(r => r.Role).HasConversion<string>().HasMaxLength(16);
                request.HasOne<Gym>().WithMany().HasForeignKey(r => r.GymId).OnDelete(DeleteBehavior.Restrict);
                request.HasOne<IdentityAccount>().WithMany().HasForeignKey(r => r.AccountId).OnDelete(DeleteBehavior.Restrict);
                request.HasOne<Student>().WithMany().HasForeignKey(r => r.StudentId).OnDelete(DeleteBehavior.Restrict);
                request.ToTable("TenantInvitationRequests", table => table.HasCheckConstraint("CK_TenantInvitationRequests_Target",
                    "([Role] = 'Trainer' AND [StudentId] IS NULL) OR ([Role] = 'Student' AND [StudentId] > 0 AND [StudentId] IS NOT NULL)"));
            });
        }
    }
}
