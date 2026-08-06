using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NubArca.Api.Domain.Ai;

namespace NubArca.Api.Data.Configurations.Ai;

public class VideoFaceAnalysisStatusConfiguration
    : IEntityTypeConfiguration<VideoFaceAnalysisStatus>
{
    public void Configure(EntityTypeBuilder<VideoFaceAnalysisStatus> builder)
    {
        builder.ToTable("video_face_analysis_statuses", t =>
        {
            t.HasCheckConstraint(
                "ck_video_face_analysis_statuses_version_positive",
                "\"AnalysisVersion\" > 0");
            t.HasCheckConstraint(
                "ck_video_face_analysis_statuses_counts_non_negative",
                "\"PlannedFrameCount\" >= 0 AND \"ProcessedFrameCount\" >= 0 "
                + "AND \"FailedFrameCount\" >= 0 AND \"TrackCount\" >= 0 "
                + "AND \"AttemptCount\" >= 0");
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

        // ONE analysis per manifest, analysis version and profile PAIR — the
        // core scoping invariant. A reindex, a new analysis version or a
        // different face package all coexist instead of overwriting.
        builder.HasIndex(e => new
        {
            e.VideoSemanticIndexId,
            e.AnalysisVersion,
            e.DetectionProfileId,
            e.EmbeddingProfileId,
        })
            .IsUnique()
            .HasDatabaseName("ux_video_face_analysis_statuses_scope");

        // Backs the backfill candidate scan ("which manifests still need face
        // analysis at version N for this profile pair").
        builder.HasIndex(e => new { e.EmbeddingProfileId, e.AnalysisVersion, e.Status })
            .HasDatabaseName("ix_video_face_analysis_statuses_profile_version_status");

        builder.HasOne<VideoSemanticIndex>()
            .WithMany()
            .HasForeignKey(e => e.VideoSemanticIndexId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<AiProfile>()
            .WithMany()
            .HasForeignKey(e => e.DetectionProfileId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<AiProfile>()
            .WithMany()
            .HasForeignKey(e => e.EmbeddingProfileId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
