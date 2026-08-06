using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NubArca.Api.Domain;

namespace NubArca.Api.Data.Configurations;

public class AestheticUploadSessionConfiguration : IEntityTypeConfiguration<AestheticUploadSession>
{
    public void Configure(EntityTypeBuilder<AestheticUploadSession> builder)
    {
        builder.ToTable("aesthetic_upload_sessions");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedNever();

        // Only the token HASH is stored; unique so a resolve is a single indexed
        // hash lookup (mirrors the Party upload-token hash).
        builder.Property(p => p.TokenHash).IsRequired().HasMaxLength(128);
        builder.HasIndex(p => p.TokenHash)
            .HasDatabaseName("ux_aesthetic_upload_sessions_token_hash")
            .IsUnique();

        builder.Property(p => p.CreatedAt).HasColumnType("timestamp with time zone");
        builder.Property(p => p.ExpiresAt).HasColumnType("timestamp with time zone");
        builder.Property(p => p.RevokedAt).HasColumnType("timestamp with time zone");

        // Owner-scoped reads (the TV lists/reads only its owner's sessions).
        builder.HasIndex(p => new { p.OwnerUserId, p.CreatedAt })
            .HasDatabaseName("ix_aesthetic_upload_sessions_owner_created");

        // Cleanup sweeper: reclaim expired/revoked rows by ExpiresAt.
        builder.HasIndex(p => p.ExpiresAt)
            .HasDatabaseName("ix_aesthetic_upload_sessions_expires");

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(p => p.OwnerUserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
