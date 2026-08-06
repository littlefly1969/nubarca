using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NubArca.Api.Domain;

namespace NubArca.Api.Data.Configurations;

public class AestheticLabItemConfiguration : IEntityTypeConfiguration<AestheticLabItem>
{
    public void Configure(EntityTypeBuilder<AestheticLabItem> builder)
    {
        builder.ToTable("aesthetic_lab_items");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedNever();

        builder.Property(p => p.OriginalFileName).IsRequired().HasMaxLength(512);
        builder.Property(p => p.ContentType).IsRequired().HasMaxLength(255);
        builder.Property(p => p.LogicalContainerKey).IsRequired().HasMaxLength(128);
        builder.Property(p => p.CreatedAt).HasColumnType("timestamp with time zone");
        builder.Property(p => p.UpdatedAt).HasColumnType("timestamp with time zone");

        // Listing: an owner's lab items, newest-first.
        builder.HasIndex(p => new { p.OwnerUserId, p.CreatedAt })
            .HasDatabaseName("ix_aesthetic_lab_items_owner_created");

        // Blob reference lookups (refcount audit / janitor accounting).
        builder.HasIndex(p => p.BlobObjectId)
            .HasDatabaseName("ix_aesthetic_lab_items_blob_object");

        // At most one lab item per owner/blob (idempotent add). Hard delete
        // removes the row, so a plain unique index is correct (a deleted item no
        // longer blocks re-adding).
        builder.HasIndex(p => new { p.OwnerUserId, p.BlobObjectId })
            .HasDatabaseName("ux_aesthetic_lab_items_owner_blob")
            .IsUnique();

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(p => p.OwnerUserId)
            .OnDelete(DeleteBehavior.Restrict);

        // RESTRICT: the blob janitor can never delete a BlobObject while a lab
        // item references it. The reference is released (refcount--) atomically
        // with the item's removal, after which the janitor reclaims a zero-ref
        // blob under its normal grace rules.
        builder.HasOne<BlobObject>()
            .WithMany()
            .HasForeignKey(p => p.BlobObjectId)
            .OnDelete(DeleteBehavior.Restrict);

        // SourceFileItemId is PROVENANCE ONLY: no FK to file_items, so deleting
        // the source gallery file never touches (or cascades to) the lab item.
        builder.Property(p => p.SourceFileItemId);
    }
}
