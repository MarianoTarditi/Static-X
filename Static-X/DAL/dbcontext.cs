using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Text;
using Entity;

namespace DAL
{
    public class dbcontext : IdentityUserContext<GymAccount>
    {
        public dbcontext(DbContextOptions<dbcontext> options) : base(options)
        {
        }

        public DbSet<Alumno> Alumnos => Set<Alumno>();
        public DbSet<Gimnasio> Gimnasios => Set<Gimnasio>();
        public DbSet<ActivationInvitation> ActivationInvitations => Set<ActivationInvitation>();
        public DbSet<ActivationOutbox> ActivationOutbox => Set<ActivationOutbox>();
        public DbSet<ProvisioningRequest> ProvisioningRequests => Set<ProvisioningRequest>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<GymAccount>(account =>
            {
                account.Property(a => a.Email).IsRequired();
                account.Property(a => a.NormalizedEmail).IsRequired();
                account.Property(a => a.Role).HasConversion<string>().HasMaxLength(16).IsRequired();
                account.HasOne<Gimnasio>().WithMany().HasForeignKey(a => a.GymId)
                    .OnDelete(DeleteBehavior.Restrict);
                account.HasIndex(a => new { a.GymId, a.NormalizedEmail }).IsUnique();
            });

            modelBuilder.Entity<Gimnasio>(gym =>
            {
                gym.Property(g => g.Nombre).HasMaxLength(200).IsRequired();
            });

            modelBuilder.Entity<Alumno>(student =>
            {
                student.Property(s => s.Email).HasMaxLength(320).IsRequired();
                student.HasOne<Gimnasio>().WithMany().HasForeignKey(s => s.GimnasioId)
                    .OnDelete(DeleteBehavior.Restrict);
                student.HasIndex(s => new { s.GimnasioId, s.Email }).IsUnique();
            });
            modelBuilder.Entity<ProvisioningRequest>(request =>
            {
                request.HasKey(r => r.Key);
                request.Property(r => r.Key).HasMaxLength(128);
                request.HasIndex(r => r.GymId).IsUnique();
                request.HasOne<Gimnasio>().WithMany().HasForeignKey(r => r.GymId).OnDelete(DeleteBehavior.Restrict);
                request.HasOne<GymAccount>().WithMany().HasForeignKey(r => r.AccountId).OnDelete(DeleteBehavior.Restrict);
            });
            modelBuilder.Entity<ActivationInvitation>(invite =>
            {
                invite.HasKey(i => i.Id);
                invite.Property(i => i.TokenHash).HasMaxLength(64).IsRequired();
                invite.HasIndex(i => i.TokenHash).IsUnique();
                invite.HasIndex(i => new { i.GymId, i.AccountId });
                invite.HasOne<Gimnasio>().WithMany().HasForeignKey(i => i.GymId).OnDelete(DeleteBehavior.Restrict);
                invite.HasOne<GymAccount>().WithMany().HasForeignKey(i => i.AccountId).OnDelete(DeleteBehavior.Restrict);
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
        }
    }
}
