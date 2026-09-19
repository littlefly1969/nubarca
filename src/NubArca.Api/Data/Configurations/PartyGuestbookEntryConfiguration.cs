using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NubArca.Api.Domain;

namespace NubArca.Api.Data.Configurations;

public class PartyGuestbookEntryConfiguration : IEntityTypeConfiguration<PartyGuestbookEntry>
{
    public void Configure(EntityTypeBuilder<PartyGuestbookEntry> builder)
    {
        builder.ToTable("party_guestbook_entries");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedNever();

        // The stored bounds are the CODE POINT limits expressed as UTF-16
        // units, for the reason PartyMessageConfiguration states: a varchar
        // bound counts units, and a body of astral characters costs two of them
        // per code point. The real limit is PartyGuestbookText's; these only
        // stop a corrupt write from being unbounded.
        builder.Property(e => e.AuthorDisplayName)
            .HasMaxLength(PartyGuestbookLimits.MaxAuthorDisplayNameLength * 2);
        builder.Property(e => e.Body)
            .IsRequired()
            .HasMaxLength(PartyGuestbookLimits.MaxBodyLength * 2);

        builder.Property(e => e.Status).IsRequired().HasMaxLength(32);

        builder.Property(e => e.CreatedAt).HasColumnType("timestamp with time zone");
        builder.Property(e => e.UpdatedAt).HasColumnType("timestamp with time zone");
        builder.Property(e => e.ModeratedAt).HasColumnType("timestamp with time zone");

        builder.Ignore(e => e.IsPublic);

        // The book as a guest reads it: this party, the visible ones, newest
        // first. Also serves the manager queue, which filters the same two
        // columns and orders on the third.
        builder.HasIndex(e => new { e.PartyId, e.Status, e.CreatedAt })
            .HasDatabaseName("ix_party_guestbook_party_status_created");

        // The book in submission order, for the manager's unfiltered listing
        // and for an export that does not care about state.
        builder.HasIndex(e => new { e.PartyId, e.CreatedAt })
            .HasDatabaseName("ix_party_guestbook_party_created");

        // The event the book belongs to. Restrict, like every other Party
        // table: tearing a party down is an explicit operation that walks its
        // rows (PartyStateEraser), not a cascade nobody can see.
        builder.HasOne<Domain.Party>()
            .WithMany()
            .HasForeignKey(e => e.PartyId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(e => e.OwnerUserId)
            .OnDelete(DeleteBehavior.Restrict);

        // Provenance only, and nullable: the QR an entry arrived on may be
        // revoked and re-minted many times over one evening, and none of that
        // is allowed to touch the book.
        builder.HasOne<PartyAlbumLink>()
            .WithMany()
            .HasForeignKey(e => e.PartyAlbumLinkId)
            .OnDelete(DeleteBehavior.Restrict);

        // Provenance only, same as party_messages: removing a participant must
        // never delete what they wrote.
        builder.HasOne<PartyParticipant>()
            .WithMany()
            .HasForeignKey(e => e.PartyParticipantId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
