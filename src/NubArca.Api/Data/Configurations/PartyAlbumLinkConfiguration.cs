using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NubArca.Api.Domain;

namespace NubArca.Api.Data.Configurations;

public class PartyAlbumLinkConfiguration : IEntityTypeConfiguration<PartyAlbumLink>
{
    public void Configure(EntityTypeBuilder<PartyAlbumLink> builder)
    {
        builder.ToTable("party_album_links");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedNever();

        // SHA-256 hex → 64 chars. Never the raw token.
        builder.Property(p => p.TokenHash).IsRequired().HasMaxLength(64);
        builder.Property(p => p.UploadTokenHash).HasMaxLength(64);
        builder.Property(p => p.Enabled).HasDefaultValue(false);
        builder.Property(p => p.UploadEnabled).HasDefaultValue(false);
        builder.Property(p => p.RequireUploadApproval).HasDefaultValue(false);
        builder.Property(p => p.CreatedAt).HasColumnType("timestamp with time zone");
        builder.Property(p => p.UpdatedAt).HasColumnType("timestamp with time zone");
        builder.Property(p => p.RevokedAt).HasColumnType("timestamp with time zone");
        builder.Property(p => p.ExpiresAt).HasColumnType("timestamp with time zone");

        // Public token lookup (validation) is by hash.
        builder.HasIndex(p => p.TokenHash)
            .IsUnique()
            .HasDatabaseName("ux_party_album_links_token_hash");
        // Upload token lookup. Unique; NULLs are distinct (Postgres + SQLite) so
        // view-only links (no upload token) don't collide.
        builder.HasIndex(p => p.UploadTokenHash)
            .IsUnique()
            .HasDatabaseName("ux_party_album_links_upload_token_hash");

        // Fast owner-scoped + album-scoped status lookups.
        builder.HasIndex(p => new { p.OwnerUserId, p.AlbumId })
            .HasDatabaseName("ix_party_album_links_owner_album");
        builder.HasIndex(p => p.AlbumId)
            .HasDatabaseName("ix_party_album_links_album");

        builder.HasOne<Album>()
            .WithMany()
            .HasForeignKey(p => p.AlbumId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(p => p.OwnerUserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
