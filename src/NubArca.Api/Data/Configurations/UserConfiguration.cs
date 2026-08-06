using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NubArca.Api.Domain;

namespace NubArca.Api.Data.Configurations;

public class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("users");

        builder.HasKey(u => u.Id);

        builder.Property(u => u.Id)
            .ValueGeneratedNever();

        builder.Property(u => u.Email)
            .IsRequired()
            .HasMaxLength(320);

        builder.Property(u => u.DisplayName)
            .IsRequired()
            .HasMaxLength(200);

        builder.Property(u => u.PasswordHash)
            .HasMaxLength(500);

        builder.Property(u => u.CreatedAt)
            .HasColumnType("timestamp with time zone");

        builder.Property(u => u.DisabledAt)
            .HasColumnType("timestamp with time zone");

        // Minimal operator marker (slice 46). NOT NULL with a default so the
        // migration backfills every existing row to `false`. Tighten with a
        // proper RBAC table when more than one role becomes interesting.
        builder.Property(u => u.IsAdmin)
            .IsRequired()
            .HasDefaultValue(false);

        // Persisted UI language. NOT NULL with an "it" default so the migration
        // backfills every existing row to Italian. varchar(8) is generous for
        // the two-letter codes; input is validated to UiLanguages.All before it
        // is ever written, so no arbitrary locale string reaches this column.
        builder.Property(u => u.UiLanguage)
            .IsRequired()
            .HasMaxLength(8)
            .HasDefaultValue(UiLanguages.Default);

        builder.HasIndex(u => u.Email)
            .IsUnique();
    }
}
