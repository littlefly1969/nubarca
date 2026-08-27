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

            // A page is a 1-based position in a document, so 0 is not a small
            // page — it is an uninitialised int that got written. Same for the
            // locator index. Null means "this format has no such position",
            // which is a different statement and stays legal.
            t.HasCheckConstraint(
                "ck_document_chunks_page_positive",
                "\"Page\" IS NULL OR \"Page\" >= 1");

            t.HasCheckConstraint(
                "ck_document_chunks_locator_index_positive",
                "\"LocatorIndex\" IS NULL OR \"LocatorIndex\" >= 1");
        });

        builder.HasKey(c => c.Id);

        builder.Property(c => c.Id)
            .ValueGeneratedNever();

        builder.Property(c => c.Heading)
            .HasMaxLength(512);

        builder.Property(c => c.Text)
            .HasColumnType("text");

        builder.Property(c => c.TextHash)
            .HasMaxLength(64);

        // Bounded, like every other string a document can influence. The kind is
        // a small closed vocabulary; the label is a heading path, a sheet name
        // or a slide title, all of which come from the document itself and are
        // therefore attacker-controlled length.
        builder.Property(c => c.LocatorKind)
            .HasMaxLength(32);

        builder.Property(c => c.LocatorLabel)
            .HasMaxLength(512);

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
