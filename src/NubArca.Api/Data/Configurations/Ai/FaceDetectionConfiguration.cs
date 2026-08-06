using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NubArca.Api.Domain;
using NubArca.Api.Domain.Ai;

namespace NubArca.Api.Data.Configurations.Ai;

public class FaceDetectionConfiguration : IEntityTypeConfiguration<FaceDetection>
{
    public void Configure(EntityTypeBuilder<FaceDetection> builder)
    {
        builder.ToTable("face_detections", t =>
        {
            t.HasCheckConstraint(
                "ck_face_detections_box_width_non_negative",
                "\"BoundingBoxWidth\" >= 0");
            t.HasCheckConstraint(
                "ck_face_detections_box_height_non_negative",
                "\"BoundingBoxHeight\" >= 0");
            t.HasCheckConstraint(
                "ck_face_detections_face_index_non_negative",
                "\"FaceIndex\" >= 0");
        });

        builder.HasKey(f => f.Id);

        builder.Property(f => f.Id)
            .ValueGeneratedNever();

        builder.Property(f => f.DetectorProfileKey)
            .HasMaxLength(128);

        builder.Property(f => f.CreatedAt)
            .HasColumnType("timestamp with time zone");

        builder.Property(f => f.UpdatedAt)
            .HasColumnType("timestamp with time zone");

        // Idempotent re-detection: exactly one row per (blob, profile, faceIndex).
        builder.HasIndex(f => new { f.BlobObjectId, f.ProfileId, f.FaceIndex })
            .IsUnique()
            .HasDatabaseName("ux_face_detections_blob_profile_index");

        // Coverage / candidate scans by profile.
        builder.HasIndex(f => new { f.ProfileId, f.BlobObjectId })
            .HasDatabaseName("ix_face_detections_profile_blob");

        // Derived data dies with its source blob.
        builder.HasOne<BlobObject>()
            .WithMany()
            .HasForeignKey(f => f.BlobObjectId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<AiProfile>()
            .WithMany()
            .HasForeignKey(f => f.ProfileId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
