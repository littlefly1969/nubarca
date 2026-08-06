using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NubArca.Api.Domain;

namespace NubArca.Api.Data.Configurations;

public class DerivativeDiagnosticConfiguration : IEntityTypeConfiguration<DerivativeDiagnostic>
{
    public void Configure(EntityTypeBuilder<DerivativeDiagnostic> builder)
    {
        builder.ToTable("derivative_diagnostics", t =>
        {
            t.HasCheckConstraint(
                "ck_derivative_diagnostics_attempt_count_non_negative",
                "\"AttemptCount\" >= 0");
        });

        builder.HasKey(d => d.Id);

        builder.Property(d => d.Id)
            .ValueGeneratedNever();

        builder.Property(d => d.Size)
            .IsRequired()
            .HasMaxLength(32);

        builder.Property(d => d.Status)
            .IsRequired()
            .HasMaxLength(32);

        builder.Property(d => d.ErrorCode)
            .HasMaxLength(64);

        // Bounded, sanitized reason — never a raw exception / path.
        builder.Property(d => d.Message)
            .HasMaxLength(200);

        builder.Property(d => d.DetectedContentType)
            .HasMaxLength(255);

        builder.Property(d => d.DetectedFormat)
            .HasMaxLength(64);

        builder.Property(d => d.Backend)
            .HasMaxLength(32);

        builder.Property(d => d.FirstAttemptedAt)
            .HasColumnType("timestamp with time zone");

        builder.Property(d => d.LastAttemptedAt)
            .HasColumnType("timestamp with time zone");

        builder.Property(d => d.NextRetryAt)
            .HasColumnType("timestamp with time zone");

        // Exactly one diagnostic per logical derivative target.
        builder.HasIndex(d => new { d.FileItemId, d.Size })
            .IsUnique()
            .HasDatabaseName("ux_derivative_diagnostics_file_size");

        // Aggregation / retry-candidate scans read by size + status.
        builder.HasIndex(d => new { d.Size, d.Status })
            .HasDatabaseName("ix_derivative_diagnostics_size_status");

        // Cascade: diagnostics are disposable state. A hard-deleted FileItem
        // takes its diagnostics with it (no refcount/blob involvement, so this
        // is safe and avoids a Restrict that would block the sweeper).
        builder.HasOne<FileItem>()
            .WithMany()
            .HasForeignKey(d => d.FileItemId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
