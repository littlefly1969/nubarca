using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NubArca.Api.Domain;

namespace NubArca.Api.Data.Configurations;

public class BlobMetadataConfiguration : IEntityTypeConfiguration<BlobMetadata>
{
    public void Configure(EntityTypeBuilder<BlobMetadata> builder)
    {
        builder.ToTable("blob_metadata", t =>
        {
            t.HasCheckConstraint(
                "ck_blob_metadata_size_bytes_non_negative",
                "\"SizeBytes\" >= 0");
            t.HasCheckConstraint(
                "ck_blob_metadata_width_positive",
                "\"Width\" IS NULL OR \"Width\" > 0");
            t.HasCheckConstraint(
                "ck_blob_metadata_height_positive",
                "\"Height\" IS NULL OR \"Height\" > 0");
            t.HasCheckConstraint(
                "ck_blob_metadata_pixel_count_non_negative",
                "\"PixelCount\" IS NULL OR \"PixelCount\" >= 0");
            t.HasCheckConstraint(
                "ck_blob_metadata_duration_non_negative",
                "\"DurationSeconds\" IS NULL OR \"DurationSeconds\" >= 0");
            t.HasCheckConstraint(
                "ck_blob_metadata_frame_rate_non_negative",
                "\"FrameRate\" IS NULL OR \"FrameRate\" >= 0");
            t.HasCheckConstraint(
                "ck_blob_metadata_video_bitrate_non_negative",
                "\"VideoBitrate\" IS NULL OR \"VideoBitrate\" >= 0");
        });

        builder.HasKey(m => m.Id);

        builder.Property(m => m.Id)
            .ValueGeneratedNever();

        builder.Property(m => m.SizeBytes)
            .IsRequired();

        builder.Property(m => m.DetectedContentType)
            .HasMaxLength(255);

        builder.Property(m => m.MediaCategory)
            .IsRequired()
            .HasMaxLength(32);

        builder.Property(m => m.DetectedFormat)
            .HasMaxLength(64);

        builder.Property(m => m.ThumbnailStatus)
            .IsRequired()
            .HasMaxLength(32);

        builder.Property(m => m.ExtractionStatus)
            .IsRequired()
            .HasMaxLength(32);

        builder.Property(m => m.ExtractionErrorCode)
            .HasMaxLength(100);

        builder.Property(m => m.ExtractedAt)
            .HasColumnType("timestamp with time zone");

        // Internal raw embedded-metadata document. jsonb on PostgreSQL; SQLite
        // stores it as text (it ignores the unknown type name and uses dynamic
        // typing). NEVER serialized to a normal DTO.
        builder.Property(m => m.RawMetadataJson)
            .HasColumnType("jsonb");

        // ---- Curated typed embedded fields (slice 54) ----------------------
        builder.Property(m => m.DateTaken)
            .HasColumnType("timestamp with time zone");
        builder.Property(m => m.DateTakenSource).HasMaxLength(32);
        builder.Property(m => m.DateTakenOffset).HasMaxLength(16);

        builder.Property(m => m.CameraMake).HasMaxLength(128);
        builder.Property(m => m.CameraModel).HasMaxLength(128);
        builder.Property(m => m.LensMake).HasMaxLength(128);
        builder.Property(m => m.LensModel).HasMaxLength(128);
        builder.Property(m => m.Software).HasMaxLength(256);
        builder.Property(m => m.BodySerialNumber).HasMaxLength(128);
        builder.Property(m => m.LensSerialNumber).HasMaxLength(128);

        builder.Property(m => m.ExposureTime).HasMaxLength(64);
        builder.Property(m => m.ExposureBias).HasMaxLength(64);
        builder.Property(m => m.ExposureProgram).HasMaxLength(64);
        builder.Property(m => m.MeteringMode).HasMaxLength(64);
        builder.Property(m => m.Flash).HasMaxLength(128);
        builder.Property(m => m.WhiteBalance).HasMaxLength(64);

        builder.Property(m => m.ColorSpace).HasMaxLength(64);
        builder.Property(m => m.IccProfileName).HasMaxLength(256);

        // ---- Video probe fields (ffprobe) ----------------------------------
        builder.Property(m => m.VideoCodec).HasMaxLength(64);
        builder.Property(m => m.AudioCodec).HasMaxLength(64);

        builder.Property(m => m.VideoExtractionStatus)
            .IsRequired()
            .HasMaxLength(32)
            .HasDefaultValue(MetadataStatuses.Pending);
        builder.Property(m => m.VideoExtractionErrorCode).HasMaxLength(100);
        builder.Property(m => m.VideoExtractedAt)
            .HasColumnType("timestamp with time zone");

        builder.Property(m => m.CreatedAt)
            .HasColumnType("timestamp with time zone");

        builder.Property(m => m.UpdatedAt)
            .HasColumnType("timestamp with time zone");

        // Exactly one metadata row per blob. The blob is global + deduplicated,
        // so this row is shared by every FileItem that references the blob.
        builder.HasIndex(m => m.BlobObjectId)
            .IsUnique()
            .HasDatabaseName("ux_blob_metadata_blob_object");

        builder.HasOne<BlobObject>()
            .WithMany()
            .HasForeignKey(m => m.BlobObjectId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
