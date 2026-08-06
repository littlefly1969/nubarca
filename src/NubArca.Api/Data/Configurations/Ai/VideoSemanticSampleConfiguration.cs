using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NubArca.Api.Domain.Ai;

namespace NubArca.Api.Data.Configurations.Ai;

public class VideoSemanticSampleConfiguration : IEntityTypeConfiguration<VideoSemanticSample>
{
    public void Configure(EntityTypeBuilder<VideoSemanticSample> builder)
    {
        builder.ToTable("video_semantic_samples", t =>
        {
            t.HasCheckConstraint(
                "ck_video_semantic_samples_timestamp_non_negative",
                "\"TimestampMilliseconds\" >= 0");
            t.HasCheckConstraint(
                "ck_video_semantic_samples_index_non_negative",
                "\"SampleIndex\" >= 0");
        });

        builder.HasKey(e => e.Id);

        builder.Property(e => e.Id)
            .ValueGeneratedNever();

        builder.Property(e => e.SelectionReason)
            .IsRequired()
            .HasMaxLength(32);

        builder.Property(e => e.CreatedAt)
            .HasColumnType("timestamp with time zone");

        builder.HasIndex(e => new { e.VideoSemanticSegmentId, e.SampleIndex })
            .IsUnique()
            .HasDatabaseName("ux_video_semantic_samples_segment_ordinal");

        builder.HasOne<VideoSemanticSegment>()
            .WithMany()
            .HasForeignKey(e => e.VideoSemanticSegmentId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
