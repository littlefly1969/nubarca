using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NubArca.Api.Domain;

namespace NubArca.Api.Data.Configurations;

public class RemoteUploadSessionConfiguration : IEntityTypeConfiguration<RemoteUploadSession>
{
    public void Configure(EntityTypeBuilder<RemoteUploadSession> builder)
    {
        builder.ToTable("remote_upload_sessions");

        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();

        builder.Property(s => s.Name).IsRequired().HasMaxLength(200);
        builder.Property(s => s.Status).IsRequired().HasMaxLength(20);
        builder.Property(s => s.StagingRelativeRoot).IsRequired().HasMaxLength(64);
        builder.Property(s => s.LastErrorCode).HasMaxLength(40);
        builder.Property(s => s.LastErrorMessage).HasMaxLength(300);

        builder.Property(s => s.CreatedAt).HasColumnType("timestamp with time zone");
        builder.Property(s => s.UpdatedAt).HasColumnType("timestamp with time zone");
        builder.Property(s => s.CompletedAt).HasColumnType("timestamp with time zone");
        builder.Property(s => s.ExpiresAt).HasColumnType("timestamp with time zone");

        // Owner-scoped session list ("my recent sessions").
        builder.HasIndex(s => new { s.CreatedByUserId, s.CreatedAt })
            .HasDatabaseName("ix_remote_upload_sessions_owner_created");
        builder.HasIndex(s => s.Status)
            .HasDatabaseName("ix_remote_upload_sessions_status");
        // The cleanup sweeper scans by expiry.
        builder.HasIndex(s => s.ExpiresAt)
            .HasDatabaseName("ix_remote_upload_sessions_expires");
    }
}

public class RemoteUploadItemConfiguration : IEntityTypeConfiguration<RemoteUploadItem>
{
    public void Configure(EntityTypeBuilder<RemoteUploadItem> builder)
    {
        builder.ToTable("remote_upload_items");

        builder.HasKey(i => i.Id);
        builder.Property(i => i.Id).ValueGeneratedNever();

        builder.Property(i => i.RelativePath).IsRequired().HasMaxLength(2048);
        builder.Property(i => i.Status).IsRequired().HasMaxLength(20);
        builder.Property(i => i.FailureCode).HasMaxLength(40);
        builder.Property(i => i.FailureMessage).HasMaxLength(300);

        builder.Property(i => i.CreatedAt).HasColumnType("timestamp with time zone");
        builder.Property(i => i.UpdatedAt).HasColumnType("timestamp with time zone");
        builder.Property(i => i.LastModifiedAt).HasColumnType("timestamp with time zone");

        builder.HasOne<RemoteUploadSession>()
            .WithMany()
            .HasForeignKey(i => i.SessionId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(i => new { i.SessionId, i.Status })
            .HasDatabaseName("ix_remote_upload_items_session_status");
        // Manifest-order keyset (paths can be too long to index safely).
        builder.HasIndex(i => new { i.SessionId, i.Ordinal })
            .IsUnique()
            .HasDatabaseName("ux_remote_upload_items_session_ordinal");
    }
}

public class RemoteUploadChunkConfiguration : IEntityTypeConfiguration<RemoteUploadChunk>
{
    public void Configure(EntityTypeBuilder<RemoteUploadChunk> builder)
    {
        builder.ToTable("remote_upload_chunks");

        // The composite key doubles as the chunk-by-item-and-index index and
        // the idempotency guard for re-uploaded chunks.
        builder.HasKey(c => new { c.ItemId, c.ChunkIndex });

        builder.Property(c => c.ReceivedAt).HasColumnType("timestamp with time zone");

        builder.HasOne<RemoteUploadItem>()
            .WithMany()
            .HasForeignKey(c => c.ItemId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
