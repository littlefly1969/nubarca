using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NubArca.Api.Domain;

namespace NubArca.Api.Data.Configurations;

public class FileItemConfiguration : IEntityTypeConfiguration<FileItem>
{
    public void Configure(EntityTypeBuilder<FileItem> builder)
    {
        builder.ToTable("file_items", t =>
        {
            t.HasCheckConstraint(
                "ck_file_items_size_bytes_non_negative",
                "\"SizeBytes\" >= 0");
        });

        builder.HasKey(f => f.Id);

        builder.Property(f => f.Id)
            .ValueGeneratedNever();

        builder.Property(f => f.Name)
            .IsRequired()
            .HasMaxLength(255);

        builder.Property(f => f.MimeType)
            .IsRequired()
            .HasMaxLength(255);

        builder.Property(f => f.SizeBytes)
            .IsRequired();

        builder.Property(f => f.CreatedAt)
            .HasColumnType("timestamp with time zone");

        builder.Property(f => f.UpdatedAt)
            .HasColumnType("timestamp with time zone");

        builder.Property(f => f.DeletedAt)
            .HasColumnType("timestamp with time zone");

        builder.Property(f => f.EffectiveDateTaken)
            .HasColumnType("timestamp with time zone");

        builder.Property(f => f.EffectiveDateTakenSource)
            .HasMaxLength(16);

        // Slice 3 (media organization): per-file media-library membership.
        // Stored as int; default 0 (Active) so every existing row is Active.
        builder.Property(f => f.MediaLibraryState)
            .HasDefaultValue(MediaLibraryState.Active);

        builder.Property(f => f.MediaLibraryStateChangedAt)
            .HasColumnType("timestamp with time zone");

        builder.HasIndex(f => new { f.OwnerUserId, f.ParentFolderId, f.DeletedAt })
            .HasDatabaseName("ix_file_items_owner_parent_deleted");

        builder.HasIndex(f => new { f.OwnerUserId, f.Name })
            .HasDatabaseName("ix_file_items_owner_name");

        builder.HasIndex(f => f.BlobObjectId)
            .HasDatabaseName("ix_file_items_blob_object");

        // Slice 60: index the gallery's default sort (created desc) and the
        // size sort. Owner-scoped first so the planner can fast-prefix-seek
        // by owner; DeletedAt next so the active-only predicate prunes
        // soft-deleted rows; the sort key + Id tie-breaker last to support
        // ORDER BY + LIMIT without a separate sort step on large libraries.
        builder.HasIndex(f => new { f.OwnerUserId, f.DeletedAt, f.CreatedAt, f.Id })
            .HasDatabaseName("ix_file_items_owner_deleted_created_id");

        builder.HasIndex(f => new { f.OwnerUserId, f.DeletedAt, f.SizeBytes, f.Id })
            .HasDatabaseName("ix_file_items_owner_deleted_size_id");

        // Slice 88: index the gallery "Date taken" sort on the denormalized
        // EffectiveDateTaken column so ORDER BY + seek-pagination resolve via a
        // single ordered index scan (no correlated subqueries, no Sort step).
        //
        // This is a PARTIAL index filtered on "DeletedAt IS NULL" with columns
        // (OwnerUserId, EffectiveDateTaken, Id). The gallery always filters
        // active rows (DeletedAt IS NULL), so baking that into the index
        // predicate keeps DeletedAt OUT of the key — a non-partial
        // (OwnerUserId, DeletedAt, EffectiveDateTaken, Id) index can NOT provide
        // the ordering, because PostgreSQL won't treat a mid-key "IS NULL" as an
        // ordering-preserving equality (confirmed via EXPLAIN). With the partial
        // index, OwnerUserId is the equality prefix and (EffectiveDateTaken, Id)
        // give the exact sort order, so the planner does an ordered index scan.
        builder.HasIndex(f => new { f.OwnerUserId, f.EffectiveDateTaken, f.Id })
            .HasFilter("\"DeletedAt\" IS NULL")
            .HasDatabaseName("ix_file_items_owner_deleted_effdate_id");

        // Sibling-name uniqueness is scoped per Private Vault via TWO partial
        // unique indexes split on PrivateVaultId IS NULL / IS NOT NULL. This
        // matters for two reasons:
        //   * A normal file and a hidden vault file can share the same
        //     (parent, name) without colliding — so creating a normal file never
        //     conflicts with (and thus never reveals) a hidden vault file.
        //   * The NORMAL-scope index keeps PrivateVaultId OUT of the key, so it
        //     behaves EXACTLY as before for normal content — including on SQLite,
        //     whose unique indexes treat any NULL key column as distinct (which
        //     would silently disable the constraint if PrivateVaultId were in the
        //     key). Production Postgres uses NULLS NOT DISTINCT for both.
        builder.HasIndex(f => new { f.OwnerUserId, f.ParentFolderId, f.Name })
            .IsUnique()
            .AreNullsDistinct(false)
            .HasFilter("\"DeletedAt\" IS NULL AND \"PrivateVaultId\" IS NULL")
            .HasDatabaseName("ux_file_items_active_sibling_name");

        builder.HasIndex(f => new { f.OwnerUserId, f.PrivateVaultId, f.ParentFolderId, f.Name })
            .IsUnique()
            .AreNullsDistinct(false)
            .HasFilter("\"DeletedAt\" IS NULL AND \"PrivateVaultId\" IS NOT NULL")
            .HasDatabaseName("ux_file_items_active_vault_sibling_name");

        // Slice 3: the Excluded gallery tab seeks the (small) minority of files
        // an owner has moved out of the media library. A partial index on the
        // Excluded rows keeps that scan cheap without touching the hot Active
        // path — the Active galleries keep using their existing sort indexes and
        // merely add a cheap "MediaLibraryState = 0" residual filter (Active is
        // the majority, so almost nothing is discarded). Filtered to active
        // (non-deleted) rows since the excluded tab never lists trashed files.
        builder.HasIndex(f => new { f.OwnerUserId, f.MediaLibraryState, f.Id })
            .HasFilter("\"DeletedAt\" IS NULL")
            .HasDatabaseName("ix_file_items_owner_medialibrarystate_id");

        // Private Vault content lookup after unlock: owner + vault + parent.
        // Partial (active only) — vault browsing never lists soft-deleted rows.
        builder.HasIndex(f => new { f.OwnerUserId, f.PrivateVaultId, f.ParentFolderId })
            .HasFilter("\"DeletedAt\" IS NULL")
            .HasDatabaseName("ix_file_items_owner_vault_parent");

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(f => f.OwnerUserId)
            .OnDelete(DeleteBehavior.Restrict);

        // Optional FK to the owner's Private Vault. Restrict so a vault can never
        // be dropped while it still logically contains content.
        builder.HasOne<PrivateVault>()
            .WithMany()
            .HasForeignKey(f => f.PrivateVaultId)
            .OnDelete(DeleteBehavior.Restrict);

        // Exclusion-first: every normal query sees only non-vault content. The
        // few paths that MUST see vault rows — vault browse/move, and blob
        // refcount/lifecycle accounting (audit, sweeper, quota) — call
        // IgnoreQueryFilters() explicitly. Raw-SQL paths (pgvector) add the
        // predicate by hand. All existing data has PrivateVaultId == NULL, so
        // this filter is a no-op until content is moved into a vault.
        builder.HasQueryFilter(f => f.PrivateVaultId == null);

        builder.HasOne<Folder>()
            .WithMany()
            .HasForeignKey(f => f.ParentFolderId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<BlobObject>()
            .WithMany()
            .HasForeignKey(f => f.BlobObjectId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
