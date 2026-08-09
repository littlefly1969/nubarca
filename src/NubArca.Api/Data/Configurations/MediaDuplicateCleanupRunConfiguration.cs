using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NubArca.Api.Domain;

namespace NubArca.Api.Data.Configurations;

public sealed class MediaDuplicateCleanupRunConfiguration
    : IEntityTypeConfiguration<MediaDuplicateCleanupRun>
{
    public void Configure(EntityTypeBuilder<MediaDuplicateCleanupRun> builder)
    {
        builder.ToTable("media_duplicate_cleanup_runs");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedNever();
        builder.Property(r => r.Status).IsRequired().HasMaxLength(20);
        builder.Property(r => r.ErrorSummary).HasMaxLength(500);
        builder.Property(r => r.CreatedAt).HasColumnType("timestamp with time zone");
        builder.Property(r => r.StartedAt).HasColumnType("timestamp with time zone");
        builder.Property(r => r.CompletedAt).HasColumnType("timestamp with time zone");
        builder.Property(r => r.UpdatedAt).HasColumnType("timestamp with time zone");
        builder.HasIndex(r => new { r.OwnerUserId, r.CreatedAt })
            .IsDescending(false, true)
            .HasDatabaseName("ix_media_duplicate_cleanup_runs_owner_created");
        builder.HasIndex(r => r.JobId)
            .HasDatabaseName("ix_media_duplicate_cleanup_runs_job");
    }
}
