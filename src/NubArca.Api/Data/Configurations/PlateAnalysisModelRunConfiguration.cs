using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NubArca.Api.Domain;

namespace NubArca.Api.Data.Configurations;

public class PlateAnalysisModelRunConfiguration : IEntityTypeConfiguration<PlateAnalysisModelRun>
{
    public void Configure(EntityTypeBuilder<PlateAnalysisModelRun> builder)
    {
        builder.ToTable("plate_analysis_model_runs");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedNever();

        builder.Property(r => r.ProfileKey).IsRequired().HasMaxLength(64);
        builder.Property(r => r.DetectorName).HasMaxLength(64);
        builder.Property(r => r.DetectorVersion).HasMaxLength(32);
        builder.Property(r => r.OcrName).HasMaxLength(64);
        builder.Property(r => r.OcrVersion).HasMaxLength(32);
        builder.Property(r => r.CreatedAt).HasColumnType("timestamp with time zone");

        builder.HasIndex(r => r.PlateAnalysisJobId)
            .HasDatabaseName("ix_plate_analysis_model_runs_job");

        // Model runs die with their analysis job (which cascades from the image).
        builder.HasOne<PlateAnalysisJob>()
            .WithMany()
            .HasForeignKey(r => r.PlateAnalysisJobId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
