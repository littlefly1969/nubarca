using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NubArca.Api.Domain;
using NubArca.Api.Domain.Ai;

namespace NubArca.Api.Data.Configurations.Ai;

public class VideoSemanticIndexConfiguration : IEntityTypeConfiguration<VideoSemanticIndex>
{
    public void Configure(EntityTypeBuilder<VideoSemanticIndex> builder)
    {
        builder.ToTable("video_semantic_indexes", t =>
        {
            t.HasCheckConstraint(
                "ck_video_semantic_indexes_version_positive",
                "\"SegmentationVersion\" > 0");
            t.HasCheckConstraint(
                "ck_video_semantic_indexes_duration_positive",
                "\"DurationMilliseconds\" IS NULL OR \"DurationMilliseconds\" > 0");
            t.HasCheckConstraint(
                "ck_video_semantic_indexes_counts_non_negative",
                "\"SegmentCount\" >= 0 AND \"SampleCount\" >= 0 AND \"AttemptCount\" >= 0");
        });

        builder.HasKey(e => e.Id);

        builder.Property(e => e.Id)
            .ValueGeneratedNever();

        builder.Property(e => e.Status)
            .IsRequired()
            .HasMaxLength(32);

        builder.Property(e => e.ErrorCode)
            .HasMaxLength(64);

        builder.Property(e => e.CreatedAt)
            .HasColumnType("timestamp with time zone");
        builder.Property(e => e.UpdatedAt)
            .HasColumnType("timestamp with time zone");
        builder.Property(e => e.CompletedAt)
            .HasColumnType("timestamp with time zone");

        // ONE manifest per blob and segmentation version — the core invariant.
        // Duplicate FileItem references reuse the same row; a new version
        // coexists with the old one instead of overwriting it.
        builder.HasIndex(e => new { e.BlobObjectId, e.SegmentationVersion })
            .IsUnique()
            .HasDatabaseName("ux_video_semantic_indexes_blob_version");

        // Backs the backfill candidate scan ("what still needs work at version
        // N" / "retry the failures at version N").
        builder.HasIndex(e => new { e.Status, e.SegmentationVersion })
            .HasDatabaseName("ix_video_semantic_indexes_status_version");

        builder.HasOne<BlobObject>()
            .WithMany()
            .HasForeignKey(e => e.BlobObjectId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
