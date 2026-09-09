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

        // "Which party is this album already part of" — the compatibility entry
        // point's lookup, and the album delete cascade's.
        builder.HasIndex(s => s.AlbumId)
            .HasDatabaseName("ix_party_media_sources_album");

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
