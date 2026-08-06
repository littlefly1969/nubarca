using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NubArca.Api.Domain;
using NubArca.Api.Domain.Ai;

namespace NubArca.Api.Data.Configurations.Ai;

public class BlobEmbeddingConfiguration : IEntityTypeConfiguration<BlobEmbedding>
{
    public void Configure(EntityTypeBuilder<BlobEmbedding> builder)
    {
        builder.ToTable("blob_embeddings", t =>
        {
            t.HasCheckConstraint(
                "ck_blob_embeddings_dimension_positive",
                "\"Dimension\" > 0");
        });

        builder.HasKey(e => e.Id);

        builder.Property(e => e.Id)
            .ValueGeneratedNever();

        builder.Property(e => e.EmbeddingBytes)
            .IsRequired();

        builder.Property(e => e.CreatedAt)
            .HasColumnType("timestamp with time zone");

        builder.HasIndex(e => new { e.BlobObjectId, e.ProfileId })
            .IsUnique()
            .HasDatabaseName("ux_blob_embeddings_blob_profile");

        builder.HasOne<BlobObject>()
            .WithMany()
            .HasForeignKey(e => e.BlobObjectId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<AiProfile>()
            .WithMany()
            .HasForeignKey(e => e.ProfileId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
