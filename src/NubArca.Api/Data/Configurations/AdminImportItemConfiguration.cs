using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NubArca.Api.Domain;

namespace NubArca.Api.Data.Configurations;

public class AdminImportItemConfiguration : IEntityTypeConfiguration<AdminImportItem>
{
    public void Configure(EntityTypeBuilder<AdminImportItem> builder)
    {
        builder.ToTable("admin_import_items");

        builder.HasKey(i => i.Id);
        builder.Property(i => i.Id).ValueGeneratedNever();

        builder.Property(i => i.Kind).IsRequired().HasMaxLength(64);
        // Bounded relative path. Entries whose relative path exceeds this are
        // recorded as skipped/path_too_long at scan time, never truncated (a
        // truncated path would break resume identity).
        builder.Property(i => i.RelativePath).IsRequired().HasMaxLength(2048);
        // 64 chars: accommodates the longest disjoint skip status
        // (skipped_previously_deleted = 26) with headroom for future statuses.
        builder.Property(i => i.Status).IsRequired().HasMaxLength(64);
        builder.Property(i => i.FailureCategory).HasMaxLength(40);
        builder.Property(i => i.FailureMessage).HasMaxLength(300);
        builder.Property(i => i.ConflictCategory).HasMaxLength(40);

        builder.Property(i => i.CreatedAt).HasColumnType("timestamp with time zone");
        builder.Property(i => i.UpdatedAt).HasColumnType("timestamp with time zone");
        builder.Property(i => i.CompletedAt).HasColumnType("timestamp with time zone");
        builder.Property(i => i.SourceModifiedAt).HasColumnType("timestamp with time zone");

        builder.HasOne<AdminImportRun>()
            .WithMany()
            .HasForeignKey(i => i.ImportRunId)
            .OnDelete(DeleteBehavior.Restrict);

        // The import loop's claim query (run + status=pending) and the counter
        // refresh (GROUP BY status WHERE run) both hit this index.
        builder.HasIndex(i => new { i.ImportRunId, i.Status })
            .HasDatabaseName("ix_admin_import_items_run_status");
        // Discovery-order pagination/keyset. Deliberately NOT an index on
        // RelativePath: a long UTF-8 path could exceed PostgreSQL's btree
        // entry limit; the small int ordinal is safe and stable.
        builder.HasIndex(i => new { i.ImportRunId, i.Ordinal })
            .IsUnique()
            .HasDatabaseName("ux_admin_import_items_run_ordinal");
    }
}
