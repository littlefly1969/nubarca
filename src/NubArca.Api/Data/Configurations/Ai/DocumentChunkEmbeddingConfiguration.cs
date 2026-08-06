using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NubArca.Api.Domain.Ai;

namespace NubArca.Api.Data.Configurations.Ai;

public class DocumentChunkEmbeddingConfiguration : IEntityTypeConfiguration<DocumentChunkEmbedding>
{
    public void Configure(EntityTypeBuilder<DocumentChunkEmbedding> builder)
    {
        builder.ToTable("document_chunk_embeddings", t =>
        {
            t.HasCheckConstraint(
                "ck_document_chunk_embeddings_dimension_positive",
                "\"Dimension\" > 0");
        });

        builder.HasKey(e => e.Id);

        builder.Property(e => e.Id)
            .ValueGeneratedNever();

        builder.Property(e => e.EmbeddingBytes)
            .IsRequired();

        builder.Property(e => e.CreatedAt)
            .HasColumnType("timestamp with time zone");

        builder.HasIndex(e => new { e.DocumentChunkId, e.ProfileId })
            .IsUnique()
            .HasDatabaseName("ux_document_chunk_embeddings_chunk_profile");

        builder.HasOne<DocumentChunk>()
            .WithMany()
            .HasForeignKey(e => e.DocumentChunkId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<AiProfile>()
            .WithMany()
            .HasForeignKey(e => e.ProfileId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
