using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NubArca.Api.Domain;
using NubArca.Api.Domain.Ai;
using NubArca.Api.Domain.Rag;

namespace NubArca.Api.Data.Configurations.Rag;

// The generic RAG substrate's persistence. Four tables with one job each:
// source identity, domain membership, passage, canonical embedding.
//
// Deliberately separate from the owner-private document tables
// (document_texts / document_chunks / document_chunk_embeddings) and from the
// photo/face vector tables. The concept is shared; the ownership semantics and
// the vector spaces are not, and merging them would put two privacy stories in
// one table.

public class RagSourceConfiguration : IEntityTypeConfiguration<RagSource>
{
    public void Configure(EntityTypeBuilder<RagSource> builder)
    {
        builder.ToTable("rag_sources");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();

        builder.Property(s => s.SourceKey).HasMaxLength(512).IsRequired();
        builder.Property(s => s.SourceKind).HasMaxLength(64).IsRequired();
        builder.Property(s => s.Title).HasMaxLength(512).IsRequired();
        builder.Property(s => s.Path).HasMaxLength(512).IsRequired();
        builder.Property(s => s.ContentHash).HasMaxLength(64).IsRequired();
        // Defaults to 0 for rows written before the column existed, which is not
        // any released format version — so the first index run after upgrading
        // rechunks them, which is exactly right: nothing knows how those chunks
        // were produced.
        builder.Property(s => s.IndexFormatVersion).HasDefaultValue(0);
        builder.Property(s => s.Language).HasMaxLength(16).IsRequired();
        builder.Property(s => s.CodeLanguage).HasMaxLength(32).IsRequired();
        builder.Property(s => s.MetadataJson).HasColumnType("text");
        builder.Property(s => s.CreatedAt).HasColumnType("timestamp with time zone");
        builder.Property(s => s.UpdatedAt).HasColumnType("timestamp with time zone");

        // One row per CONTENT INTERPRETATION: the key, its bytes, and how they
        // were read. This uniqueness is what makes "the same file in two
        // domains" cost a membership row instead of a second copy of its text
        // and vectors — including while the two domains sit at two different
        // revisions during a sequential upgrade, because the revision is not
        // part of this identity.
        //
        // NOT unique on SourceKey alone. That was the old shape, and it forced a
        // shared document's bytes to be rewritten in place whenever either
        // domain moved, which is precisely the mutation that had to be refused.
        builder.HasIndex(s => new { s.SourceKey, s.ContentHash, s.IndexFormatVersion })
            .IsUnique()
            .HasDatabaseName("ux_rag_sources_key_content_format");

        builder.HasIndex(s => s.SourceKey).HasDatabaseName("ix_rag_sources_key");
        builder.HasIndex(s => s.OwnerUserId).HasDatabaseName("ix_rag_sources_owner");

        // Restrict rather than Cascade: system knowledge has no owner today, and
        // deleting a user must not silently delete an index nobody expected them
        // to own.
        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(s => s.OwnerUserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public class RagDomainSourceConfiguration : IEntityTypeConfiguration<RagDomainSource>
{
    public void Configure(EntityTypeBuilder<RagDomainSource> builder)
    {
        builder.ToTable("rag_domain_sources", t =>
        {
            t.HasCheckConstraint(
                "ck_rag_domain_sources_priority_range",
                "\"Priority\" >= 1 AND \"Priority\" <= 100");
        });

        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id).ValueGeneratedNever();

        builder.Property(m => m.DomainKey).HasMaxLength(64).IsRequired();
        builder.Property(m => m.Revision).HasMaxLength(64).IsRequired();
        builder.Property(m => m.MetadataJson).HasColumnType("text");
        builder.Property(m => m.CreatedAt).HasColumnType("timestamp with time zone");
        builder.Property(m => m.UpdatedAt).HasColumnType("timestamp with time zone");

        builder.HasIndex(m => new { m.DomainKey, m.SourceId })
            .IsUnique()
            .HasDatabaseName("ux_rag_domain_sources_domain_source");

        builder.HasIndex(m => m.DomainKey).HasDatabaseName("ix_rag_domain_sources_domain");

        // Revision is the domain's snapshot claim, and `rag status` asks for the
        // distinct set of them per domain on every question.
        builder.HasIndex(m => new { m.DomainKey, m.Revision })
            .HasDatabaseName("ix_rag_domain_sources_domain_revision");

        // A source disappearing from the snapshot takes its memberships with it.
        builder.HasOne<RagSource>()
            .WithMany()
            .HasForeignKey(m => m.SourceId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public class RagChunkConfiguration : IEntityTypeConfiguration<RagChunk>
{
    public void Configure(EntityTypeBuilder<RagChunk> builder)
    {
        builder.ToTable("rag_chunks", t =>
        {
            t.HasCheckConstraint("ck_rag_chunks_ordinal_non_negative", "\"Ordinal\" >= 0");
        });

        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).ValueGeneratedNever();

        builder.Property(c => c.Heading).HasMaxLength(512).IsRequired();
        builder.Property(c => c.Text).HasColumnType("text").IsRequired();
        builder.Property(c => c.TextHash).HasMaxLength(64).IsRequired();
        builder.Property(c => c.Language).HasMaxLength(16).IsRequired();
        builder.Property(c => c.MetadataJson).HasColumnType("text");
        builder.Property(c => c.CreatedAt).HasColumnType("timestamp with time zone");

        builder.HasIndex(c => new { c.SourceId, c.Ordinal })
            .IsUnique()
            .HasDatabaseName("ux_rag_chunks_source_ordinal");

        builder.HasOne<RagSource>()
            .WithMany()
            .HasForeignKey(c => c.SourceId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public class RagChunkEmbeddingConfiguration : IEntityTypeConfiguration<RagChunkEmbedding>
{
    public void Configure(EntityTypeBuilder<RagChunkEmbedding> builder)
    {
        builder.ToTable("rag_chunk_embeddings", t =>
        {
            t.HasCheckConstraint("ck_rag_chunk_embeddings_dimension_positive", "\"Dimension\" > 0");
        });

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedNever();

        builder.Property(e => e.EmbeddingBytes).IsRequired();
        builder.Property(e => e.CreatedAt).HasColumnType("timestamp with time zone");

        // One canonical vector per (chunk, profile). Two profiles coexist; they
        // are never compared and never merged.
        builder.HasIndex(e => new { e.ChunkId, e.ProfileId })
            .IsUnique()
            .HasDatabaseName("ux_rag_chunk_embeddings_chunk_profile");

        builder.HasIndex(e => e.ProfileId).HasDatabaseName("ix_rag_chunk_embeddings_profile");

        builder.HasOne<RagChunk>()
            .WithMany()
            .HasForeignKey(e => e.ChunkId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<AiProfile>()
            .WithMany()
            .HasForeignKey(e => e.ProfileId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
