using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NubArca.Api.Domain;

namespace NubArca.Api.Data.Configurations;

public class FolderConfiguration : IEntityTypeConfiguration<Folder>
{
    public void Configure(EntityTypeBuilder<Folder> builder)
    {
        builder.ToTable("folders");

        builder.HasKey(f => f.Id);

        builder.Property(f => f.Id)
            .ValueGeneratedNever();

        builder.Property(f => f.Name)
            .IsRequired()
            .HasMaxLength(255);

        builder.Property(f => f.CreatedAt)
            .HasColumnType("timestamp with time zone");

        builder.Property(f => f.UpdatedAt)
            .HasColumnType("timestamp with time zone");

        builder.Property(f => f.DeletedAt)
            .HasColumnType("timestamp with time zone");

        builder.HasIndex(f => new { f.OwnerUserId, f.ParentFolderId, f.DeletedAt })
            .HasDatabaseName("ix_folders_owner_parent_deleted");

        // Sibling-name uniqueness scoped per Private Vault via two partial unique
        // indexes (see FileItemConfiguration for the full rationale).
        builder.HasIndex(f => new { f.OwnerUserId, f.ParentFolderId, f.Name })
            .IsUnique()
            .AreNullsDistinct(false)
            .HasFilter("\"DeletedAt\" IS NULL AND \"PrivateVaultId\" IS NULL")
            .HasDatabaseName("ux_folders_active_sibling_name");

        builder.HasIndex(f => new { f.OwnerUserId, f.PrivateVaultId, f.ParentFolderId, f.Name })
            .IsUnique()
            .AreNullsDistinct(false)
            .HasFilter("\"DeletedAt\" IS NULL AND \"PrivateVaultId\" IS NOT NULL")
            .HasDatabaseName("ux_folders_active_vault_sibling_name");

        // Private Vault folder lookup after unlock: owner + vault + parent.
        builder.HasIndex(f => new { f.OwnerUserId, f.PrivateVaultId, f.ParentFolderId })
            .HasFilter("\"DeletedAt\" IS NULL")
            .HasDatabaseName("ix_folders_owner_vault_parent");

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(f => f.OwnerUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Folder>()
            .WithMany()
            .HasForeignKey(f => f.ParentFolderId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<PrivateVault>()
            .WithMany()
            .HasForeignKey(f => f.PrivateVaultId)
            .OnDelete(DeleteBehavior.Restrict);

        // Exclusion-first global filter (see FileItemConfiguration for rationale).
        builder.HasQueryFilter(f => f.PrivateVaultId == null);
    }
}
