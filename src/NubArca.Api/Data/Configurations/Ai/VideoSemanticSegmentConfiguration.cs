using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NubArca.Api.Domain.Ai;

namespace NubArca.Api.Data.Configurations.Ai;

public class VideoSemanticSegmentConfiguration : IEntityTypeConfiguration<VideoSemanticSegment>
{
    public void Configure(EntityTypeBuilder<VideoSemanticSegment> builder)
    {
        builder.ToTable("video_semantic_segments", t =>
        {
            // [start, end) with a strictly positive length — a zero-length or
            // inverted interval is not representable.
            t.HasCheckConstraint(
                "ck_video_semantic_segments_interval",
                "\"StartMilliseconds\" >= 0 AND \"EndMilliseconds\" > \"StartMilliseconds\"");
            t.HasCheckConstraint(
                "ck_video_semantic_segments_index_non_negative",
                "\"SegmentIndex\" >= 0");
        });

        builder.HasKey(e => e.Id);

        builder.Property(e => e.Id)
            .ValueGeneratedNever();

        builder.Property(e => e.BoundaryReason)
            .IsRequired()
            .HasMaxLength(32);

        builder.Property(e => e.CreatedAt)
            .HasColumnType("timestamp with time zone");

        builder.HasIndex(e => new { e.VideoSemanticIndexId, e.SegmentIndex })
            .IsUnique()
            .HasDatabaseName("ux_video_semantic_segments_index_ordinal");

        // Backs "the segments of this manifest, in time order" — the read shape
        // every later consumer uses.
        builder.HasIndex(e => new { e.VideoSemanticIndexId, e.StartMilliseconds })
            .HasDatabaseName("ix_video_semantic_segments_index_start");

        builder.HasOne<VideoSemanticIndex>()
            .WithMany()
            .HasForeignKey(e => e.VideoSemanticIndexId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
