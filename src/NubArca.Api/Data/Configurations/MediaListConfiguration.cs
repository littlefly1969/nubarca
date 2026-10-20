using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NubArca.Api.Domain;

namespace NubArca.Api.Data.Configurations;

public class MediaListConfiguration : IEntityTypeConfiguration<MediaList>
{
    public void Configure(EntityTypeBuilder<MediaList> builder)
    {
        builder.ToTable("media_lists");
        builder.HasKey(l => l.Id);
        builder.Property(l => l.Id).ValueGeneratedNever();
        builder.Property(l => l.Name).HasMaxLength(120);
        builder.Property(l => l.Kind).HasConversion<int>();
        builder.Property(l => l.Version).HasDefaultValue(1);
        builder.Property(l => l.CreatedAt).HasColumnType("timestamp with time zone");
        builder.Property(l => l.UpdatedAt).HasColumnType("timestamp with time zone");

        builder.HasIndex(l => new { l.OwnerUserId, l.Kind })
            .HasDatabaseName("ix_media_lists_owner_kind");

        // One current selection per owner. A concurrent lazy creation from two
        // devices loses on this index and re-reads the winner.
        builder.HasIndex(l => l.OwnerUserId)
            .IsUnique()
            .HasFilter("\"Kind\" = 1")
            .HasDatabaseName("ux_media_lists_owner_selection");

        // One personal selection per member per shared album.
        builder.HasIndex(l => new { l.OwnerUserId, l.AlbumId })
            .IsUnique()
            .HasFilter("\"Kind\" = 2")
            .HasDatabaseName("ux_media_lists_owner_album_selection");

        // A deleted album takes its members' selections with it.
        builder.HasOne<Album>()
            .WithMany()
            .HasForeignKey(l => l.AlbumId)
            .OnDelete(DeleteBehavior.Cascade);

        // Cascade FROM the user only (users are never deleted today); nothing on
        // the users table gains a restriction.
        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(l => l.OwnerUserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public class MediaListItemConfiguration : IEntityTypeConfiguration<MediaListItem>
{
    public void Configure(EntityTypeBuilder<MediaListItem> builder)
    {
        builder.ToTable("media_list_items");
        builder.HasKey(i => new { i.ListId, i.FileItemId });
        builder.Property(i => i.AddedAt).HasColumnType("timestamp with time zone");

        // The list's own order, read without a sort.
        builder.HasIndex(i => new { i.ListId, i.Position })
            .HasDatabaseName("ix_media_list_items_list_position");
        // Reverse lookup for the cascade below.
        builder.HasIndex(i => i.FileItemId)
            .HasDatabaseName("ix_media_list_items_file");

        builder.HasOne<MediaList>()
            .WithMany()
            .HasForeignKey(i => i.ListId)
            .OnDelete(DeleteBehavior.Cascade);

        // CASCADE, unlike album_items: a list entry is a personal bookmark, not
        // history worth protecting, and a restriction here would make every
        // existing permanent-delete path (including the previous application's)
        // fail on a file that happens to be in somebody's list.
        builder.HasOne<FileItem>()
            .WithMany()
            .HasForeignKey(i => i.FileItemId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
