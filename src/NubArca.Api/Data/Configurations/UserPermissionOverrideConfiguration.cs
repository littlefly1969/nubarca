using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NubArca.Api.Domain;

namespace NubArca.Api.Data.Configurations;

public class UserPermissionOverrideConfiguration : IEntityTypeConfiguration<UserPermissionOverride>
{
    public void Configure(EntityTypeBuilder<UserPermissionOverride> builder)
    {
        builder.ToTable("user_permission_overrides");

        builder.HasKey(o => o.Id);

        builder.Property(o => o.Id)
            .ValueGeneratedNever();

        builder.Property(o => o.PermissionKey)
            .IsRequired()
            .HasMaxLength(64);

        // Stored as its name. A row read straight out of psql says "Deny", not
        // "1", and adding a member to the enum later cannot renumber an
        // existing row's meaning.
        builder.Property(o => o.Effect)
            .IsRequired()
            .HasMaxLength(16)
            .HasConversion<string>();

        builder.Property(o => o.CreatedAt)
            .HasColumnType("timestamp with time zone");

        builder.Property(o => o.UpdatedAt)
            .HasColumnType("timestamp with time zone");

        // One override per (user, permission) — the constraint IS the semantics:
        // an override answers a single question and cannot hold both a Grant and
        // a Deny for the same key.
        builder.HasIndex(o => new { o.UserId, o.PermissionKey })
            .IsUnique();

        // Deleting a user takes their overrides with them; they describe nothing
        // once the account is gone.
        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(o => o.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
