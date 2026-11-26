using Entity;
using Microsoft.EntityFrameworkCore;

namespace DAL;

public static class VideoLibraryConfiguration
{
    public static void Configure(ModelBuilder builder)
    {
        builder.Entity<VideoObject>(entity =>
        {
            entity.ToTable("VideoObjects", table => table.HasCheckConstraint("CK_VideoObjects_State", "[State] IN ('Pending', 'Active', 'Cleanup')"));
            entity.HasKey(v => v.Key);
            entity.Property(v => v.Key).HasMaxLength(128);
            entity.Property(v => v.State).HasMaxLength(16).IsRequired();
            entity.HasIndex(v => new { v.State, v.NextAttemptAt });
            entity.HasOne<Gym>().WithMany().HasForeignKey(v => v.GymId).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<LibraryVideo>(entity =>
        {
            entity.ToTable("LibraryVideos", table =>
            {
                table.HasCheckConstraint("CK_LibraryVideos_Length", "[Length] > 0 AND [Length] <= 100000000");
                table.HasCheckConstraint("CK_LibraryVideos_Category", "[Category] IN ('informational', 'exercise')");
            });
            entity.HasKey(v => v.Id);
            entity.Property(v => v.Revision).IsConcurrencyToken();
            entity.Property(v => v.Title).HasMaxLength(200).IsRequired();
            entity.Property(v => v.Category).HasMaxLength(16).IsRequired();
            entity.Property(v => v.Description).HasMaxLength(2000);
            entity.Property(v => v.ObjectKey).HasMaxLength(128).IsRequired();
            entity.HasIndex(v => v.ObjectKey).IsUnique();
            entity.HasIndex(v => new { v.GymId, v.CreatedAt, v.Id });
            entity.HasOne<Gym>().WithMany().HasForeignKey(v => v.GymId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<VideoObject>().WithMany().HasForeignKey(v => v.ObjectKey).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
