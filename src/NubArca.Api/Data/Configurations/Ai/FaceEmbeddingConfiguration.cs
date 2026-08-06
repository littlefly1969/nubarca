using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NubArca.Api.Domain.Ai;

namespace NubArca.Api.Data.Configurations.Ai;

public class FaceEmbeddingConfiguration : IEntityTypeConfiguration<FaceEmbedding>
{
    public void Configure(EntityTypeBuilder<FaceEmbedding> builder)
    {
        builder.ToTable("face_embeddings", t =>
        {
            t.HasCheckConstraint(
                "ck_face_embeddings_dimension_positive",
                "\"Dimension\" > 0");
        });

        builder.HasKey(e => e.Id);

        builder.Property(e => e.Id)
            .ValueGeneratedNever();

        builder.Property(e => e.EmbeddingBytes)
            .IsRequired();

        builder.Property(e => e.EmbeddingStatus)
            .IsRequired()
            .HasMaxLength(32);

        builder.Property(e => e.ErrorCode)
            .HasMaxLength(64);

        builder.Property(e => e.CreatedAt)
            .HasColumnType("timestamp with time zone");

        builder.Property(e => e.UpdatedAt)
            .HasColumnType("timestamp with time zone");

        builder.HasIndex(e => new { e.FaceDetectionId, e.ProfileId })
            .IsUnique()
            .HasDatabaseName("ux_face_embeddings_detection_profile");

        builder.HasOne<FaceDetection>()
            .WithMany()
            .HasForeignKey(e => e.FaceDetectionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<AiProfile>()
            .WithMany()
            .HasForeignKey(e => e.ProfileId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
