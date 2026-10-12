using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NubArca.Api.Domain;

namespace NubArca.Api.Data.Configurations;

public class ResumableUploadConfiguration : IEntityTypeConfiguration<ResumableUpload>
{
    public void Configure(EntityTypeBuilder<ResumableUpload> builder)
    {
        builder.ToTable("resumable_uploads");

        builder.HasKey(u => u.Id);
        builder.Property(u => u.Id).ValueGeneratedNever();

        builder.Property(u => u.FileName).IsRequired().HasMaxLength(255);
        builder.Property(u => u.ContentType).HasMaxLength(255);
        builder.Property(u => u.Status).IsRequired().HasMaxLength(20);

        builder.Property(u => u.CreatedAt).HasColumnType("timestamp with time zone");
        builder.Property(u => u.UpdatedAt).HasColumnType("timestamp with time zone");
        builder.Property(u => u.ExpiresAt).HasColumnType("timestamp with time zone");
        builder.Property(u => u.CompletedAt).HasColumnType("timestamp with time zone");

        // Temporary bookkeeping of one user's upload: it goes with the user.
        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(u => u.OwnerUserId)
            .OnDelete(DeleteBehavior.Cascade);

        // The per-owner active-upload ceiling counts by owner and status.
        builder.HasIndex(u => new { u.OwnerUserId, u.Status })
            .HasDatabaseName("ix_resumable_uploads_owner_status");
        // The sweeper scans by expiry.
        builder.HasIndex(u => u.ExpiresAt)
            .HasDatabaseName("ix_resumable_uploads_expires");
    }
}

public class ResumableUploadChunkConfiguration : IEntityTypeConfiguration<ResumableUploadChunk>
{
    public void Configure(EntityTypeBuilder<ResumableUploadChunk> builder)
    {
        builder.ToTable("resumable_upload_chunks");

        // The composite key is both the lookup and the idempotency guard for a
        // chunk sent twice.
        builder.HasKey(c => new { c.UploadId, c.ChunkIndex });

        builder.Property(c => c.ReceivedAt).HasColumnType("timestamp with time zone");

        builder.HasOne<ResumableUpload>()
            .WithMany()
            .HasForeignKey(c => c.UploadId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
