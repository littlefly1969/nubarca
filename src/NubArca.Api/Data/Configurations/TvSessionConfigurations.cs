using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NubArca.Api.Domain;

namespace NubArca.Api.Data.Configurations;

public sealed class TvPairingRequestConfiguration : IEntityTypeConfiguration<TvPairingRequest>
{
    public void Configure(EntityTypeBuilder<TvPairingRequest> builder)
    {
        builder.ToTable("tv_pairing_requests");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.PublicCode).IsRequired().HasMaxLength(8).IsFixedLength();
        builder.Property(x => x.SecretHash).IsRequired().HasMaxLength(64).IsFixedLength();
        builder.Property(x => x.Status).IsRequired().HasMaxLength(20);
        builder.Property(x => x.CreatedAt).HasColumnType("timestamp with time zone");
        builder.Property(x => x.ExpiresAt).HasColumnType("timestamp with time zone");
        builder.Property(x => x.ApprovedAt).HasColumnType("timestamp with time zone");

        builder.HasIndex(x => x.PublicCode).IsUnique()
            .HasDatabaseName("ux_tv_pairing_requests_public_code");
        builder.HasIndex(x => x.ExpiresAt)
            .HasDatabaseName("ix_tv_pairing_requests_expires_at");

        builder.HasOne<User>().WithMany().HasForeignKey(x => x.ApprovedByUserId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<TvSession>().WithMany().HasForeignKey(x => x.TvSessionId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}

public sealed class TvSessionConfiguration : IEntityTypeConfiguration<TvSession>
{
    public void Configure(EntityTypeBuilder<TvSession> builder)
    {
        builder.ToTable("tv_sessions", t =>
            // BOTH halves of the assignment invariant, as a database fact rather
            // than as a rule each write path remembers: a party assignment always
            // names a link, and a general one never does. Without the second half
            // a television switched back to general would keep pointing at the
            // party it used to show.
            t.HasCheckConstraint("ck_tv_sessions_display_assignment",
                "(\"DisplayAssignment\" = 'general' AND \"AssignedPartyAlbumLinkId\" IS NULL)"
                + " OR (\"DisplayAssignment\" = 'party' AND \"AssignedPartyAlbumLinkId\" IS NOT NULL)"));
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.SessionTokenHash).IsRequired().HasMaxLength(64).IsFixedLength();
        builder.Property(x => x.DeviceLabel).HasMaxLength(100);
        builder.Property(x => x.UserAgent).HasMaxLength(500);
        builder.Property(x => x.CreatedAt).HasColumnType("timestamp with time zone");
        builder.Property(x => x.LastSeenAt).HasColumnType("timestamp with time zone");
        builder.Property(x => x.ExpiresAt).HasColumnType("timestamp with time zone");
        builder.Property(x => x.RevokedAt).HasColumnType("timestamp with time zone");
        builder.Property(x => x.PersonalPinFailedAttempts).HasDefaultValue(0);
        builder.Property(x => x.PersonalPinLockedUntil).HasColumnType("timestamp with time zone");

        // The default is what makes the migration additive: every television
        // paired before assignments existed reads as `general`, which is exactly
        // the experience it already had.
        builder.Property(x => x.DisplayAssignment).IsRequired().HasMaxLength(20)
            .HasDefaultValue(TvDisplayAssignments.General);

        builder.HasIndex(x => x.SessionTokenHash).IsUnique()
            .HasDatabaseName("ux_tv_sessions_token_hash");
        builder.HasIndex(x => new { x.OwnerUserId, x.ExpiresAt })
            .HasDatabaseName("ix_tv_sessions_owner_expires");

        // "Which televisions are showing this party" is the question the party
        // side will ask, so it is an index rather than a scan of the fleet.
        builder.HasIndex(x => x.AssignedPartyAlbumLinkId)
            .HasDatabaseName("ix_tv_sessions_assigned_party_link");

        builder.HasOne<User>().WithMany().HasForeignKey(x => x.OwnerUserId)
            .OnDelete(DeleteBehavior.Restrict);

        // Restrict rather than SetNull: nulling the column behind the check
        // constraint would leave a row claiming to show a party it cannot name.
        // Deleting an album returns its televisions to `general` explicitly, in
        // AlbumService, where the rest of that album's Party state is cleared.
        builder.HasOne<PartyAlbumLink>().WithMany()
            .HasForeignKey(x => x.AssignedPartyAlbumLinkId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class TvPersonalPinConfiguration : IEntityTypeConfiguration<TvPersonalPin>
{
    public void Configure(EntityTypeBuilder<TvPersonalPin> builder)
    {
        builder.ToTable("tv_personal_pins");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.PinHash).IsRequired().HasMaxLength(512);
        // Rows created before the directional code existed are numeric PINs; the
        // default makes that true for every backfilled row without a data script.
        builder.Property(x => x.Scheme).IsRequired().HasMaxLength(20)
            .HasDefaultValue(TvPersonalSecretSchemes.LegacyPin);
        builder.Property(x => x.Generation).HasDefaultValue(1);
        builder.Property(x => x.CreatedAt).HasColumnType("timestamp with time zone");
        builder.Property(x => x.UpdatedAt).HasColumnType("timestamp with time zone");

        // Exactly one Personal Area PIN per owner.
        builder.HasIndex(x => x.OwnerUserId).IsUnique()
            .HasDatabaseName("ux_tv_personal_pins_owner");
        builder.HasOne<User>().WithMany().HasForeignKey(x => x.OwnerUserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class TvPersonalUnlockGrantConfiguration
    : IEntityTypeConfiguration<TvPersonalUnlockGrant>
{
    public void Configure(EntityTypeBuilder<TvPersonalUnlockGrant> builder)
    {
        builder.ToTable("tv_personal_unlock_grants");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.TokenHash).IsRequired().HasMaxLength(64).IsFixedLength();
        builder.Property(x => x.CreatedAt).HasColumnType("timestamp with time zone");
        builder.Property(x => x.ExpiresAt).HasColumnType("timestamp with time zone");
        builder.Property(x => x.RevokedAt).HasColumnType("timestamp with time zone");

        builder.HasIndex(x => x.TokenHash).IsUnique()
            .HasDatabaseName("ux_tv_personal_unlock_grants_token_hash");
        builder.HasIndex(x => x.TvSessionId)
            .HasDatabaseName("ix_tv_personal_unlock_grants_session");

        // Grants die with their TV session row; the owner FK stays Restrict
        // (users are never hard-deleted while dependent rows exist).
        builder.HasOne<TvSession>().WithMany().HasForeignKey(x => x.TvSessionId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<User>().WithMany().HasForeignKey(x => x.OwnerUserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

/// <summary>
/// A television's permission to show one party. Mirrors
/// <see cref="TvPersonalUnlockGrantConfiguration"/>, because it is the same
/// kind of thing: a short-lived, hash-stored, device-bound capability.
/// </summary>
public sealed class PartyDisplayGrantConfiguration : IEntityTypeConfiguration<PartyDisplayGrant>
{
    public void Configure(EntityTypeBuilder<PartyDisplayGrant> builder)
    {
        builder.ToTable("party_display_grants");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.TokenHash).IsRequired().HasMaxLength(64).IsFixedLength();
        builder.Property(x => x.CreatedAt).HasColumnType("timestamp with time zone");
        builder.Property(x => x.ExpiresAt).HasColumnType("timestamp with time zone");
        builder.Property(x => x.RevokedAt).HasColumnType("timestamp with time zone");

        // The lookup every request makes, and the reason a stolen database is
        // not a stolen display: only the digest is here.
        builder.HasIndex(x => x.TokenHash).IsUnique()
            .HasDatabaseName("ux_party_display_grants_token_hash");
        builder.HasIndex(x => x.TvSessionId)
            .HasDatabaseName("ix_party_display_grants_session");
        // "Which grants point at this party" — asked when a party ends.
        builder.HasIndex(x => x.PartyAlbumLinkId)
            .HasDatabaseName("ix_party_display_grants_link");
        builder.HasIndex(x => x.ExpiresAt)
            .HasDatabaseName("ix_party_display_grants_expires_at");

        // Grants die with their television, exactly as unlock grants do.
        builder.HasOne<TvSession>().WithMany().HasForeignKey(x => x.TvSessionId)
            .OnDelete(DeleteBehavior.Cascade);
        // Restricting, not cascading — and that is a deliberate cost. A cascade
        // would make grants vanish silently with their link, which reads as the
        // safe choice right up to the moment a new teardown path forgets they
        // exist. Restrict makes the database refuse instead, so PartyStateEraser
        // has to name this table. The refusal IS bug #116's failure mode, which
        // is why the eraser deletes these before the link rather than after.
        builder.HasOne<PartyAlbumLink>().WithMany().HasForeignKey(x => x.PartyAlbumLinkId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
