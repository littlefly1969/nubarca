using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NubArca.Api.Domain;
using NubArca.Api.Domain.Ai;

namespace NubArca.Api.Data.Configurations.Ai;

public class DocumentChunkConfiguration : IEntityTypeConfiguration<DocumentChunk>
{
    public void Configure(EntityTypeBuilder<DocumentChunk> builder)
    {
        builder.ToTable("document_chunks", t =>
        {
            t.HasCheckConstraint(
                "ck_document_chunks_ordinal_non_negative",
                "\"Ordinal\" >= 0");
        });

        builder.HasKey(c => c.Id);

        builder.Property(c => c.Id)
            .ValueGeneratedNever();

        builder.Property(c => c.Text)
            .HasColumnType("text");

        builder.Property(c => c.TextHash)
            .HasMaxLength(64);

        builder.Property(c => c.CreatedAt)
            .HasColumnType("timestamp with time zone");

        builder.HasIndex(c => new { c.DocumentTextId, c.ProfileId, c.Ordinal })
            .IsUnique()
            .HasDatabaseName("ux_document_chunks_doc_profile_ordinal");

        builder.HasIndex(c => c.OwnerUserId)
            .HasDatabaseName("ix_document_chunks_owner");

        builder.HasOne<DocumentText>()
            .WithMany()
            .HasForeignKey(c => c.DocumentTextId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(c => c.OwnerUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<AiProfile>()
            .WithMany()
            .HasForeignKey(c => c.ProfileId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
