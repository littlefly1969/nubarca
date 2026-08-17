using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NubArca.Api.Domain;

namespace NubArca.Api.Data.Configurations;

public class PartyParticipantConfiguration : IEntityTypeConfiguration<PartyParticipant>
{
    public void Configure(EntityTypeBuilder<PartyParticipant> builder)
    {
        builder.ToTable("party_participants");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedNever();

        // SHA-256 hex.
        builder.Property(p => p.TokenHash).IsRequired().HasMaxLength(64);
        builder.Property(p => p.CreatedAt).HasColumnType("timestamp with time zone");
        builder.Property(p => p.LastSeenAt).HasColumnType("timestamp with time zone");

        // The resolve path is (link, token hash), and it must be UNIQUE: two rows
        // for one token would silently split a participant's counters and hand
        // them a second allowance. Unique on the pair rather than on the hash
        // alone so the same browser can hold independent sessions at two parties.
        builder.HasIndex(p => new { p.PartyAlbumLinkId, p.TokenHash })
            .IsUnique()
            .HasDatabaseName("ux_party_participants_link_token");

        // Restrict, not Cascade: revoking or superseding a party link must not
        // delete the guest-upload provenance that PartyUploadItem points at.
        builder.HasOne<PartyAlbumLink>()
            .WithMany()
            .HasForeignKey(p => p.PartyAlbumLinkId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
