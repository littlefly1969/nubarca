using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NubArca.Api.Domain;
using NubArca.Api.Domain.Ai;

namespace NubArca.Api.Data.Configurations.Ai;

public class DocumentVisualIndexConfiguration : IEntityTypeConfiguration<DocumentVisualIndex>
{
    public void Configure(EntityTypeBuilder<DocumentVisualIndex> builder)
    {
        builder.ToTable("document_visual_indexes", t =>
        {
            t.HasCheckConstraint(
                "ck_document_visual_indexes_unit_count_non_negative",
                "\"UnitCount\" >= 0");

            // A COMPLETED INDEX WITH NO UNITS IS NOT A COMPLETE READING.
            //
            // Stated in the database rather than in the indexer, because this is
            // the exact shape a partial-publication bug produces: a renderer that
            // returned nothing, an index marked done, and a document that
            // silently answers no visual question forever. A constraint makes
            // that a write that cannot commit.
            t.HasCheckConstraint(
                "ck_document_visual_indexes_completed_has_units",
                "\"Status\" <> 'completed' OR \"UnitCount\" > 0");
        });

        builder.HasKey(i => i.Id);

        builder.Property(i => i.Id).ValueGeneratedNever();

        builder.Property(i => i.RenderProfileKey).IsRequired().HasMaxLength(64);
        builder.Property(i => i.Status).IsRequired().HasMaxLength(32);
        builder.Property(i => i.ErrorCode).HasMaxLength(100);

        builder.Property(i => i.CreatedAt).HasColumnType("timestamp with time zone");
        builder.Property(i => i.UpdatedAt).HasColumnType("timestamp with time zone");
        builder.Property(i => i.CompletedAt).HasColumnType("timestamp with time zone");

        // ONE INDEX PER (file, bytes, render identity, embedding identity).
        //
        // All four, because each one independently changes what the pixels or
        // the vectors mean, and any two documents differing in one of them are
        // different readings that must not overwrite each other. The blob is in
        // the key rather than being "the current one" so that replacing a file's
        // content leaves the old index inert instead of racing to update it.
        builder.HasIndex(i => new
            {
                i.FileItemId, i.SourceBlobObjectId, i.RenderProfileKey, i.EmbeddingProfileId,
            })
            .IsUnique()
            .HasDatabaseName("ux_document_visual_indexes_file_blob_render_profile");

        builder.HasIndex(i => i.OwnerUserId)
            .HasDatabaseName("ix_document_visual_indexes_owner");

        // The retrieval join's shape: this owner's completed indexes under one
        // embedding profile.
        builder.HasIndex(i => new { i.OwnerUserId, i.Status, i.EmbeddingProfileId })
            .HasDatabaseName("ix_document_visual_indexes_owner_status_profile");

        builder.HasOne<FileItem>()
            .WithMany()
            .HasForeignKey(i => i.FileItemId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(i => i.OwnerUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<AiProfile>()
            .WithMany()
            .HasForeignKey(i => i.EmbeddingProfileId)
            .OnDelete(DeleteBehavior.Restrict);

        // Deliberately NOT a foreign key to BlobObject, exactly as
        // `DocumentText.SourceBlobObjectId` is not. This column records WHICH
        // BYTES were rendered; it is not a reference that should keep a blob
        // alive or cascade when one is purged. Reference counting is FileItem's
        // job and a cache row must not participate in storage lifetime.
        builder.HasIndex(i => i.SourceBlobObjectId)
            .HasDatabaseName("ix_document_visual_indexes_source_blob");
    }
}
