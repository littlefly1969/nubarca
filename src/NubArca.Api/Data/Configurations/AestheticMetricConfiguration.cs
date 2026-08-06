using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NubArca.Api.Domain;

namespace NubArca.Api.Data.Configurations;

public class AestheticMetricConfiguration : IEntityTypeConfiguration<AestheticMetric>
{
    public void Configure(EntityTypeBuilder<AestheticMetric> builder)
    {
        builder.ToTable("aesthetic_metrics");
        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id).ValueGeneratedNever();

        builder.Property(m => m.MetricKey).IsRequired().HasMaxLength(64);
        builder.Property(m => m.MetricGroup).IsRequired().HasMaxLength(32);
        builder.Property(m => m.CreatedAt).HasColumnType("timestamp with time zone");

        // Per-run metric lookup + one row per (run, key).
        builder.HasIndex(m => new { m.RunId, m.MetricKey })
            .HasDatabaseName("ux_aesthetic_metrics_run_key")
            .IsUnique();

        // Cross-run queries by dimension (e.g. history of overall_aesthetic).
        builder.HasIndex(m => m.MetricKey)
            .HasDatabaseName("ix_aesthetic_metrics_key");

        // Metrics die with their run (which dies with its item).
        builder.HasOne<AestheticAnalysisRun>()
            .WithMany()
            .HasForeignKey(m => m.RunId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
