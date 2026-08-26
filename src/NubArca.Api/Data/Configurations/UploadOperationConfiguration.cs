using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NubArca.Api.Domain;

namespace NubArca.Api.Data.Configurations;

public class UploadOperationConfiguration : IEntityTypeConfiguration<UploadOperation>
{
    public void Configure(EntityTypeBuilder<UploadOperation> builder)
    {
        builder.ToTable("upload_operations");
        builder.HasKey(o => o.Id);
        builder.Property(o => o.Id).ValueGeneratedNever();

        builder.Property(o => o.OperationKey)
            .IsRequired()
            .HasMaxLength(128);

        builder.Property(o => o.Status)
            .IsRequired()
            .HasMaxLength(16);

        builder.Property(o => o.CreatedAt).HasColumnType("timestamp with time zone");
        builder.Property(o => o.LeaseExpiresAt).HasColumnType("timestamp with time zone");

        // THE arbitration primitive: one outstanding operation per (owner, key).
        // Including the owner in the key's uniqueness is what keeps account A's
        // replay namespace disjoint from account B's.
        builder.HasIndex(o => new { o.OwnerUserId, o.OperationKey })
            .IsUnique()
            .HasDatabaseName("ux_upload_operations_owner_key");

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(o => o.OwnerUserId)
            .OnDelete(DeleteBehavior.Restrict);

        // When the produced file is permanently purged, the operation no longer
        // reconstructs anything and must stop claiming otherwise. Soft deletes
        // are unaffected (only DeletedAt changes), so a replay of a completed
        // operation whose file was soft-deleted is handled by the lookup, which
        // drops the row instead of resurrecting the deleted file.
        builder.HasOne<FileItem>()
            .WithMany()
            .HasForeignKey(o => o.FileItemId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}