using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NubArca.Api.Domain;
using NubArca.Api.Domain.Ai;

namespace NubArca.Api.Data.Configurations.Ai;

public class BlobAiArtifactStatusConfiguration : IEntityTypeConfiguration<BlobAiArtifactStatus>
{
    public void Configure(EntityTypeBuilder<BlobAiArtifactStatus> builder)
    {
        builder.ToTable("blob_ai_artifact_statuses", t =>
        {
            t.HasCheckConstraint(
                "ck_blob_ai_artifact_statuses_attempt_count_non_negative",
                "\"AttemptCount\" >= 0");
        });

        builder.HasKey(s => s.Id);

        builder.Property(s => s.Id)
            .ValueGeneratedNever();

        builder.Property(s => s.Capability)
            .IsRequired()
            .HasMaxLength(64);

        builder.Property(s => s.Status)
            .IsRequired()
            .HasMaxLength(32);

        builder.Property(s => s.ErrorCode)
            .HasMaxLength(100);

        builder.Property(s => s.CreatedAt)
            .HasColumnType("timestamp with time zone");

        builder.Property(s => s.UpdatedAt)
            .HasColumnType("timestamp with time zone");

        // Sparse table: exactly one terminal row per (blob, profile, capability).
        // A missing row is implicit pending — rows are never pre-materialized.
        builder.HasIndex(s => new { s.BlobObjectId, s.ProfileId, s.Capability })
            .IsUnique()
            .HasDatabaseName("ux_blob_ai_artifact_statuses_blob_profile_capability");

        // Coverage / backfill-cursor support.
        builder.HasIndex(s => new { s.ProfileId, s.Status })
            .HasDatabaseName("ix_blob_ai_artifact_statuses_profile_status");

        // Derived data dies with its source blob: Cascade keeps the blob janitor
        // working with no janitor code change once rows exist in later phases.
        builder.HasOne<BlobObject>()
            .WithMany()
            .HasForeignKey(s => s.BlobObjectId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<AiProfile>()
            .WithMany()
            .HasForeignKey(s => s.ProfileId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
