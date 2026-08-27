using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NubArca.Api.Domain;
using NubArca.Api.Domain.Ai;

namespace NubArca.Api.Data.Configurations.Ai;

public class DocumentTextConfiguration : IEntityTypeConfiguration<DocumentText>
{
    public void Configure(EntityTypeBuilder<DocumentText> builder)
    {
        builder.ToTable("document_texts", t =>
        {
            t.HasCheckConstraint(
                "ck_document_texts_char_count_non_negative",
                "\"CharCount\" IS NULL OR \"CharCount\" >= 0");
        });

        builder.HasKey(d => d.Id);

        builder.Property(d => d.Id)
            .ValueGeneratedNever();

        builder.Property(d => d.Source)
            .IsRequired()
            .HasMaxLength(32);

        builder.Property(d => d.Status)
            .IsRequired()
            .HasMaxLength(32);

        builder.Property(d => d.ErrorCode)
            .HasMaxLength(100);

        builder.Property(d => d.TextHash)
            .HasMaxLength(64);

        // Internal-only full text. text on PostgreSQL; SQLite uses dynamic typing.
        builder.Property(d => d.Text)
            .HasColumnType("text");

        builder.Property(d => d.Language)
            .HasMaxLength(32);

        builder.Property(d => d.CreatedAt)
            .HasColumnType("timestamp with time zone");

        builder.Property(d => d.UpdatedAt)
            .HasColumnType("timestamp with time zone");

        builder.HasIndex(d => new { d.FileItemId, d.ProfileId })
            .IsUnique()
            .HasDatabaseName("ux_document_texts_file_profile");

        builder.HasIndex(d => d.OwnerUserId)
            .HasDatabaseName("ix_document_texts_owner");

        // AT MOST ONE CURRENT EXTRACTION PER FILE, enforced by the database.
        //
        // A filtered unique index rather than application discipline, because
        // the failure it prevents is silent. Two current rows do not throw
        // anywhere: retrieval simply joins both, and somebody's question gets
        // answered from a mixture of two readings of their document with no
        // symptom that anything is wrong. A partial index turns that into a
        // write that cannot commit, at the moment it is attempted.
        //
        // Filtered on IsCurrent so historical rows are unconstrained — there may
        // be any number of superseded readings of one file, and they are the
        // provenance a future extractor upgrade reads.
        builder.HasIndex(d => d.FileItemId)
            .IsUnique()
            .HasFilter("\"IsCurrent\"")
            .HasDatabaseName("ux_document_texts_current_per_file");

        // Not a foreign key to BlobObject on purpose. This column records WHICH
        // BYTES were read, for idempotence; it is not a reference that should
        // keep a blob alive or cascade when one is purged. Reference counting is
        // FileItem's job, and adding a second referent here would make a cache
        // row participate in storage lifetime.
        builder.HasIndex(d => d.SourceBlobObjectId)
            .HasDatabaseName("ix_document_texts_source_blob");

        builder.HasOne<FileItem>()
            .WithMany()
            .HasForeignKey(d => d.FileItemId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(d => d.OwnerUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<AiProfile>()
            .WithMany()
            .HasForeignKey(d => d.ProfileId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
