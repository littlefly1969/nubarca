using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NubArca.Api.Domain;

namespace NubArca.Api.Data.Configurations;

public class ShareLinkConfiguration : IEntityTypeConfiguration<ShareLink>
{
    public void Configure(EntityTypeBuilder<ShareLink> builder)
    {
        builder.ToTable("share_links", t =>
        {
            t.HasCheckConstraint(
                "ck_share_links_download_count_non_negative",
                "\"DownloadCount\" >= 0");
            t.HasCheckConstraint(
                "ck_share_links_max_downloads_positive_or_null",
                "\"MaxDownloads\" IS NULL OR \"MaxDownloads\" > 0");
        });

        builder.HasKey(s => s.Id);

        builder.Property(s => s.Id)
            .ValueGeneratedNever();

        builder.Property(s => s.TokenHash)
            .IsRequired()
            .HasMaxLength(64)
            .IsFixedLength();

        builder.Property(s => s.CreatedAt)
            .HasColumnType("timestamp with time zone");

        builder.Property(s => s.ExpiresAt)
            .HasColumnType("timestamp with time zone");

        builder.Property(s => s.RevokedAt)
            .HasColumnType("timestamp with time zone");

        builder.Property(s => s.LastAccessedAt)
            .HasColumnType("timestamp with time zone");

        builder.Property(s => s.DownloadCount)
            .HasDefaultValue(0);

        builder.HasIndex(s => s.TokenHash)
            .IsUnique()
            .HasDatabaseName("ux_share_links_token_hash");

        builder.HasIndex(s => s.OwnerUserId)
            .HasDatabaseName("ix_share_links_owner");

        builder.HasIndex(s => s.FileItemId)
            .HasDatabaseName("ix_share_links_file_item");

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(s => s.OwnerUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<FileItem>()
            .WithMany()
            .HasForeignKey(s => s.FileItemId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
