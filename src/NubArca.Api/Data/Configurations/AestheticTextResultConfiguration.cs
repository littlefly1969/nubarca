using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NubArca.Api.Domain;

namespace NubArca.Api.Data.Configurations;

public class AestheticTextResultConfiguration : IEntityTypeConfiguration<AestheticTextResult>
{
    public void Configure(EntityTypeBuilder<AestheticTextResult> builder)
    {
        builder.ToTable("aesthetic_text_results");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).ValueGeneratedNever();

        builder.Property(t => t.TextKind).IsRequired().HasMaxLength(48);
        builder.Property(t => t.Language).IsRequired().HasMaxLength(16);
        // Bounded but generous; the sidecar caps per-text size and the validator
        // rejects over-long text before persistence.
        builder.Property(t => t.Text).IsRequired().HasMaxLength(8000);
        builder.Property(t => t.CreatedAt).HasColumnType("timestamp with time zone");

        builder.HasIndex(t => new { t.RunId, t.TextKind })
            .HasDatabaseName("ix_aesthetic_text_results_run_kind");

        builder.HasOne<AestheticAnalysisRun>()
            .WithMany()
            .HasForeignKey(t => t.RunId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
