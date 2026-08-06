using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NubArca.Api.Domain.Ai;

namespace NubArca.Api.Data.Configurations.Ai;

public class VideoSemanticEmbeddingStatusConfiguration
    : IEntityTypeConfiguration<VideoSemanticEmbeddingStatus>
{
    public void Configure(EntityTypeBuilder<VideoSemanticEmbeddingStatus> builder)
    {
        builder.ToTable("video_semantic_embedding_statuses", t =>
        {
            t.HasCheckConstraint(
                "ck_video_semantic_embedding_statuses_counts_non_negative",
                "\"ExpectedSampleCount\" >= 0 AND \"CompletedSampleCount\" >= 0 "
                + "AND \"FailedSampleCount\" >= 0 AND \"AttemptCount\" >= 0");
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

        // ONE aggregate row per manifest and profile. The segmentation version
        // is implied by the manifest, so a reindex coexists with the old rows.
        builder.HasIndex(e => new { e.VideoSemanticIndexId, e.ProfileId })
            .IsUnique()
            .HasDatabaseName("ux_video_semantic_embedding_statuses_index_profile");

        // Backs the backfill candidate scan ("which manifests still need
        // embedding work for this profile").
        builder.HasIndex(e => new { e.ProfileId, e.Status })
            .HasDatabaseName("ix_video_semantic_embedding_statuses_profile_status");

        builder.HasOne<VideoSemanticIndex>()
            .WithMany()
            .HasForeignKey(e => e.VideoSemanticIndexId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<AiProfile>()
            .WithMany()
            .HasForeignKey(e => e.ProfileId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
