using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NubArca.Api.Domain.Ai;

namespace NubArca.Api.Data.Configurations.Ai;

public class VideoSemanticSampleEmbeddingConfiguration
    : IEntityTypeConfiguration<VideoSemanticSampleEmbedding>
{
    public void Configure(EntityTypeBuilder<VideoSemanticSampleEmbedding> builder)
    {
        builder.ToTable("video_semantic_sample_embeddings", t =>
        {
            // A completed row carries a positive dimension; a failed row a zero
            // one (no payload). Negative is never valid.
            t.HasCheckConstraint(
                "ck_video_semantic_sample_embeddings_dimension_non_negative",
                "\"Dimension\" >= 0");
            t.HasCheckConstraint(
                "ck_video_semantic_sample_embeddings_attempts_non_negative",
                "\"AttemptCount\" >= 0");
        });

        builder.HasKey(e => e.Id);

        builder.Property(e => e.Id)
            .ValueGeneratedNever();

        builder.Property(e => e.EmbeddingBytes)
            .IsRequired();

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

        // ONE embedding per sample and profile — the core invariant. Different
        // profiles coexist; a segmentation reindex creates NEW samples, so
        // versions coexist through the manifest tree, never through this key.
        builder.HasIndex(e => new { e.VideoSemanticSampleId, e.ProfileId })
            .IsUnique()
            .HasDatabaseName("ux_video_semantic_sample_embeddings_sample_profile");

        // Backs the per-profile progress/retry scans.
        builder.HasIndex(e => new { e.ProfileId, e.Status })
            .HasDatabaseName("ix_video_semantic_sample_embeddings_profile_status");

        builder.HasOne<VideoSemanticSample>()
            .WithMany()
            .HasForeignKey(e => e.VideoSemanticSampleId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<AiProfile>()
            .WithMany()
            .HasForeignKey(e => e.ProfileId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
