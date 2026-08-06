using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NubArca.Api.Domain;

namespace NubArca.Api.Data.Configurations;

public class PhotoExportSessionConfiguration : IEntityTypeConfiguration<PhotoExportSession>
{
    public void Configure(EntityTypeBuilder<PhotoExportSession> builder)
    {
        builder.ToTable("photo_export_sessions");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();

        builder.Property(s => s.TokenHash)
            .IsRequired()
            .HasMaxLength(64)
            .IsFixedLength();

        builder.Property(s => s.Status).IsRequired().HasMaxLength(20);
        builder.Property(s => s.ErrorSummary).HasMaxLength(500);

        builder.Property(s => s.CreatedAt).HasColumnType("timestamp with time zone");
        builder.Property(s => s.StartedAt).HasColumnType("timestamp with time zone");
        builder.Property(s => s.CompletedAt).HasColumnType("timestamp with time zone");
        builder.Property(s => s.ExpiresAt).HasColumnType("timestamp with time zone");
        builder.Property(s => s.RevokedAt).HasColumnType("timestamp with time zone");
        builder.Property(s => s.UpdatedAt).HasColumnType("timestamp with time zone");

        builder.HasIndex(s => s.TokenHash)
            .IsUnique()
            .HasDatabaseName("ux_photo_export_sessions_token_hash");
        builder.HasIndex(s => new { s.OwnerUserId, s.CreatedAt })
            .HasDatabaseName("ix_photo_export_sessions_owner_created");

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(s => s.OwnerUserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public class PhotoExportEntryConfiguration : IEntityTypeConfiguration<PhotoExportEntry>
{
    public void Configure(EntityTypeBuilder<PhotoExportEntry> builder)
    {
        builder.ToTable("photo_export_entries");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedNever();

        builder.Property(e => e.RelativePath).IsRequired().HasMaxLength(4096);
        builder.Property(e => e.Name).IsRequired().HasMaxLength(1024);
        builder.Property(e => e.ContentType).HasMaxLength(255);
        builder.Property(e => e.LastModified).HasColumnType("timestamp with time zone");

        // Keyset manifest paging is ordered by (SessionId, Id).
        builder.HasIndex(e => new { e.SessionId, e.Id })
            .HasDatabaseName("ix_photo_export_entries_session_id");

        builder.HasOne<PhotoExportSession>()
            .WithMany()
            .HasForeignKey(e => e.SessionId)
            .OnDelete(DeleteBehavior.Cascade);

        // FileItemId is an internal reference; RESTRICT so a snapshot row can
        // never dangle while the export session is live.
        builder.HasOne<FileItem>()
            .WithMany()
            .HasForeignKey(e => e.FileItemId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
