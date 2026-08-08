using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NubArca.Api.Domain;

namespace NubArca.Api.Data.Configurations;

public class CastMediaGrantConfiguration : IEntityTypeConfiguration<CastMediaGrant>
{
    public void Configure(EntityTypeBuilder<CastMediaGrant> builder)
    {
        builder.ToTable("cast_media_grants");
        builder.HasKey(g => g.Id);
        builder.Property(g => g.Id).ValueGeneratedNever();

        builder.Property(g => g.TokenHash)
            .IsRequired()
            .HasMaxLength(64)
            .IsFixedLength();

        builder.Property(g => g.CreatedAt).HasColumnType("timestamp with time zone");
        builder.Property(g => g.ExpiresAt).HasColumnType("timestamp with time zone");
        builder.Property(g => g.RevokedAt).HasColumnType("timestamp with time zone");

        // Unique so two grants can never share a digest. The lookup itself is by
        // primary key — a token on its own addresses nothing — but a duplicate
        // digest would still be a defect worth making impossible.
        builder.HasIndex(g => g.TokenHash)
            .IsUnique()
            .HasDatabaseName("ux_cast_media_grants_token_hash");

        // Owner-scoped listing and the opportunistic expiry sweep.
        builder.HasIndex(g => new { g.UserId, g.ExpiresAt })
            .HasDatabaseName("ix_cast_media_grants_user_expires");
        builder.HasIndex(g => g.ExpiresAt)
            .HasDatabaseName("ix_cast_media_grants_expires");

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(g => g.UserId)
            .OnDelete(DeleteBehavior.Restrict);

        // A grant is meaningless without its file, and a purge must not be
        // blocked by a delegated capability that can no longer resolve to
        // anything. Cascade, matching the other per-file derived rows.
        builder.HasOne<FileItem>()
            .WithMany()
            .HasForeignKey(g => g.FileItemId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
