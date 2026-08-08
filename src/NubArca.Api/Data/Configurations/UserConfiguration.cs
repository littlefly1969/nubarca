using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NubArca.Api.Access;
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

        builder.Property(u => u.FirstName)
            .HasMaxLength(100);

        builder.Property(u => u.LastName)
            .HasMaxLength(100);

        builder.Property(u => u.PasswordHash)
            .HasMaxLength(500);

        builder.Property(u => u.CreatedAt)
            .HasColumnType("timestamp with time zone");

        builder.Property(u => u.DisabledAt)
            .HasColumnType("timestamp with time zone");

        // The authoritative authorization source. NOT NULL with a Member
        // default so a row inserted by an older code path can never land
        // without a role; the migration backfilled Administrator for every
        // previous IsAdmin=true account and Member for the rest.
        //
        // 64 characters, matching access_roles.Key exactly — this is a foreign
        // key, and a custom role's generated `custom:<uuid>` key is 39. The
        // column started at 32, when the only possible values were the three
        // built-in names.
        builder.Property(u => u.RoleKey)
            .IsRequired()
            .HasMaxLength(64)
            .HasDefaultValue(RoleKeys.Member);

        // Persisted UI language. NOT NULL with an "it" default so the migration
        // backfills every existing row to Italian. varchar(8) is generous for
        // the two-letter codes; input is validated to UiLanguages.All before it
        // is ever written, so no arbitrary locale string reaches this column.
        builder.Property(u => u.UiLanguage)
            .IsRequired()
            .HasMaxLength(8)
            .HasDefaultValue(UiLanguages.Default);

        // IANA identifier ("Europe/Rome"); validated against TimeZoneInfo before
        // it is written. 64 characters covers every id in the tz database.
        builder.Property(u => u.TimeZone)
            .HasMaxLength(64);

        builder.Property(u => u.LastLoginAt)
            .HasColumnType("timestamp with time zone");

        builder.Property(u => u.PasswordChangedAt)
            .HasColumnType("timestamp with time zone");

        // Starts at 1 rather than 0 so "never bumped" is still a value a cookie
        // can carry and compare, with no zero/absent ambiguity.
        builder.Property(u => u.SecurityVersion)
            .IsRequired()
            .HasDefaultValue(1);

        builder.HasIndex(u => u.Email)
            .IsUnique();

        // Administrator counting runs on every demote/disable guard, so the
        // last-administrator invariant is answered by an index rather than a
        // sequential scan of the user table.
        builder.HasIndex(u => u.RoleKey);

        // A real reference, not a loose string. RESTRICT is the point: a role
        // that still has users cannot be deleted, so "reassign them first" is
        // enforced by the database and not only by the endpoint that happens to
        // check. It also means an account can never reference a role that does
        // not exist, which is what lets permission resolution treat a missing
        // role as impossible rather than guessing a fallback.
        builder.HasOne<AccessRole>()
            .WithMany()
            .HasForeignKey(u => u.RoleKey)
            .HasPrincipalKey(r => r.Key)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
