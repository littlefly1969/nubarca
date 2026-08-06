using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NubArca.Api.Domain;
using NubArca.Api.Domain.Ai;

namespace NubArca.Api.Data.Configurations.Ai;

public class VideoFaceTrackConfiguration : IEntityTypeConfiguration<VideoFaceTrack>
{
    public void Configure(EntityTypeBuilder<VideoFaceTrack> builder)
    {
        builder.ToTable("video_face_tracks", t =>
        {
            // The temporal invariant, enforced by the DATABASE and not only by
            // the service: a representative timestamp always lies inside the
            // interval the track covers.
            t.HasCheckConstraint(
                "ck_video_face_tracks_interval_ordered",
                "\"StartMilliseconds\" >= 0 "
                + "AND \"StartMilliseconds\" <= \"RepresentativeTimestampMilliseconds\" "
                + "AND \"RepresentativeTimestampMilliseconds\" <= \"EndMilliseconds\"");
            t.HasCheckConstraint(
                "ck_video_face_tracks_detections_positive",
                "\"DetectionCount\" > 0");
            t.HasCheckConstraint(
                "ck_video_face_tracks_dimension_positive",
                "\"EmbeddingDimension\" > 0");
            t.HasCheckConstraint(
                "ck_video_face_tracks_index_non_negative",
                "\"TrackIndex\" >= 0");
            t.HasCheckConstraint(
                "ck_video_face_tracks_quality_unit_range",
                "\"QualityScore\" >= 0 AND \"QualityScore\" <= 1");
            t.HasCheckConstraint(
                "ck_video_face_tracks_bbox_unit_range",
                "\"RepresentativeBoundingBoxX\" >= 0 AND \"RepresentativeBoundingBoxX\" <= 1 "
                + "AND \"RepresentativeBoundingBoxY\" >= 0 AND \"RepresentativeBoundingBoxY\" <= 1 "
                + "AND \"RepresentativeBoundingBoxWidth\" >= 0 AND \"RepresentativeBoundingBoxWidth\" <= 1 "
                + "AND \"RepresentativeBoundingBoxHeight\" >= 0 AND \"RepresentativeBoundingBoxHeight\" <= 1");
        });

        builder.HasKey(e => e.Id);

        builder.Property(e => e.Id)
            .ValueGeneratedNever();

        builder.Property(e => e.EmbeddingBytes)
            .IsRequired();

        builder.Property(e => e.CreatedAt)
            .HasColumnType("timestamp with time zone");
        builder.Property(e => e.UpdatedAt)
            .HasColumnType("timestamp with time zone");

        // ONE track per analysis and ordinal — makes a re-analysis idempotent
        // and the track order stable.
        builder.HasIndex(e => new { e.VideoFaceAnalysisStatusId, e.TrackIndex })
            .IsUnique()
            .HasDatabaseName("ux_video_face_tracks_analysis_ordinal");

        builder.HasOne<VideoFaceAnalysisStatus>()
            .WithMany()
            .HasForeignKey(e => e.VideoFaceAnalysisStatusId)
            .OnDelete(DeleteBehavior.Cascade);

        // The optional representative crop lives in the DERIVED store. VFACE-01
        // never writes it; the FK is Restrict so a crop can never be orphaned by
        // a blob delete without an explicit cleanup decision in VFACE-02.
        builder.HasOne<BlobObject>()
            .WithMany()
            .HasForeignKey(e => e.RepresentativeCropBlobObjectId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
