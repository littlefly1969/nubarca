using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NubArca.Api.Domain;

namespace NubArca.Api.Data.Configurations;

public class FileThumbnailConfiguration : IEntityTypeConfiguration<FileThumbnail>
{
    public void Configure(EntityTypeBuilder<FileThumbnail> builder)
    {
        builder.ToTable("file_thumbnails", t =>
        {
            t.HasCheckConstraint(
                "ck_file_thumbnails_width_positive",
                "\"Width\" > 0");
            t.HasCheckConstraint(
                "ck_file_thumbnails_height_positive",
                "\"Height\" > 0");
        });

        builder.HasKey(t => t.Id);

        builder.Property(t => t.Id)
            .ValueGeneratedNever();

        builder.Property(t => t.Size)
            .IsRequired()
            .HasMaxLength(32);

        builder.Property(t => t.Width)
            .IsRequired();

        builder.Property(t => t.Height)
            .IsRequired();

        // Slice 95: poster provenance (synthetic | ffmpeg | ...).
        builder.Property(t => t.PosterSource)
            .HasMaxLength(20);

        builder.Property(t => t.CreatedAt)
            .HasColumnType("timestamp with time zone");

        builder.HasIndex(t => new { t.FileItemId, t.Size })
            .IsUnique()
            .HasDatabaseName("ux_file_thumbnails_file_size");

        builder.HasIndex(t => t.BlobObjectId)
            .HasDatabaseName("ix_file_thumbnails_blob_object");

        builder.HasOne<FileItem>()
            .WithMany()
            .HasForeignKey(t => t.FileItemId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<BlobObject>()
            .WithMany()
            .HasForeignKey(t => t.BlobObjectId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
