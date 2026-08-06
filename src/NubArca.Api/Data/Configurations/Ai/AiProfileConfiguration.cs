using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NubArca.Api.Domain.Ai;

namespace NubArca.Api.Data.Configurations.Ai;

public class AiProfileConfiguration : IEntityTypeConfiguration<AiProfile>
{
    public void Configure(EntityTypeBuilder<AiProfile> builder)
    {
        builder.ToTable("ai_profiles", t =>
        {
            t.HasCheckConstraint(
                "ck_ai_profiles_dimension_positive",
                "\"Dimension\" IS NULL OR \"Dimension\" > 0");
        });

        builder.HasKey(p => p.Id);

        builder.Property(p => p.Id)
            .ValueGeneratedNever();

        builder.Property(p => p.Key)
            .IsRequired()
            .HasMaxLength(128);

        builder.Property(p => p.Capability)
            .IsRequired()
            .HasMaxLength(64);

        builder.Property(p => p.Modality)
            .IsRequired()
            .HasMaxLength(32);

        builder.Property(p => p.DistanceMetric)
            .HasMaxLength(16);

        builder.Property(p => p.ConfigHash)
            .HasMaxLength(128);

        builder.Property(p => p.CreatedAt)
            .HasColumnType("timestamp with time zone");

        builder.Property(p => p.UpdatedAt)
            .HasColumnType("timestamp with time zone");

        builder.HasIndex(p => p.Key)
            .IsUnique()
            .HasDatabaseName("ux_ai_profiles_key");

        builder.HasIndex(p => new { p.Capability, p.IsDefault })
            .HasDatabaseName("ix_ai_profiles_capability_default");

        // At most one default profile per capability. Partial unique index on a
        // bare boolean predicate — valid on both PostgreSQL (boolean column) and
        // SQLite (0/1), exactly like the filtered unique indexes elsewhere.
        builder.HasIndex(p => p.Capability)
            .IsUnique()
            .HasFilter("\"IsDefault\"")
            .HasDatabaseName("ux_ai_profiles_capability_default_active");

        builder.HasOne<AiModel>()
            .WithMany()
            .HasForeignKey(p => p.AiModelId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
