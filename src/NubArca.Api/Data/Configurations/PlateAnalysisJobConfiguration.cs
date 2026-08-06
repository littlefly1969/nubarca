using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NubArca.Api.Domain;

namespace NubArca.Api.Data.Configurations;

public class PlateAnalysisJobConfiguration : IEntityTypeConfiguration<PlateAnalysisJob>
{
    public void Configure(EntityTypeBuilder<PlateAnalysisJob> builder)
    {
        builder.ToTable("plate_analysis_jobs");
        builder.HasKey(j => j.Id);
        builder.Property(j => j.Id).ValueGeneratedNever();

        builder.Property(j => j.Status).IsRequired().HasMaxLength(32);
        builder.Property(j => j.ErrorCode).HasMaxLength(64);
        builder.Property(j => j.ErrorMessageSafe).HasMaxLength(512);
        builder.Property(j => j.ProfileKey).IsRequired().HasMaxLength(64);
        builder.Property(j => j.RequestedAt).HasColumnType("timestamp with time zone");
        builder.Property(j => j.StartedAt).HasColumnType("timestamp with time zone");
        builder.Property(j => j.CompletedAt).HasColumnType("timestamp with time zone");
        builder.Property(j => j.FailedAt).HasColumnType("timestamp with time zone");
        builder.Property(j => j.CreatedAt).HasColumnType("timestamp with time zone");
        builder.Property(j => j.UpdatedAt).HasColumnType("timestamp with time zone");

        builder.HasIndex(j => new { j.OwnerUserId, j.PlateImageId, j.Status })
            .HasDatabaseName("ix_plate_analysis_jobs_owner_image_status");
        builder.HasIndex(j => new { j.OwnerUserId, j.RequestedAt })
            .HasDatabaseName("ix_plate_analysis_jobs_owner_requested");

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(j => j.OwnerUserId)
            .OnDelete(DeleteBehavior.Restrict);

        // Deleting a PlateImage cascades to its analysis jobs — Slice 1 uses hard
        // delete for PlateImage, so no orphan analysis rows may remain.
        builder.HasOne<PlateImage>()
            .WithMany()
            .HasForeignKey(j => j.PlateImageId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
