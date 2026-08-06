using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NubArca.Api.Domain;

namespace NubArca.Api.Data.Configurations;

public class AlbumItemConfiguration : IEntityTypeConfiguration<AlbumItem>
{
    public void Configure(EntityTypeBuilder<AlbumItem> builder)
    {
        builder.ToTable("album_items");
        builder.HasKey(ai => new { ai.AlbumId, ai.FileItemId });

        // SHARE-ALBUM-03: stable surrogate identity for the membership row, used
        // by the reorder contract. An ALTERNATE key — the composite primary key
        // above still enforces one row per file per album.
        builder.Property(ai => ai.Id).ValueGeneratedNever();
        builder.HasAlternateKey(ai => ai.Id);
        builder.Property(ai => ai.AddedAt).HasColumnType("timestamp with time zone");

        // Reverse lookup "which albums is this file in" — the index behind the
        // albumMembership=assigned/unassigned EXISTS / NOT EXISTS predicates on
        // the photo and video galleries. Declared explicitly because a gallery
        // filter now depends on it; EF already materialises the same index from
        // the FileItemId foreign key below (IX_album_items_FileItemId, created
        // by the AddAlbums migration), so this is documentation, not a schema
        // change — it produces no new migration.
        builder.HasIndex(ai => ai.FileItemId);

        // SHARE-ALBUM-02 provenance. "Which items did this user contribute" is
        // the predicate behind withdraw-my-contribution and behind the automatic
        // withdrawal that a membership revocation performs, so it is indexed
        // per album rather than globally.
        builder.HasIndex(ai => new { ai.AlbumId, ai.AddedByUserId })
            .HasDatabaseName("ix_album_items_album_added_by");

        // SHARE-ALBUM-03: the album's curated order. Indexed with the album so
        // every listing reads it in order without a sort.
        builder.HasIndex(ai => new { ai.AlbumId, ai.SortOrder })
            .HasDatabaseName("ix_album_items_album_sort_order");

        // FK Restrict, like every other user reference here: a user row cannot
        // vanish while it still explains why an album item exists.
        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(ai => ai.AddedByUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Album>()
            .WithMany()
            .HasForeignKey(ai => ai.AlbumId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<FileItem>()
            .WithMany()
            .HasForeignKey(ai => ai.FileItemId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
