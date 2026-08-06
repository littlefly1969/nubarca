using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NubArca.Api.Domain;

namespace NubArca.Api.Data.Configurations;

public class PlateImageConfiguration : IEntityTypeConfiguration<PlateImage>
{
    public void Configure(EntityTypeBuilder<PlateImage> builder)
    {
        builder.ToTable("plate_images");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedNever();

        builder.Property(p => p.OriginalFileName).IsRequired().HasMaxLength(512);
        builder.Property(p => p.ContentType).IsRequired().HasMaxLength(255);
        builder.Property(p => p.LogicalContainerKey).IsRequired().HasMaxLength(128);
        builder.Property(p => p.Status).IsRequired().HasMaxLength(32);
        builder.Property(p => p.CreatedAt).HasColumnType("timestamp with time zone");
        builder.Property(p => p.UpdatedAt).HasColumnType("timestamp with time zone");

        // Listing: an owner's plates newest-first.
        builder.HasIndex(p => new { p.OwnerUserId, p.CreatedAt })
            .HasDatabaseName("ix_plate_images_owner_created");

        // Future status filtering (analysis pipeline).
        builder.HasIndex(p => new { p.OwnerUserId, p.Status })
            .HasDatabaseName("ix_plate_images_owner_status");

        // Blob reference lookups (refcount audit / janitor accounting).
        builder.HasIndex(p => p.BlobObjectId)
            .HasDatabaseName("ix_plate_images_blob_object");

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(p => p.OwnerUserId)
            .OnDelete(DeleteBehavior.Restrict);

        // RESTRICT safety net: the blob janitor can never delete a BlobObject
        // while a PlateImage still references it. The reference is released
        // (refcount--) atomically with the row's hard delete, after which the
        // janitor reclaims a zero-ref blob under its normal grace rules.
        builder.HasOne<BlobObject>()
            .WithMany()
            .HasForeignKey(p => p.BlobObjectId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
