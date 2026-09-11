using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NubArca.Api.Domain;

namespace NubArca.Api.Data.Configurations;

public class PartyConfiguration : IEntityTypeConfiguration<Domain.Party>
{
    public void Configure(EntityTypeBuilder<Domain.Party> builder)
    {
        builder.ToTable("parties");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedNever();

        builder.Property(p => p.Title).IsRequired().HasMaxLength(200);
        builder.Property(p => p.Description).HasMaxLength(2000);

        // Short status token, generously bounded so a future state can never
        // overflow the column — the same shape as party_messages.Status.
        builder.Property(p => p.Status)
            .IsRequired()
            .HasMaxLength(32)
            .HasDefaultValue(PartyStatuses.Draft);

        // Starts at 1 so a client can never send 0 and accidentally match, the
        // same contract the album's version carries.
        builder.Property(p => p.Version).HasDefaultValue(1);

        builder.Property(p => p.EventStartsAt).HasColumnType("timestamp with time zone");
        builder.Property(p => p.LiveStartedAt).HasColumnType("timestamp with time zone");
        builder.Property(p => p.LiveEndedAt).HasColumnType("timestamp with time zone");
        builder.Property(p => p.GuestAccessExpiresAt).HasColumnType("timestamp with time zone");
        builder.Property(p => p.LibraryAccessExpiresAt).HasColumnType("timestamp with time zone");
        builder.Property(p => p.CreatedAt).HasColumnType("timestamp with time zone");
        builder.Property(p => p.UpdatedAt).HasColumnType("timestamp with time zone");

        // "My parties, newest first" is the only listing shape the owner surface
        // has, so it is the only index the root needs.
        builder.HasIndex(p => new { p.OwnerUserId, p.CreatedAt })
            .HasDatabaseName("ix_parties_owner_created");

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(p => p.OwnerUserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public class PartyMediaSourceConfiguration : IEntityTypeConfiguration<PartyMediaSource>
{
    public void Configure(EntityTypeBuilder<PartyMediaSource> builder)
    {
        builder.ToTable("party_media_sources");

        // The composite key IS the uniqueness rule the model needs: one album
        // contributes to one party once. Expressing it as the key rather than as
        // a separate surrogate id plus a unique index means there is no second
        // way to write the same fact.
        builder.HasKey(s => new { s.PartyId, s.AlbumId });

        builder.Property(s => s.Role).IsRequired().HasMaxLength(32);
        builder.Property(s => s.CreatedAt).HasColumnType("timestamp with time zone");

        // The public seam's query: this party's sources, by role, in host order.
        builder.HasIndex(s => new { s.PartyId, s.Role, s.SortOrder })
            .HasDatabaseName("ix_party_media_sources_party_role");

        // ONE album is ONE party's `main` source.
        //
        // The composite key already says an album contributes to a party once;
        // this says it contributes to at most one party IN THAT ROLE, which is
        // the invariant `EnsureForAlbumAsync` has always needed a single answer
        // to. Enforced by the DATABASE rather than by an `AnyAsync` check the
        // application performs first, so two concurrent attempts to claim the
        // same album cannot both find it free and both proceed.
        //
        // Keyed on Role rather than hard-coded to `main` so the future roles the
        // table exists for — `official`, `guest-contributions`,
        // `selected-memories` — each get the same rule for free, and none of
        // them needs a database enum to do it.
        builder.HasIndex(s => new { s.AlbumId, s.Role })
            .IsUnique()
            .HasDatabaseName("ux_party_media_sources_album_role");

        builder.HasOne<Domain.Party>()
            .WithMany()
            .HasForeignKey(s => s.PartyId)
            .OnDelete(DeleteBehavior.Restrict);

        // Restrict, like every other Party foreign key to an album: deleting an
        // album must state out loud what party state goes with it (AlbumService)
        // rather than have rows disappear through a cascade nobody reads.
        builder.HasOne<Album>()
            .WithMany()
            .HasForeignKey(s => s.AlbumId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public class PartyGuestContentConfiguration : IEntityTypeConfiguration<PartyGuestContent>
{
    public void Configure(EntityTypeBuilder<PartyGuestContent> builder)
    {
        builder.ToTable("party_guest_contents", t =>
            // The presentation vocabulary, held by the DATABASE as well as by
            // the validator. Two values is a closed set, and a closed set that
            // only the application enforces is one bad write away from a
            // surface that cannot render its own row.
            t.HasCheckConstraint(
                "ck_party_guest_contents_media_presentation",
                "\"MediaPresentation\" IN ('inline', 'poster')"));

        // The composite key IS the "at most one slot per kind" rule. Expressing
        // it as the key rather than as a surrogate id plus a unique index means
        // there is no second way to write the same fact — and no id for a client
        // to address a slot by instead of naming what it is.
        builder.HasKey(c => new { c.PartyId, c.Kind });

        builder.Property(c => c.Kind).IsRequired().HasMaxLength(32);
        builder.Property(c => c.Enabled).HasDefaultValue(false);
        builder.Property(c => c.VisibleBefore).HasDefaultValue(false);
        builder.Property(c => c.VisibleLive).HasDefaultValue(false);
        builder.Property(c => c.VisibleAfter).HasDefaultValue(false);

        // Bounded generously. The real limits are enforced per FIELD by
        // PartyGuestContentPayload before anything is stored; this only stops a
        // corrupt write from being unbounded.
        builder.Property(c => c.ContentJson).IsRequired().HasMaxLength(16_384);

        // NOT NULL with an 'inline' default, which is the whole upgrade story:
        // every row written before this column existed means exactly what it
        // rendered as, and an older application that never mentions the column
        // still writes rows the constraint accepts.
        builder.Property(c => c.MediaPresentation)
            .IsRequired()
            .HasMaxLength(16)
            .HasDefaultValue(PartyGuestContentMediaPresentations.Inline);

        builder.Property(c => c.Version).HasDefaultValue(1);
        builder.Property(c => c.CreatedAt).HasColumnType("timestamp with time zone");
        builder.Property(c => c.UpdatedAt).HasColumnType("timestamp with time zone");

        // Restrict, like every other Party foreign key: what a delete takes with
        // it is stated out loud in AlbumService rather than left to a cascade
        // nobody reads.
        builder.HasOne<Domain.Party>()
            .WithMany()
            .HasForeignKey(c => c.PartyId)
            .OnDelete(DeleteBehavior.Restrict);

        // The slot's photograph is a REFERENCE to the owner's ordinary file, and
        // unlike the party's own rows it gives way to that file's lifecycle
        // instead of blocking it. Every purge trigger ends in one DELETE of the
        // file row, and SET NULL is what lets that DELETE succeed while the slot
        // keeps its words. Trash and the Private Vault leave the id in place;
        // they are enforced where the bytes are served.
        builder.HasOne<FileItem>()
            .WithMany()
            .HasForeignKey(c => c.MediaFileItemId)
            .OnDelete(DeleteBehavior.SetNull);
        builder.HasIndex(c => c.MediaFileItemId)
            .HasDatabaseName("ix_party_guest_contents_media_file");
    }
}
