using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NubArca.Api.Domain.Ai;

namespace NubArca.Api.Data.Configurations.Ai;

public class AiIndexDiagnosticConfiguration : IEntityTypeConfiguration<AiIndexDiagnostic>
{
    public void Configure(EntityTypeBuilder<AiIndexDiagnostic> builder)
    {
        builder.ToTable("ai_index_diagnostics", t =>
        {
            t.HasCheckConstraint(
                "ck_ai_index_diagnostics_attempt_count_non_negative",
                "\"AttemptCount\" >= 0");
        });

        builder.HasKey(d => d.Id);

        builder.Property(d => d.Id)
            .ValueGeneratedNever();

        builder.Property(d => d.Capability)
            .IsRequired()
            .HasMaxLength(64);

        builder.Property(d => d.TargetKind)
            .IsRequired()
            .HasMaxLength(32);

        builder.Property(d => d.ErrorCode)
            .IsRequired()
            .HasMaxLength(100);

        // Short, content-free, truncated. Never paths/text/vectors/SHA/payloads.
        builder.Property(d => d.SanitizedMessage)
            .HasMaxLength(500);

        builder.Property(d => d.OccurredAt)
            .HasColumnType("timestamp with time zone");

        // Aggregate-only query support. The target-id columns
        // (BlobObjectId/DocumentChunkId/FaceDetectionId/OwnerUserId/ProfileId)
        // are deliberately plain correlation ids with NO FK constraints, so a
        // diagnostic never blocks deletion of a blob/file/profile and never
        // couples its lifecycle to them.
        builder.HasIndex(d => new { d.Capability, d.ProfileId, d.ErrorCode })
            .HasDatabaseName("ix_ai_index_diagnostics_capability_profile_error");

        builder.HasIndex(d => new { d.Capability, d.TargetKind })
            .HasDatabaseName("ix_ai_index_diagnostics_capability_target_kind");

        builder.HasIndex(d => new { d.OwnerUserId, d.Capability })
            .HasDatabaseName("ix_ai_index_diagnostics_owner_capability");
    }
}
