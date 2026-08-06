using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NubArca.Api.Domain;

namespace NubArca.Api.Data.Configurations;

public class PartyUploadItemConfiguration : IEntityTypeConfiguration<PartyUploadItem>
{
    public void Configure(EntityTypeBuilder<PartyUploadItem> builder)
    {
        builder.ToTable("party_upload_items");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedNever();

        // Short status token ("approved"/"pending"/"hidden"/"rejected"/
        // "removed_from_album"). Generous bound so a future status can never
        // overflow the column (see the widened admin-import status columns lesson).
        builder.Property(p => p.Status).IsRequired().HasMaxLength(32);
        builder.Property(p => p.UploadedAt).HasColumnType("timestamp with time zone");
        builder.Property(p => p.ModeratedAt).HasColumnType("timestamp with time zone");

        // One moderation record per uploaded file.
        builder.HasIndex(p => p.FileItemId)
            .IsUnique()
            .HasDatabaseName("ux_party_upload_items_file_item");

        // Owner + album + status is the owner moderation listing / visibility path.
        builder.HasIndex(p => new { p.OwnerUserId, p.AlbumId, p.Status })
            .HasDatabaseName("ix_party_upload_items_owner_album_status");

        // If the file is hard-deleted, drop its moderation row with it.
        builder.HasOne<FileItem>()
            .WithMany()
            .HasForeignKey(p => p.FileItemId)
            .OnDelete(DeleteBehavior.Cascade);

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
