using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NubArca.Api.Domain;

namespace NubArca.Api.Data.Configurations;

public class PlateRedactedMediaConfiguration : IEntityTypeConfiguration<PlateRedactedMedia>
{
    public void Configure(EntityTypeBuilder<PlateRedactedMedia> builder)
    {
        builder.ToTable("plate_redacted_media");
        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id).ValueGeneratedNever();

        builder.Property(m => m.SourceKind).IsRequired().HasMaxLength(16);
        builder.Property(m => m.ProfileKey).IsRequired().HasMaxLength(64);
        builder.Property(m => m.RedactionMode).IsRequired().HasMaxLength(32);
        builder.Property(m => m.ContentType).IsRequired().HasMaxLength(255);
        builder.Property(m => m.CreatedAt).HasColumnType("timestamp with time zone");
        builder.Property(m => m.UpdatedAt).HasColumnType("timestamp with time zone");

        // Cache lookup key: one cached rendition per image/source/profile/mode/
        // block-size for a redacted (BlurFaces) variant.
        builder.HasIndex(m => new { m.OwnerUserId, m.PlateImageId, m.SourceKind, m.ProfileKey, m.RedactionMode })
            .HasDatabaseName("ix_plate_redacted_media_lookup");
        builder.HasIndex(m => m.PlateImageId)
            .HasDatabaseName("ix_plate_redacted_media_image");
        // Blob reference lookups (refcount audit / janitor accounting).
        builder.HasIndex(m => m.BlobObjectId)
            .HasDatabaseName("ix_plate_redacted_media_blob_object");

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(m => m.OwnerUserId)
            .OnDelete(DeleteBehavior.Restrict);

        // Cache rows die with their PlateImage. The blob REFERENCE is released
        // explicitly in the same transaction as the PlateImage delete
        // (PlateImageService.DeleteAsync) BEFORE the cascade removes the rows, so
        // a zero-ref derived blob becomes janitor-eligible and nothing leaks.
        builder.HasOne<PlateImage>()
            .WithMany()
            .HasForeignKey(m => m.PlateImageId)
            .OnDelete(DeleteBehavior.Cascade);

        // RESTRICT safety net on the derived blob, matching plate_images: the
        // janitor can never delete bytes a cache row still references.
        builder.HasOne<BlobObject>()
            .WithMany()
            .HasForeignKey(m => m.BlobObjectId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
