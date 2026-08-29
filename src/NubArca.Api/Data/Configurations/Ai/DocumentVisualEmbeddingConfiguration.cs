using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NubArca.Api.Domain.Ai;

namespace NubArca.Api.Data.Configurations.Ai;

public class DocumentVisualEmbeddingConfiguration : IEntityTypeConfiguration<DocumentVisualEmbedding>
{
    public void Configure(EntityTypeBuilder<DocumentVisualEmbedding> builder)
    {
        builder.ToTable("document_visual_embeddings", t =>
        {
            t.HasCheckConstraint(
                "ck_document_visual_embeddings_dimension_positive",
                "\"Dimension\" > 0");
            // A dense row holds exactly one vector and a late-interaction row
            // holds at least one. Zero vectors in a stored embedding is the
            // shape a truncation bug leaves behind.
            t.HasCheckConstraint(
                "ck_document_visual_embeddings_vector_count_positive",
                "\"VectorCount\" > 0");
            t.HasCheckConstraint(
                "ck_document_visual_embeddings_dense_is_single",
                "\"Layout\" <> 'dense' OR \"VectorCount\" = 1");
        });

        builder.HasKey(e => e.Id);

        builder.Property(e => e.Id).ValueGeneratedNever();

        builder.Property(e => e.Layout).IsRequired().HasMaxLength(24);
        builder.Property(e => e.EmbeddingBytes).IsRequired();
        builder.Property(e => e.CreatedAt).HasColumnType("timestamp with time zone");

        builder.HasIndex(e => new { e.DocumentVisualUnitId, e.ProfileId })
            .IsUnique()
            .HasDatabaseName("ux_document_visual_embeddings_unit_profile");

        builder.HasOne<DocumentVisualUnit>()
            .WithMany()
            .HasForeignKey(e => e.DocumentVisualUnitId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<AiProfile>()
            .WithMany()
            .HasForeignKey(e => e.ProfileId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
