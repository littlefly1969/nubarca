using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NubArca.Api.Domain;

namespace NubArca.Api.Data.Configurations;

public class AestheticLabDerivativeConfiguration : IEntityTypeConfiguration<AestheticLabDerivative>
{
    public void Configure(EntityTypeBuilder<AestheticLabDerivative> builder)
    {
        builder.ToTable("aesthetic_lab_derivatives");
        builder.HasKey(d => d.Id);
        builder.Property(d => d.Id).ValueGeneratedNever();

        builder.Property(d => d.Size).IsRequired().HasMaxLength(16);
        builder.Property(d => d.ContentType).IsRequired().HasMaxLength(255);
        builder.Property(d => d.CreatedAt).HasColumnType("timestamp with time zone");

        // One cached derivative per (item, size).
        builder.HasIndex(d => new { d.AestheticLabItemId, d.Size })
            .HasDatabaseName("ux_aesthetic_lab_derivatives_item_size")
            .IsUnique();

        // Blob reference lookups (refcount audit / janitor accounting).
        builder.HasIndex(d => d.BlobObjectId)
            .HasDatabaseName("ix_aesthetic_lab_derivatives_blob_object");

        // The derivative row dies with its lab item. The derived blob REFERENCE
        // is released explicitly in the remove transaction BEFORE this cascade,
        // so the cascade never bypasses reference release.
        builder.HasOne<AestheticLabItem>()
            .WithMany()
            .HasForeignKey(d => d.AestheticLabItemId)
            .OnDelete(DeleteBehavior.Cascade);

        // RESTRICT against the blob: the janitor cannot delete a derived blob a
        // live derivative row still references.
        builder.HasOne<BlobObject>()
            .WithMany()
            .HasForeignKey(d => d.BlobObjectId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
