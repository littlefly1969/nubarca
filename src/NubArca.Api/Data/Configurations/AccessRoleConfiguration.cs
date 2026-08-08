using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NubArca.Api.Domain;

namespace NubArca.Api.Data.Configurations;

public class AccessRoleConfiguration : IEntityTypeConfiguration<AccessRole>
{
    public void Configure(EntityTypeBuilder<AccessRole> builder)
    {
        builder.ToTable("access_roles");

        // The key IS the primary key. `users.RoleKey` references it directly, so
        // there is no surrogate id to keep in step with a second unique column,
        // and a role's identity is the same value in every table that mentions
        // it.
        builder.HasKey(r => r.Key);

        builder.Property(r => r.Key)
            .IsRequired()
            .HasMaxLength(64)
            .ValueGeneratedNever();

        builder.Property(r => r.Name)
            .IsRequired()
            .HasMaxLength(64);

        builder.Property(r => r.Description)
            .HasMaxLength(256);

        builder.Property(r => r.IsSystem).IsRequired();
        builder.Property(r => r.IsAdministrator).IsRequired();

        builder.Property(r => r.CreatedAt)
            .HasColumnType("timestamp with time zone");

        builder.Property(r => r.UpdatedAt)
            .HasColumnType("timestamp with time zone");

        builder.Property(r => r.Version)
            .IsRequired()
            .HasDefaultValue(1);
    }
}

public class RolePermissionConfiguration : IEntityTypeConfiguration<RolePermission>
{
    public void Configure(EntityTypeBuilder<RolePermission> builder)
    {
        builder.ToTable("role_permissions");

        // The pair IS the row: a role either carries a permission or it does
        // not, so the composite key makes a duplicate impossible rather than
        // merely unlikely.
        builder.HasKey(p => new { p.RoleKey, p.PermissionKey });

        builder.Property(p => p.RoleKey)
            .IsRequired()
            .HasMaxLength(64);

        builder.Property(p => p.PermissionKey)
            .IsRequired()
            .HasMaxLength(64);

        // Deleting a role takes its permissions with it; they describe nothing
        // once the role is gone. A role that still has users cannot be deleted
        // at all — see the Restrict on users.RoleKey.
        builder.HasOne<AccessRole>()
            .WithMany()
            .HasForeignKey(p => p.RoleKey)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
