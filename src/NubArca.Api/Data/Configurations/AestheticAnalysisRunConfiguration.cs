using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NubArca.Api.Domain;

namespace NubArca.Api.Data.Configurations;

public class AestheticAnalysisRunConfiguration : IEntityTypeConfiguration<AestheticAnalysisRun>
{
    public void Configure(EntityTypeBuilder<AestheticAnalysisRun> builder)
    {
        builder.ToTable("aesthetic_analysis_runs");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedNever();

        builder.Property(r => r.ProfileKey).IsRequired().HasMaxLength(64);
        builder.Property(r => r.ModelName).HasMaxLength(128);
        builder.Property(r => r.ModelRevision).HasMaxLength(128);
        builder.Property(r => r.RuntimeName).HasMaxLength(64);
        builder.Property(r => r.RuntimeVersion).HasMaxLength(32);
        builder.Property(r => r.PreprocessingProfileKey).IsRequired().HasMaxLength(64);
        builder.Property(r => r.RequestedCapabilities).IsRequired().HasMaxLength(256);
        builder.Property(r => r.CompletedCapabilities).IsRequired().HasMaxLength(256);
        builder.Property(r => r.Status).IsRequired().HasMaxLength(16);
        builder.Property(r => r.ErrorCode).HasMaxLength(48);
        builder.Property(r => r.CreatedAt).HasColumnType("timestamp with time zone");
        builder.Property(r => r.StartedAt).HasColumnType("timestamp with time zone");
        builder.Property(r => r.CompletedAt).HasColumnType("timestamp with time zone");

        // Bounded internal provenance + safe warnings. jsonb on PostgreSQL;
        // SQLite stores TEXT under the same declared type (EnsureCreated).
        builder.Property(r => r.RawOutputJson).HasColumnType("jsonb");
        builder.Property(r => r.WarningsJson).HasColumnType("jsonb");

        // Run history for one item, newest-first.
        builder.HasIndex(r => new { r.AestheticLabItemId, r.CreatedAt })
            .HasDatabaseName("ix_aesthetic_runs_item_created");

        // Live-run idempotency + status filtering.
        builder.HasIndex(r => new { r.AestheticLabItemId, r.Status })
            .HasDatabaseName("ix_aesthetic_runs_item_status");

        builder.HasIndex(r => r.OwnerUserId)
            .HasDatabaseName("ix_aesthetic_runs_owner");

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(r => r.OwnerUserId)
            .OnDelete(DeleteBehavior.Restrict);

        // Runs die with their lab item (retention policy: removing the item
        // purges its runs). Cascade touches only aesthetic_* rows — never a blob
        // (blob refs are released explicitly in the remove transaction BEFORE the
        // cascade), so no cascade can bypass blob-reference release.
        builder.HasOne<AestheticLabItem>()
            .WithMany()
            .HasForeignKey(r => r.AestheticLabItemId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
