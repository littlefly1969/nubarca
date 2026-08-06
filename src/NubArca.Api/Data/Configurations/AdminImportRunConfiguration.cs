using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NubArca.Api.Domain;

namespace NubArca.Api.Data.Configurations;

public class AdminImportRunConfiguration : IEntityTypeConfiguration<AdminImportRun>
{
    public void Configure(EntityTypeBuilder<AdminImportRun> builder)
    {
        builder.ToTable("admin_import_runs");

        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedNever();

        builder.Property(r => r.RootId).IsRequired().HasMaxLength(64);
        // Bounded the same way RelativeUploadPath bounds a relative path.
        builder.Property(r => r.SourceRelativePath).IsRequired().HasMaxLength(4096);
        builder.Property(r => r.Status).IsRequired().HasMaxLength(64);
        // Slice 92: sub-phase of a running job (scanning | importing).
        builder.Property(r => r.Phase).HasMaxLength(64);
        builder.Property(r => r.CurrentRelativePath).HasMaxLength(4096);
        builder.Property(r => r.ErrorSummary).HasMaxLength(500);

        builder.Property(r => r.ScanCompletedAt).HasColumnType("timestamp with time zone");
        builder.Property(r => r.CreatedAt).HasColumnType("timestamp with time zone");
        builder.Property(r => r.StartedAt).HasColumnType("timestamp with time zone");
        builder.Property(r => r.CompletedAt).HasColumnType("timestamp with time zone");
        builder.Property(r => r.UpdatedAt).HasColumnType("timestamp with time zone");

        builder.HasIndex(r => r.TargetUserId).HasDatabaseName("ix_admin_import_runs_target_user");
        // Slice 91: look up a run by its executing background job for status
        // reconciliation (and the reverse lookup).
        builder.HasIndex(r => r.JobId).HasDatabaseName("ix_admin_import_runs_job");
        // Slice 84: the admin runs list is `ORDER BY CreatedAt DESC` with no
        // filter — a descending index lets PostgreSQL satisfy it without a sort.
        builder.HasIndex(r => r.CreatedAt)
            .IsDescending()
            .HasDatabaseName("ix_admin_import_runs_created_desc");
    }
}
