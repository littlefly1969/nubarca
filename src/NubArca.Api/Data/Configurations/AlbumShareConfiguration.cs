using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NubArca.Api.Domain;

namespace NubArca.Api.Data.Configurations;

public sealed class AlbumShareLinkConfiguration : IEntityTypeConfiguration<AlbumShareLink>
{
    public void Configure(EntityTypeBuilder<AlbumShareLink> b)
    {
        b.ToTable("album_share_links", t =>
        {
            t.HasCheckConstraint("ck_album_share_links_max_uploads",
                "\"MaxUploads\" >= 0 AND \"MaxUploads\" <= 100000");
            t.HasCheckConstraint("ck_album_share_links_upload_count", "\"UploadCount\" >= 0");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        // Only the digest is stored, and it is what every public request is
        // matched against — so the lookup is on an index over the hash and
        // never over anything that could be guessed from it.
        b.Property(x => x.TokenHash).IsRequired().HasMaxLength(64);
        b.HasIndex(x => x.TokenHash).IsUnique().HasDatabaseName("ux_album_share_links_token");
        b.Property(x => x.Label).HasMaxLength(AlbumShareLimits.MaxLabelLength);
        b.Property(x => x.Enabled).HasDefaultValue(true);
        b.Property(x => x.UploadEnabled).HasDefaultValue(true);
        // The SAFE default, written into the schema and not only into the
        // constructor: an original carries GPS, and a public share must not.
        b.Property(x => x.AllowOriginalDownload).HasDefaultValue(false);
        b.Property(x => x.MaxUploads).HasDefaultValue(AlbumShareLimits.DefaultMaxUploads);
        b.Property(x => x.UploadCount).HasDefaultValue(0);
        b.Property(x => x.CreatedAt).HasColumnType("timestamp with time zone");
        b.Property(x => x.UpdatedAt).HasColumnType("timestamp with time zone");
        b.Property(x => x.RevokedAt).HasColumnType("timestamp with time zone");
        b.Property(x => x.ExpiresAt).HasColumnType("timestamp with time zone");
        // Deleting the album takes its shares with it: a capability over
        // something that no longer exists is not a capability.
        b.HasOne<Album>().WithMany().HasForeignKey(x => x.AlbumId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => new { x.AlbumId, x.Enabled }).HasDatabaseName("ix_album_share_links_album");
        // ONE LIVE LINK PER ALBUM, enforced where it cannot be raced.
        //
        // The service reads "is there an active one?" and mints if not, and two
        // requests arriving together both read "no" and both mint. A second
        // hidden capability over somebody's album is exactly the bug nobody
        // notices: revoking finds one row, and the other keeps opening. A
        // partial unique index makes the second insert fail instead, which the
        // service catches and turns into "use the one that won".
        b.HasIndex(x => x.AlbumId)
            .IsUnique()
            .HasFilter("\"Enabled\" AND \"RevokedAt\" IS NULL")
            .HasDatabaseName("ux_album_share_links_one_live");
    }
}

public sealed class AlbumShareGuestConfiguration : IEntityTypeConfiguration<AlbumShareGuest>
{
    public void Configure(EntityTypeBuilder<AlbumShareGuest> b)
    {
        b.ToTable("album_share_guests");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Email).IsRequired().HasMaxLength(AlbumShareLimits.MaxEmailLength);
        b.Property(x => x.DisplayName).HasMaxLength(AlbumShareLimits.MaxDisplayNameLength);
        b.Property(x => x.CreatedAt).HasColumnType("timestamp with time zone");
        b.Property(x => x.RevokedAt).HasColumnType("timestamp with time zone");
        b.HasOne<AlbumShareLink>().WithMany().HasForeignKey(x => x.AlbumShareLinkId)
            .OnDelete(DeleteBehavior.Cascade);
        // One row per address per share: re-adding somebody the owner removed
        // reuses their row rather than leaving two with different verdicts.
        b.HasIndex(x => new { x.AlbumShareLinkId, x.Email }).IsUnique()
            .HasDatabaseName("ux_album_share_guests_email");
    }
}

public sealed class AlbumShareChallengeConfiguration : IEntityTypeConfiguration<AlbumShareChallenge>
{
    public void Configure(EntityTypeBuilder<AlbumShareChallenge> b)
    {
        b.ToTable("album_share_challenges", t =>
            t.HasCheckConstraint("ck_album_share_challenges_attempts", "\"Attempts\" >= 0"));
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        // 64 hex characters of HMAC-SHA256. Not a hash of the code: see the
        // entity's own note on why storing one would be a mistake.
        b.Property(x => x.OtpProof).IsRequired().HasMaxLength(64);
        b.Property(x => x.Generation).HasDefaultValue(1);
        b.Property(x => x.CreatedAt).HasColumnType("timestamp with time zone");
        b.Property(x => x.LastSentAt).HasColumnType("timestamp with time zone");
        b.Property(x => x.ExpiresAt).HasColumnType("timestamp with time zone");
        b.Property(x => x.VerifiedAt).HasColumnType("timestamp with time zone");
        b.HasOne<AlbumShareLink>().WithMany().HasForeignKey(x => x.AlbumShareLinkId)
            .OnDelete(DeleteBehavior.Cascade);
        b.HasOne<AlbumShareGuest>().WithMany().HasForeignKey(x => x.AlbumShareGuestId)
            .OnDelete(DeleteBehavior.Cascade);
        // One challenge in flight per address: asking again resends on the same
        // row with a new generation, which is what invalidates the old code.
        b.HasIndex(x => x.AlbumShareGuestId).IsUnique()
            .HasDatabaseName("ux_album_share_challenges_guest");
    }
}

public sealed class AlbumShareDeviceConfiguration : IEntityTypeConfiguration<AlbumShareDevice>
{
    public void Configure(EntityTypeBuilder<AlbumShareDevice> b)
    {
        b.ToTable("album_share_devices");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.TokenHash).IsRequired().HasMaxLength(64);
        b.HasIndex(x => x.TokenHash).IsUnique().HasDatabaseName("ux_album_share_devices_token");
        b.Property(x => x.UserAgent).HasMaxLength(AlbumShareLimits.MaxUserAgentLength);
        b.Property(x => x.CreatedAt).HasColumnType("timestamp with time zone");
        b.Property(x => x.LastSeenAt).HasColumnType("timestamp with time zone");
        b.Property(x => x.ExpiresAt).HasColumnType("timestamp with time zone");
        b.Property(x => x.RevokedAt).HasColumnType("timestamp with time zone");
        b.HasOne<AlbumShareLink>().WithMany().HasForeignKey(x => x.AlbumShareLinkId)
            .OnDelete(DeleteBehavior.Cascade);
        b.HasOne<AlbumShareGuest>().WithMany().HasForeignKey(x => x.AlbumShareGuestId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
