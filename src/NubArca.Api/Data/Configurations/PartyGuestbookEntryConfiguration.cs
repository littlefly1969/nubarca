using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NubArca.Api.Domain;

namespace NubArca.Api.Data.Configurations;

public class PartyGuestbookEntryConfiguration : IEntityTypeConfiguration<PartyGuestbookEntry>
{
    public void Configure(EntityTypeBuilder<PartyGuestbookEntry> builder)
    {
        builder.ToTable("party_guestbook_entries", t =>
        {
            // The framing inside the print crop editor's own limits — the same
            // bounds party_guest_contents states — and a photograph with a
            // real shape. The SERVER validates both before writing; these stop
            // a corrupt write from producing a memory nothing can draw.
            t.HasCheckConstraint(
                "ck_party_guestbook_entries_crop",
                "\"CropZoom\" BETWEEN 1 AND 4 AND \"CropCenterX\" BETWEEN 0 AND 1 "
                + "AND \"CropCenterY\" BETWEEN 0 AND 1");
            t.HasCheckConstraint(
                "ck_party_guestbook_entries_photo_size",
                "\"PhotoWidth\" > 0 AND \"PhotoHeight\" > 0");
            t.HasCheckConstraint(
                "ck_party_guestbook_entries_template_version",
                "\"TemplateVersion\" >= 1");
        });
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedNever();

        // The stored bounds are the CODE POINT limits expressed as UTF-16
        // units, for the reason PartyMessageConfiguration states: a varchar
        // bound counts units, and a body of astral characters costs two of them
        // per code point. The real limit is PartyGuestbookText's; these only
        // stop a corrupt write from being unbounded.
        builder.Property(e => e.AuthorDisplayName)
            .IsRequired()
            .HasMaxLength(PartyGuestbookLimits.MaxAuthorDisplayNameLength * 2);
        builder.Property(e => e.Body)
            .IsRequired()
            .HasMaxLength(PartyGuestbookLimits.MaxBodyLength * 2);

        builder.Property(e => e.TemplateKey)
            .IsRequired()
            .HasMaxLength(PartyGuestbookTemplates.MaxKeyLength);

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

        // THE MEMORY'S OWN PHOTOGRAPH. Restrict, like every blob owner: the row
        // holds one reference, counted by BlobReferenceAuditService, and the
        // key is the janitor's last safety net should a count ever drift.
        //
        // There is deliberately NO key to the album file the photograph was
        // chosen from. A memory that cascaded from a FileItem would vanish when
        // the host tidied their album, and one that restricted it would stop
        // the host deleting their own file.
        builder.HasOne<BlobObject>()
            .WithMany()
            .HasForeignKey(e => e.BlobObjectId)
            .OnDelete(DeleteBehavior.Restrict);

        // The regenerable preview, held the same way.
        builder.HasOne<BlobObject>()
            .WithMany()
            .HasForeignKey(e => e.PreviewBlobObjectId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
