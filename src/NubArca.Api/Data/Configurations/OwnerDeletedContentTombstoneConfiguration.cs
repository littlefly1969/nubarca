using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NubArca.Api.Domain;

namespace NubArca.Api.Data.Configurations;

public class OwnerDeletedContentTombstoneConfiguration : IEntityTypeConfiguration<OwnerDeletedContentTombstone>
{
    public void Configure(EntityTypeBuilder<OwnerDeletedContentTombstone> builder)
    {
        builder.ToTable("owner_deleted_content_tombstones");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).ValueGeneratedNever();

        builder.Property(t => t.ContentFingerprint).IsRequired().HasMaxLength(128);
        builder.Property(t => t.FingerprintScheme).IsRequired().HasMaxLength(32);
        builder.Property(t => t.LastFileNameSnapshot).HasMaxLength(1024);
        builder.Property(t => t.LastDeletedFromPathSnapshot).HasMaxLength(4096);
        builder.Property(t => t.Source).HasMaxLength(32);

        builder.Property(t => t.FirstDeletedAt).HasColumnType("timestamp with time zone");
        builder.Property(t => t.LastDeletedAt).HasColumnType("timestamp with time zone");
        builder.Property(t => t.CreatedAt).HasColumnType("timestamp with time zone");
        builder.Property(t => t.UpdatedAt).HasColumnType("timestamp with time zone");

        // Owner-scoped uniqueness + the exact lookup shape used by the import
        // skip check (owner + scheme + fingerprint), so the ledger read is a
        // single indexed probe per distinct incoming fingerprint.
        builder.HasIndex(t => new { t.OwnerUserId, t.FingerprintScheme, t.ContentFingerprint })
            .IsUnique()
            .HasDatabaseName("ux_owner_deleted_content_owner_scheme_fingerprint");

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(t => t.OwnerUserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
