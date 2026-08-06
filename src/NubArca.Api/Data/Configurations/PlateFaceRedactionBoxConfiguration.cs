using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NubArca.Api.Domain;

namespace NubArca.Api.Data.Configurations;

public class PlateFaceRedactionBoxConfiguration : IEntityTypeConfiguration<PlateFaceRedactionBox>
{
    public void Configure(EntityTypeBuilder<PlateFaceRedactionBox> builder)
    {
        builder.ToTable("plate_face_redaction_boxes", t =>
        {
            // Normalized geometry must be non-negative (mirrors plate_detections
            // / face_detections).
            t.HasCheckConstraint("ck_plate_face_redaction_boxes_width_non_negative", "\"BoundingBoxWidth\" >= 0");
            t.HasCheckConstraint("ck_plate_face_redaction_boxes_height_non_negative", "\"BoundingBoxHeight\" >= 0");
        });

        builder.HasKey(b => b.Id);
        builder.Property(b => b.Id).ValueGeneratedNever();

        builder.Property(b => b.ModelProfileKey).IsRequired().HasMaxLength(64);
        builder.Property(b => b.CreatedAt).HasColumnType("timestamp with time zone");
        builder.Property(b => b.UpdatedAt).HasColumnType("timestamp with time zone");

        builder.HasIndex(b => new { b.OwnerUserId, b.PlateImageId })
            .HasDatabaseName("ix_plate_face_redaction_boxes_owner_image");
        builder.HasIndex(b => b.PlateImageId)
            .HasDatabaseName("ix_plate_face_redaction_boxes_image");

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(b => b.OwnerUserId)
            .OnDelete(DeleteBehavior.Restrict);

        // Boxes die with their PlateImage (Slice 1 hard delete leaves no orphans).
        builder.HasOne<PlateImage>()
            .WithMany()
            .HasForeignKey(b => b.PlateImageId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
