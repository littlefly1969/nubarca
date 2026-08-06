using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NubArca.Api.Domain;

namespace NubArca.Api.Data.Configurations;

public class BackgroundJobConfiguration : IEntityTypeConfiguration<BackgroundJob>
{
    public void Configure(EntityTypeBuilder<BackgroundJob> builder)
    {
        builder.ToTable("background_jobs", t =>
        {
            t.HasCheckConstraint(
                "ck_background_jobs_attempts_nonneg",
                "\"Attempts\" >= 0 AND \"MaxAttempts\" >= 1");
        });

        builder.HasKey(j => j.Id);
        builder.Property(j => j.Id).ValueGeneratedNever();

        builder.Property(j => j.Type).IsRequired().HasMaxLength(100);
        builder.Property(j => j.Status).IsRequired().HasMaxLength(20);

        // Flag-only JSON payload. Bounded — payloads are tiny by design.
        builder.Property(j => j.PayloadJson).IsRequired().HasMaxLength(4000);

        builder.Property(j => j.LockOwner).HasMaxLength(128);
        builder.Property(j => j.LastErrorCode).HasMaxLength(100);
        builder.Property(j => j.LastErrorMessage).HasMaxLength(500);
        builder.Property(j => j.IdempotencyKey).HasMaxLength(200);

        // Slice 89: progress message is a short, handler-authored phase/count
        // string. Bounded so it can never carry a large or sensitive payload.
        builder.Property(j => j.ProgressMessage).HasMaxLength(200);

        // Scheduler v2: cooperative slicing state.
        // SliceNumber defaults to 0 so the column backfills cleanly on existing
        // rows. CheckpointJson is UNBOUNDED text (no HasMaxLength) — future
        // AI/OCR/embedding jobs may need larger resume state; it is internal
        // and never returned by any admin DTO. YieldReason is a short enum-ish
        // label.
        builder.Property(j => j.SliceNumber).HasDefaultValue(0);
        builder.Property(j => j.CheckpointJson).HasColumnType("text");
        builder.Property(j => j.YieldReason).HasMaxLength(40);

        builder.Property(j => j.CreatedAt).HasColumnType("timestamp with time zone");
        builder.Property(j => j.AvailableAt).HasColumnType("timestamp with time zone");
        builder.Property(j => j.StartedAt).HasColumnType("timestamp with time zone");
        builder.Property(j => j.CompletedAt).HasColumnType("timestamp with time zone");
        builder.Property(j => j.LeaseUntil).HasColumnType("timestamp with time zone");
        builder.Property(j => j.HeartbeatAt).HasColumnType("timestamp with time zone");
        builder.Property(j => j.UpdatedAt).HasColumnType("timestamp with time zone");

        // Worker claim path: queued + available filter (and the running +
        // expired-lease reclaim branch). Serves the oldest-first candidate
        // fetch (starvation-grace pool).
        builder.HasIndex(j => new { j.Status, j.AvailableAt })
            .HasDatabaseName("ix_background_jobs_status_available");
        builder.HasIndex(j => j.Type)
            .HasDatabaseName("ix_background_jobs_type");

        // Scheduler v2: highest-priority-first candidate fetch for the claim
        // loop (top-K by (Priority, AvailableAt) within eligible rows).
        builder.HasIndex(j => new { j.Status, j.Priority, j.AvailableAt })
            .HasDatabaseName("ix_background_jobs_status_priority_available");

        // Slice 90: admin jobs list — newest-first, optionally filtered by
        // status. (Status, CreatedAt) serves the status-filtered list directly
        // and the unfiltered newest-first list via the trailing CreatedAt key.
        builder.HasIndex(j => new { j.Status, j.CreatedAt })
            .HasDatabaseName("ix_background_jobs_status_created");

        // Idempotency collapses only LIVE work. Terminal history keeps its key
        // for auditability, but must not prevent a later explicit rerun of the
        // same idempotent operation.
        builder.HasIndex(j => j.IdempotencyKey)
            .IsUnique()
            .HasDatabaseName("ux_background_jobs_idempotency")
            .HasFilter("\"IdempotencyKey\" IS NOT NULL AND \"Status\" IN ('queued', 'running')");
    }
}
