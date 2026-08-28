using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NubArca.Api.Domain;

namespace NubArca.Api.Data.Configurations;

public class PartyMessageConfiguration : IEntityTypeConfiguration<PartyMessage>
{
    public void Configure(EntityTypeBuilder<PartyMessage> builder)
    {
        builder.ToTable("party_messages");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedNever();

        // The stored lengths are the CODE POINT limits expressed as UTF-16
        // units, which is what a varchar bound counts: a 120-code-point body of
        // astral characters is 240 units. The real limit is enforced in
        // PartyMessageText; these bounds only stop a corrupt write from being
        // unbounded, so they are deliberately generous rather than exact.
        builder.Property(p => p.DisplayName)
            .HasMaxLength(PartyMessageLimits.MaxDisplayNameLength * 2);
        builder.Property(p => p.Body)
            .IsRequired()
            .HasMaxLength(PartyMessageLimits.MaxBodyLength * 2);

        // Short status token. Generous bound so a future state can never
        // overflow the column, matching party_upload_items.
        builder.Property(p => p.Status).IsRequired().HasMaxLength(32);

        builder.Property(p => p.CreatedAt).HasColumnType("timestamp with time zone");
        builder.Property(p => p.UpdatedAt).HasColumnType("timestamp with time zone");
        builder.Property(p => p.ModeratedAt).HasColumnType("timestamp with time zone");
        builder.Property(p => p.HeroPromotedAt).HasColumnType("timestamp with time zone");

        // Computed on read; never a column.
        builder.Ignore(p => p.IsPresentable);
        builder.Ignore(p => p.IsHero);

        // The live feed: one party, the visible ones, oldest-to-newest. Serves
        // both the TV ribbon poll (every 5s) and the owner queue's status filter.
        builder.HasIndex(p => new { p.PartyAlbumLinkId, p.Status, p.CreatedAt })
            .HasDatabaseName("ix_party_messages_link_status_created");

        // The Hero rotation, ordered by when each promotion happened.
        builder.HasIndex(p => new { p.PartyAlbumLinkId, p.HeroPromotedAt })
            .HasDatabaseName("ix_party_messages_link_hero_promoted");

        // The party the message was written at. Restrict, not Cascade: the link
        // row is the event's identity and is never deleted in normal operation
        // (party mode is revoked, not erased), so a cascade here would only ever
        // fire on an administrative delete and would silently take the evidence
        // of what was said with it.
        builder.HasOne<PartyAlbumLink>()
            .WithMany()
            .HasForeignKey(p => p.PartyAlbumLinkId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Album>()
            .WithMany()
            .HasForeignKey(p => p.AlbumId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(p => p.OwnerUserId)
            .OnDelete(DeleteBehavior.Restrict);

        // Provenance only, same as party_upload_items: removing a participant
        // must never delete what they wrote.
        builder.HasOne<PartyParticipant>()
            .WithMany()
            .HasForeignKey(p => p.PartyParticipantId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
