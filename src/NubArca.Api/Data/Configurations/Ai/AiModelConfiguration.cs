using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NubArca.Api.Domain.Ai;

namespace NubArca.Api.Data.Configurations.Ai;

public class AiModelConfiguration : IEntityTypeConfiguration<AiModel>
{
    public void Configure(EntityTypeBuilder<AiModel> builder)
    {
        builder.ToTable("ai_models", t =>
        {
            t.HasCheckConstraint(
                "ck_ai_models_version_positive",
                "\"Version\" > 0");
            t.HasCheckConstraint(
                "ck_ai_models_dimension_positive",
                "\"Dimension\" IS NULL OR \"Dimension\" > 0");
        });

        builder.HasKey(m => m.Id);

        builder.Property(m => m.Id)
            .ValueGeneratedNever();

        builder.Property(m => m.Key)
            .IsRequired()
            .HasMaxLength(128);

        builder.Property(m => m.Provider)
            .IsRequired()
            .HasMaxLength(32);

        builder.Property(m => m.Capability)
            .IsRequired()
            .HasMaxLength(64);

        builder.Property(m => m.Modality)
            .IsRequired()
            .HasMaxLength(32);

        builder.Property(m => m.DistanceMetric)
            .HasMaxLength(16);

        builder.Property(m => m.CreatedAt)
            .HasColumnType("timestamp with time zone");

        builder.Property(m => m.UpdatedAt)
            .HasColumnType("timestamp with time zone");

        // Stable model identity — prevents duplicate registry rows.
        builder.HasIndex(m => m.Key)
            .IsUnique()
            .HasDatabaseName("ux_ai_models_key");
    }
}
