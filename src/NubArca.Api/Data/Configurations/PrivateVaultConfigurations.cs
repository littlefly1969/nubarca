using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NubArca.Api.Domain;

namespace NubArca.Api.Data.Configurations;

public class PrivateVaultConfiguration : IEntityTypeConfiguration<PrivateVault>
{
    public void Configure(EntityTypeBuilder<PrivateVault> builder)
    {
        builder.ToTable("private_vaults");
        builder.HasKey(v => v.Id);
        builder.Property(v => v.Id).ValueGeneratedNever();

        builder.Property(v => v.DisplayName).IsRequired().HasMaxLength(100);
        builder.Property(v => v.PasswordHash).IsRequired().HasMaxLength(512);
        builder.Property(v => v.EncryptionMode).IsRequired().HasMaxLength(40);
        // Reserved for future encryption readiness; no length cap needed.

        builder.Property(v => v.CreatedAt).HasColumnType("timestamp with time zone");
        builder.Property(v => v.UpdatedAt).HasColumnType("timestamp with time zone");

        // At most one vault per owner in v0.
        builder.HasIndex(v => v.OwnerUserId)
            .IsUnique()
            .HasDatabaseName("ux_private_vaults_owner");

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(v => v.OwnerUserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public class PrivateVaultAccessTokenConfiguration : IEntityTypeConfiguration<PrivateVaultAccessToken>
{
    public void Configure(EntityTypeBuilder<PrivateVaultAccessToken> builder)
    {
        builder.ToTable("private_vault_access_tokens");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).ValueGeneratedNever();

        builder.Property(t => t.TokenHash)
            .IsRequired()
            .HasMaxLength(64)
            .IsFixedLength();

        builder.Property(t => t.CreatedAt).HasColumnType("timestamp with time zone");
        builder.Property(t => t.ExpiresAt).HasColumnType("timestamp with time zone");
        builder.Property(t => t.RevokedAt).HasColumnType("timestamp with time zone");

        builder.HasIndex(t => t.TokenHash)
            .IsUnique()
            .HasDatabaseName("ux_private_vault_access_tokens_token_hash");

        // Cleanup / owner-scoped lookups.
        builder.HasIndex(t => new { t.OwnerUserId, t.ExpiresAt })
            .HasDatabaseName("ix_private_vault_access_tokens_owner_expires");

        builder.HasOne<PrivateVault>()
            .WithMany()
            .HasForeignKey(t => t.PrivateVaultId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(t => t.OwnerUserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
