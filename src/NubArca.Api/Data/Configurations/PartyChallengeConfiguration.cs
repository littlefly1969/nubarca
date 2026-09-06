using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NubArca.Api.Domain;

namespace NubArca.Api.Data.Configurations;

public sealed class PartyChallengeConfiguration : IEntityTypeConfiguration<PartyChallenge>
{
    public void Configure(EntityTypeBuilder<PartyChallenge> b)
    {
        b.ToTable("party_challenges", t =>
        {
            t.HasCheckConstraint("ck_party_challenges_kind", "\"Kind\" IN ('dare','penalty','guess','custom')");
            t.HasCheckConstraint("ck_party_challenges_sort_order", "\"SortOrder\" >= 0");
            t.HasCheckConstraint("ck_party_challenges_voting_mode", "\"VotingMode\" IN ('none','binary')");
            t.HasCheckConstraint("ck_party_challenges_duration",
                "\"DurationSeconds\" IS NULL OR (\"DurationSeconds\" >= 5 AND \"DurationSeconds\" <= 3600)");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Title).IsRequired().HasMaxLength(PartyChallengeLimits.MaxTitleLength);
        b.Property(x => x.Body).IsRequired().HasMaxLength(PartyChallengeLimits.MaxBodyLength);
        b.Property(x => x.Kind).IsRequired().HasMaxLength(20);
        b.Property(x => x.IsEnabled).HasDefaultValue(true);
        // Existing activities become ordinary voted ones: a pass/fail verdict is
        // the game's normal shape, so the default is what a host would have
        // chosen anyway.
        b.Property(x => x.VotingMode).IsRequired().HasMaxLength(20)
            .HasDefaultValue(PartyChallengeVotingModes.Binary);
        b.Property(x => x.VoteQuestion).HasMaxLength(PartyChallengeLimits.MaxVoteQuestionLength);
        b.Property(x => x.CreatedAt).HasColumnType("timestamp with time zone");
        b.Property(x => x.UpdatedAt).HasColumnType("timestamp with time zone");
        b.HasIndex(x => new { x.AlbumId, x.SortOrder, x.Id }).HasDatabaseName("ix_party_challenges_album_order");
        b.HasOne<Album>().WithMany().HasForeignKey(x => x.AlbumId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class PartyChallengeVoteConfiguration : IEntityTypeConfiguration<PartyChallengeVote>
{
    public void Configure(EntityTypeBuilder<PartyChallengeVote> b)
    {
        b.ToTable("party_challenge_votes");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.CreatedAt).HasColumnType("timestamp with time zone");
        b.HasIndex(x => new { x.PartyAlbumLinkId, x.PartyParticipantId, x.PartyChallengeId })
            .IsUnique().HasDatabaseName("ux_party_challenge_votes_link_guest_challenge");
        b.HasIndex(x => new { x.PartyAlbumLinkId, x.PartyChallengeId })
            .HasDatabaseName("ix_party_challenge_votes_link_challenge");
        b.HasOne<PartyAlbumLink>().WithMany().HasForeignKey(x => x.PartyAlbumLinkId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<PartyParticipant>().WithMany().HasForeignKey(x => x.PartyParticipantId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<PartyChallenge>().WithMany().HasForeignKey(x => x.PartyChallengeId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class PartyChallengeSessionConfiguration : IEntityTypeConfiguration<PartyChallengeSession>
{
    public void Configure(EntityTypeBuilder<PartyChallengeSession> b)
    {
        b.ToTable("party_challenge_sessions", t =>
            t.HasCheckConstraint("ck_party_challenge_sessions_mode", "\"Mode\" IN ('media','challenge_hold')"));
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Mode).IsRequired().HasMaxLength(20);
        b.Property(x => x.Version).HasDefaultValue(1).IsConcurrencyToken();
        b.Property(x => x.CreatedAt).HasColumnType("timestamp with time zone");
        b.Property(x => x.UpdatedAt).HasColumnType("timestamp with time zone");
        b.HasIndex(x => x.PartyAlbumLinkId).IsUnique().HasDatabaseName("ux_party_challenge_sessions_link");
        b.HasOne<PartyAlbumLink>().WithMany().HasForeignKey(x => x.PartyAlbumLinkId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class PartyChallengeCompletionConfiguration : IEntityTypeConfiguration<PartyChallengeCompletion>
{
    public void Configure(EntityTypeBuilder<PartyChallengeCompletion> b)
    {
        b.ToTable("party_challenge_completions");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.CompletedAt).HasColumnType("timestamp with time zone");
        b.HasIndex(x => new { x.PartyAlbumLinkId, x.PartyChallengeId }).IsUnique()
            .HasDatabaseName("ux_party_challenge_completions_link_challenge");
        b.HasOne<PartyAlbumLink>().WithMany().HasForeignKey(x => x.PartyAlbumLinkId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<PartyChallenge>().WithMany().HasForeignKey(x => x.PartyChallengeId).OnDelete(DeleteBehavior.Restrict);
    }
}
