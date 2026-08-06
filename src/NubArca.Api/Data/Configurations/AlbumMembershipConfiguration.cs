using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NubArca.Api.Domain;

namespace NubArca.Api.Data.Configurations;

public class AlbumMembershipConfiguration : IEntityTypeConfiguration<AlbumMembership>
{
    public void Configure(EntityTypeBuilder<AlbumMembership> builder)
    {
        builder.ToTable("album_memberships", t =>
        {
            t.HasCheckConstraint(
                "ck_album_memberships_role",
                "\"Role\" IN ('viewer', 'contributor', 'editor')");
            t.HasCheckConstraint(
                "ck_album_memberships_state",
                "\"State\" IN ('pending', 'accepted', 'declined', 'revoked')");
        });

        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id).ValueGeneratedNever();

        builder.Property(m => m.Role).IsRequired().HasMaxLength(32);
        builder.Property(m => m.State).IsRequired().HasMaxLength(32);
        builder.Property(m => m.AllowOriginalDownload).HasDefaultValue(false);
        builder.Property(m => m.InvitedAt).HasColumnType("timestamp with time zone");
        builder.Property(m => m.AcceptedAt).HasColumnType("timestamp with time zone");
        builder.Property(m => m.DeclinedAt).HasColumnType("timestamp with time zone");
        builder.Property(m => m.RevokedAt).HasColumnType("timestamp with time zone");
        builder.Property(m => m.UpdatedAt).HasColumnType("timestamp with time zone");

        // Concurrency-safe uniqueness for (album, member). A PLAIN unique index,
        // not a partial one: two simultaneous invitations for the same recipient
        // must collide at the database regardless of what state either row is
        // in, and a plain index behaves identically on PostgreSQL and on the
        // SQLite the endpoint tests run against. Re-invite reuses the row.
        builder.HasIndex(m => new { m.AlbumId, m.MemberUserId })
            .IsUnique()
            .HasDatabaseName("ux_album_memberships_album_member");

        // "Which albums are shared with me / which invitations am I holding" —
        // the index behind every /api/shared-albums listing.
        builder.HasIndex(m => new { m.MemberUserId, m.State })
            .HasDatabaseName("ix_album_memberships_member_state");

        // "Who is this album shared with" — the owner-side member list.
        builder.HasIndex(m => new { m.AlbumId, m.State })
            .HasDatabaseName("ix_album_memberships_album_state");

        builder.HasOne<Album>()
            .WithMany()
            .HasForeignKey(m => m.AlbumId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(m => m.MemberUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(m => m.InvitedByUserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
