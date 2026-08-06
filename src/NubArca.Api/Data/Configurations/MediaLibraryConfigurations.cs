using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NubArca.Api.Domain;

namespace NubArca.Api.Data.Configurations;

public class MediaLibraryRuleConfiguration : IEntityTypeConfiguration<MediaLibraryRule>
{
    public void Configure(EntityTypeBuilder<MediaLibraryRule> builder)
    {
        builder.ToTable("media_library_rules");

        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedNever();

        builder.Property(r => r.RuleType).IsRequired().HasMaxLength(10);

        builder.Property(r => r.CreatedAt).HasColumnType("timestamp with time zone");
        builder.Property(r => r.UpdatedAt).HasColumnType("timestamp with time zone");

        builder.HasOne<Folder>()
            .WithMany()
            .HasForeignKey(r => r.FolderId)
            .OnDelete(DeleteBehavior.Restrict);

        // One rule per folder (the rule's kind flags cover photo/video splits);
        // also the owner-scoped lookup the recompute walk uses.
        builder.HasIndex(r => new { r.OwnerUserId, r.FolderId })
            .IsUnique()
            .HasDatabaseName("ux_media_library_rules_owner_folder");
    }
}

public class FileItemLocationConfiguration : IEntityTypeConfiguration<FileItemLocation>
{
    public void Configure(EntityTypeBuilder<FileItemLocation> builder)
    {
        builder.ToTable("file_item_locations");

        // 1:1 with the FileItem — the file id is the key.
        builder.HasKey(l => l.FileItemId);
        builder.Property(l => l.FileItemId).ValueGeneratedNever();

        builder.Property(l => l.CreatedAt).HasColumnType("timestamp with time zone");
        builder.Property(l => l.UpdatedAt).HasColumnType("timestamp with time zone");
        builder.Property(l => l.TakenAt).HasColumnType("timestamp with time zone");

        builder.HasOne<FileItem>()
            .WithOne()
            .HasForeignKey<FileItemLocation>(l => l.FileItemId)
            .OnDelete(DeleteBehavior.Restrict);

        // The future map view lists an owner's locations (optionally by time).
        builder.HasIndex(l => new { l.OwnerUserId, l.TakenAt })
            .HasDatabaseName("ix_file_item_locations_owner_taken");
    }
}
