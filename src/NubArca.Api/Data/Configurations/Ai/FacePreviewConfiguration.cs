using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NubArca.Api.Domain.Ai;

namespace NubArca.Api.Data.Configurations.Ai;

public class FacePreviewConfiguration : IEntityTypeConfiguration<FacePreview>
{
    public void Configure(EntityTypeBuilder<FacePreview> builder)
    {
        builder.ToTable("face_previews");

        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedNever();
        builder.Property(p => p.Size).IsRequired().HasMaxLength(32);
        builder.Property(p => p.CreatedAt).HasColumnType("timestamp with time zone");

        // One cached crop per (face, size). Regenerable.
        builder.HasIndex(p => new { p.FaceDetectionId, p.Size })
            .IsUnique()
            .HasDatabaseName("ux_face_previews_face_size");

        // Previews die with their blob-level detection (which cascades from blob).
        builder.HasOne<FaceDetection>()
            .WithMany()
            .HasForeignKey(p => p.FaceDetectionId)
            .OnDelete(DeleteBehavior.Cascade);

        // BlobObjectId is a plain correlation id to a refcount-managed derived
        // blob (no FK, mirroring the derived-artifact convention).
    }
}
