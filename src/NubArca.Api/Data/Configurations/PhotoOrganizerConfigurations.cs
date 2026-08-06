using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NubArca.Api.Domain;

namespace NubArca.Api.Data.Configurations;

public class PhotoOrganizerRunConfiguration : IEntityTypeConfiguration<PhotoOrganizerRun>
{
    public void Configure(EntityTypeBuilder<PhotoOrganizerRun> builder)
    {
        builder.ToTable("photo_organizer_runs");

        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedNever();

        builder.Property(r => r.Kind).IsRequired().HasMaxLength(32);
        builder.Property(r => r.Status).IsRequired().HasMaxLength(20);
        // Options + dry-run snapshot are small validated JSON documents.
        builder.Property(r => r.OptionsJson).IsRequired().HasMaxLength(8192);
        builder.Property(r => r.DryRunSummaryJson).HasMaxLength(4096);
        builder.Property(r => r.ErrorSummary).HasMaxLength(500);

        builder.Property(r => r.CreatedAt).HasColumnType("timestamp with time zone");
        builder.Property(r => r.StartedAt).HasColumnType("timestamp with time zone");
        builder.Property(r => r.CompletedAt).HasColumnType("timestamp with time zone");
        builder.Property(r => r.UpdatedAt).HasColumnType("timestamp with time zone");

        // Owner's run history, newest first, without a sort step.
        builder.HasIndex(r => new { r.OwnerUserId, r.CreatedAt })
            .IsDescending(false, true)
            .HasDatabaseName("ix_photo_organizer_runs_owner_created");
        builder.HasIndex(r => r.JobId).HasDatabaseName("ix_photo_organizer_runs_job");
    }
}

public class PhotoOrganizerMoveConfiguration : IEntityTypeConfiguration<PhotoOrganizerMove>
{
    public void Configure(EntityTypeBuilder<PhotoOrganizerMove> builder)
    {
        builder.ToTable("photo_organizer_moves");

        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id).ValueGeneratedNever();

        builder.Property(m => m.SourceName).IsRequired().HasMaxLength(255);
        builder.Property(m => m.TargetName).IsRequired().HasMaxLength(255);
        builder.Property(m => m.DateTakenSource).IsRequired().HasMaxLength(32);

        builder.Property(m => m.EffectiveDateTaken).HasColumnType("timestamp with time zone");
        builder.Property(m => m.CreatedAt).HasColumnType("timestamp with time zone");

        // All moves for a run, in creation order (manifest read / future undo).
        builder.HasIndex(m => new { m.RunId, m.CreatedAt })
            .HasDatabaseName("ix_photo_organizer_moves_run");
    }
}
