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
        builder.ToTable("tv_sessions");
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

        builder.HasIndex(x => x.SessionTokenHash).IsUnique()
            .HasDatabaseName("ux_tv_sessions_token_hash");
        builder.HasIndex(x => new { x.OwnerUserId, x.ExpiresAt })
            .HasDatabaseName("ix_tv_sessions_owner_expires");
        builder.HasOne<User>().WithMany().HasForeignKey(x => x.OwnerUserId)
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
