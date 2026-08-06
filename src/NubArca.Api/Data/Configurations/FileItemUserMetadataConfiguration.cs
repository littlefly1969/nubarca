using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NubArca.Api.Domain;

namespace NubArca.Api.Data.Configurations;

public class FileItemUserMetadataConfiguration : IEntityTypeConfiguration<FileItemUserMetadata>
{
    public void Configure(EntityTypeBuilder<FileItemUserMetadata> builder)
    {
        builder.ToTable("file_item_user_metadata", t =>
        {
            t.HasCheckConstraint(
                "ck_file_item_user_metadata_rating_range",
                "\"Rating\" IS NULL OR (\"Rating\" >= 0 AND \"Rating\" <= 5)");
        });

        builder.HasKey(m => m.Id);

        builder.Property(m => m.Id)
            .ValueGeneratedNever();

        builder.Property(m => m.Title)
            .HasMaxLength(255);

        builder.Property(m => m.Description)
            .HasMaxLength(2000);

        // Curated JSON array of tags. Stored as text; no DB-level shape
        // constraint — normalization/validation happens in the service.
        builder.Property(m => m.TagsJson);

        builder.Property(m => m.LocationOverride)
            .HasMaxLength(512);

        builder.Property(m => m.IsFavorite)
            .IsRequired();

        builder.Property(m => m.DateTakenOverride)
            .HasColumnType("timestamp with time zone");

        builder.Property(m => m.CreatedAt)
            .HasColumnType("timestamp with time zone");

        builder.Property(m => m.UpdatedAt)
            .HasColumnType("timestamp with time zone");

        // At most one user-metadata row per FileItem.
        builder.HasIndex(m => m.FileItemId)
            .IsUnique()
            .HasDatabaseName("ux_file_item_user_metadata_file");

        builder.HasOne<FileItem>()
            .WithMany()
            .HasForeignKey(m => m.FileItemId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
