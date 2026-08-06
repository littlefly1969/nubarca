using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NubArca.Api.Domain;

namespace NubArca.Api.Data.Configurations;

public class BlobObjectConfiguration : IEntityTypeConfiguration<BlobObject>
{
    public void Configure(EntityTypeBuilder<BlobObject> builder)
    {
        builder.ToTable("blob_objects", t =>
        {
            t.HasCheckConstraint(
                "ck_blob_objects_reference_count_non_negative",
                "\"ReferenceCount\" >= 0");
            t.HasCheckConstraint(
                "ck_blob_objects_size_bytes_non_negative",
                "\"SizeBytes\" >= 0");
        });

        builder.HasKey(b => b.Id);

        builder.Property(b => b.Id)
            .ValueGeneratedNever();

        builder.Property(b => b.Sha256)
            .IsRequired()
            .HasMaxLength(64)
            .IsFixedLength();

        builder.Property(b => b.SizeBytes)
            .IsRequired();

        builder.Property(b => b.StorageKey)
            .IsRequired()
            .HasMaxLength(512);

        builder.Property(b => b.ReferenceCount)
            .IsRequired();

        builder.Property(b => b.CreatedAt)
            .HasColumnType("timestamp with time zone");

        builder.Property(b => b.PurgeEligibleAt)
            .HasColumnType("timestamp with time zone");

        builder.HasIndex(b => b.Sha256)
            .IsUnique()
            .HasDatabaseName("ux_blob_objects_sha256");

        builder.HasIndex(b => b.PurgeEligibleAt)
            .HasFilter("\"ReferenceCount\" = 0 AND \"PurgeEligibleAt\" IS NOT NULL")
            .HasDatabaseName("ix_blob_objects_purge_eligible_at");
    }
}
