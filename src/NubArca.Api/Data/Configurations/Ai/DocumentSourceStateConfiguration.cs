using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NubArca.Api.Domain;
using NubArca.Api.Domain.Ai;

namespace NubArca.Api.Data.Configurations.Ai;

public class DocumentSourceStateConfiguration : IEntityTypeConfiguration<DocumentSourceState>
{
    public void Configure(EntityTypeBuilder<DocumentSourceState> builder)
    {
        builder.ToTable("document_source_states", t =>
        {
            t.HasCheckConstraint(
                "ck_document_source_states_attempts_non_negative",
                "\"Attempts\" >= 0");
        });

        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();

        builder.Property(s => s.SourceKind).IsRequired().HasMaxLength(16);
        builder.Property(s => s.Phase).IsRequired().HasMaxLength(16);
        builder.Property(s => s.Status).IsRequired().HasMaxLength(16);
        builder.Property(s => s.ReasonCode).HasMaxLength(100);
        builder.Property(s => s.SourceRevision).IsRequired().HasMaxLength(128);

        builder.Property(s => s.CreatedAt).HasColumnType("timestamp with time zone");
        builder.Property(s => s.UpdatedAt).HasColumnType("timestamp with time zone");
        builder.Property(s => s.StartedAt).HasColumnType("timestamp with time zone");
        builder.Property(s => s.CompletedAt).HasColumnType("timestamp with time zone");

        // ONE ROW PER (source, phase). The row is updated in place as the
        // source moves through revisions; history is not this table's job.
        builder.HasIndex(s => new { s.SourceKind, s.SourceId, s.Phase })
            .IsUnique()
            .HasDatabaseName("ux_document_source_states_source_phase");

        // The progress view's shape: one owner's counts per phase and status.
        builder.HasIndex(s => new { s.OwnerUserId, s.Phase, s.Status })
            .HasDatabaseName("ix_document_source_states_owner_phase_status");

        // Deliberately no foreign key on SourceId: it is polymorphic across
        // source kinds. A row whose source has gone is inert — every count
        // joins live eligibility — and is housekeeping, not a boundary.
        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(s => s.OwnerUserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
