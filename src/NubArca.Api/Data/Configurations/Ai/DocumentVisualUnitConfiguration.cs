using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NubArca.Api.Domain.Ai;

namespace NubArca.Api.Data.Configurations.Ai;

public class DocumentVisualUnitConfiguration : IEntityTypeConfiguration<DocumentVisualUnit>
{
    public void Configure(EntityTypeBuilder<DocumentVisualUnit> builder)
    {
        builder.ToTable("document_visual_units", t =>
        {
            t.HasCheckConstraint(
                "ck_document_visual_units_dimensions_positive",
                "\"Width\" > 0 AND \"Height\" > 0");
            t.HasCheckConstraint(
                "ck_document_visual_units_ordinal_non_negative",
                "\"Ordinal\" >= 0");
            // A real PDF page is 1-based. Null everywhere else, and never zero:
            // zero is the value a 0-based renderer index leaks in as.
            t.HasCheckConstraint(
                "ck_document_visual_units_source_page_positive",
                "\"SourcePage\" IS NULL OR \"SourcePage\" >= 1");
        });

        builder.HasKey(u => u.Id);

        builder.Property(u => u.Id).ValueGeneratedNever();

        builder.Property(u => u.RenderKind).IsRequired().HasMaxLength(32);
        builder.Property(u => u.SourceLocatorKind).HasMaxLength(16);
        builder.Property(u => u.SourceLocatorLabel).HasMaxLength(200);
        builder.Property(u => u.PixelHash).IsRequired().HasMaxLength(64);

        builder.Property(u => u.CreatedAt).HasColumnType("timestamp with time zone");

        builder.HasIndex(u => new { u.DocumentVisualIndexId, u.Ordinal })
            .IsUnique()
            .HasDatabaseName("ux_document_visual_units_index_ordinal");

        builder.HasOne<DocumentVisualIndex>()
            .WithMany()
            .HasForeignKey(u => u.DocumentVisualIndexId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
