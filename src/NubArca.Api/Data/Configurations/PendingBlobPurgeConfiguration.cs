using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NubArca.Api.Domain;

namespace NubArca.Api.Data.Configurations;

public class PendingBlobPurgeConfiguration : IEntityTypeConfiguration<PendingBlobPurge>
{
    public void Configure(EntityTypeBuilder<PendingBlobPurge> builder)
    {
        builder.ToTable("pending_blob_purges");

        // Keyed by the blob id, NOT a surrogate: the row is created in the same
        // transaction that deletes that blob, so one per blob is the invariant.
        builder.HasKey(p => p.BlobObjectId);

        builder.Property(p => p.BlobObjectId)
            .ValueGeneratedNever();

        // No FK to blob_objects on purpose — this row outlives that row by
        // design, and a FK would make the intended ordering impossible.
        builder.Property(p => p.StorageKey)
            .IsRequired()
            .HasMaxLength(512);

        builder.Property(p => p.Sha256)
            .IsRequired()
            .HasMaxLength(64)
            .IsFixedLength();

        builder.Property(p => p.CreatedAt)
            .HasColumnType("timestamp with time zone");

        builder.Property(p => p.AttemptCount)
            .IsRequired();

        builder.Property(p => p.LastAttemptAt)
            .HasColumnType("timestamp with time zone");

        // The janitor drains oldest-first so a persistently failing key can
        // never starve the ones behind it.
        builder.HasIndex(p => p.CreatedAt)
            .HasDatabaseName("ix_pending_blob_purges_created_at");
    }
}
