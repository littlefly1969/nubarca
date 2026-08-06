using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NubArca.Api.Domain.Ai;

namespace NubArca.Api.Data.Configurations.Ai;

public class AiSettingConfiguration : IEntityTypeConfiguration<AiSetting>
{
    public void Configure(EntityTypeBuilder<AiSetting> builder)
    {
        builder.ToTable("ai_settings");

        builder.HasKey(s => s.Key);
        builder.Property(s => s.Key).HasMaxLength(128).ValueGeneratedNever();
        builder.Property(s => s.Value).IsRequired().HasMaxLength(1024);
        builder.Property(s => s.UpdatedAt).HasColumnType("timestamp with time zone");
        // UpdatedByUserId is a plain audit column (no FK, so a deleted admin never
        // blocks a settings row).
    }
}
