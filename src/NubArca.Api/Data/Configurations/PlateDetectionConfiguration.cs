using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NubArca.Api.Domain;

namespace NubArca.Api.Data.Configurations;

public class PlateDetectionConfiguration : IEntityTypeConfiguration<PlateDetection>
{
    public void Configure(EntityTypeBuilder<PlateDetection> builder)
    {
        builder.ToTable("plate_detections", t =>
        {
            // Normalized geometry must be non-negative (mirrors face_detections).
            t.HasCheckConstraint("ck_plate_detections_box_width_non_negative", "\"BoundingBoxWidth\" >= 0");
            t.HasCheckConstraint("ck_plate_detections_box_height_non_negative", "\"BoundingBoxHeight\" >= 0");
        });

        builder.HasKey(d => d.Id);
        builder.Property(d => d.Id).ValueGeneratedNever();

        builder.Property(d => d.Text).IsRequired().HasMaxLength(64);
        builder.Property(d => d.NormalizedText).IsRequired().HasMaxLength(64);
        builder.Property(d => d.CountryHint).HasMaxLength(8);
        builder.Property(d => d.RegionHint).HasMaxLength(32);
        builder.Property(d => d.ModelProfileKey).IsRequired().HasMaxLength(64);
        // Optional refined polygon (opaque JSON, kept out of every DTO).
        builder.Property(d => d.PolygonJson).HasColumnType("text");
        builder.Property(d => d.CreatedAt).HasColumnType("timestamp with time zone");
        builder.Property(d => d.UpdatedAt).HasColumnType("timestamp with time zone");

        builder.HasIndex(d => new { d.OwnerUserId, d.PlateImageId })
            .HasDatabaseName("ix_plate_detections_owner_image");
        builder.HasIndex(d => new { d.OwnerUserId, d.NormalizedText })
            .HasDatabaseName("ix_plate_detections_owner_normalized_text");
        builder.HasIndex(d => d.PlateAnalysisJobId)
            .HasDatabaseName("ix_plate_detections_job");

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(d => d.OwnerUserId)
            .OnDelete(DeleteBehavior.Restrict);

        // Detections die with their PlateImage (hard delete) and with their job.
        builder.HasOne<PlateImage>()
            .WithMany()
            .HasForeignKey(d => d.PlateImageId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<PlateAnalysisJob>()
            .WithMany()
            .HasForeignKey(d => d.PlateAnalysisJobId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
