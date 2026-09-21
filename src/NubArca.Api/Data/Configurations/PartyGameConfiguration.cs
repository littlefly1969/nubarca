using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NubArca.Api.Domain;

namespace NubArca.Api.Data.Configurations;

public sealed class PartyGameSessionConfiguration : IEntityTypeConfiguration<PartyGameSession>
{
    public void Configure(EntityTypeBuilder<PartyGameSession> b)
    {
        b.ToTable("party_game_sessions", t =>
        {
            t.HasCheckConstraint("ck_party_game_sessions_status",
                "\"Status\" IN ('lobby','live','finished')");
            t.HasCheckConstraint("ck_party_game_sessions_phase",
                "\"Phase\" IN ('lobby','challenge_reveal','challenge_active','voting_open','voting_closed','result','intermission','finished')");
            t.HasCheckConstraint("ck_party_game_sessions_round_number", "\"CurrentRoundNumber\" >= 0");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Status).IsRequired().HasMaxLength(20);
        b.Property(x => x.Phase).IsRequired().HasMaxLength(30);

        // The optimistic-concurrency authority. Two owner devices sending a
        // command at the same instant both pass the expectedVersion check in
        // memory; this is what makes exactly one of them win at the database.
        b.Property(x => x.Version).HasDefaultValue(1).IsConcurrencyToken();

        b.Property(x => x.CreatedAt).HasColumnType("timestamp with time zone");
        b.Property(x => x.UpdatedAt).HasColumnType("timestamp with time zone");
        b.Property(x => x.StartedAt).HasColumnType("timestamp with time zone");
        b.Property(x => x.FinishedAt).HasColumnType("timestamp with time zone");

        // One game per party link. A re-enabled party mints a new link, so a new
        // party genuinely is a new game; this index is what stops two concurrent
        // `start` commands from creating two.
        b.HasIndex(x => x.PartyAlbumLinkId).IsUnique()
            .HasDatabaseName("ux_party_game_sessions_link");
        b.HasIndex(x => x.AlbumId).HasDatabaseName("ix_party_game_sessions_album");

        b.HasOne<PartyAlbumLink>().WithMany().HasForeignKey(x => x.PartyAlbumLinkId)
            .OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Album>().WithMany().HasForeignKey(x => x.AlbumId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class PartyGameRoundConfiguration : IEntityTypeConfiguration<PartyGameRound>
{
    public void Configure(EntityTypeBuilder<PartyGameRound> b)
    {
        b.ToTable("party_game_rounds", t =>
        {
            t.HasCheckConstraint("ck_party_game_rounds_status",
                "\"Status\" IN ('active','completed','abandoned')");
            t.HasCheckConstraint("ck_party_game_rounds_sequence", "\"Sequence\" >= 1");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Status).IsRequired().HasMaxLength(20);
        b.Property(x => x.StartedAt).HasColumnType("timestamp with time zone");
        b.Property(x => x.PhaseStartedAt).HasColumnType("timestamp with time zone");
        b.Property(x => x.PhaseEndsAt).HasColumnType("timestamp with time zone");
        b.Property(x => x.CompletedAt).HasColumnType("timestamp with time zone");

        // The sequence is the room's experience of the evening, so it is unique
        // per session rather than merely indexed.
        b.HasIndex(x => new { x.PartyGameSessionId, x.Sequence }).IsUnique()
            .HasDatabaseName("ux_party_game_rounds_session_sequence");

        // An activity is played at most once per game. This is what "no repeat"
        // means when it is a database fact rather than a query the picker
        // remembers to write.
        b.HasIndex(x => new { x.PartyGameSessionId, x.PartyChallengeId }).IsUnique()
            .HasDatabaseName("ux_party_game_rounds_session_challenge");

        b.HasOne<PartyGameSession>().WithMany().HasForeignKey(x => x.PartyGameSessionId)
            .OnDelete(DeleteBehavior.Cascade);
        b.HasOne<PartyChallenge>().WithMany().HasForeignKey(x => x.PartyChallengeId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class PartyGameVoteConfiguration : IEntityTypeConfiguration<PartyGameVote>
{
    public void Configure(EntityTypeBuilder<PartyGameVote> b)
    {
        // AN ANSWER IS A VERDICT OR AN OPTION ID. A binary round is answered
        // with one of two words; a choice round is answered with the id of one
        // of the activity's own options, and WHICH ids are acceptable is a
        // question about that activity — so the constraint checks the SHAPE and
        // the service checks the membership. `length` rather than a uuid regex
        // because the same constraint has to hold on PostgreSQL and on the
        // SQLite the tests run against.
        b.ToTable("party_game_votes", t =>
            t.HasCheckConstraint("ck_party_game_votes_value",
                "\"Value\" IN ('yes','no') OR length(\"Value\") = 36"));
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Value).IsRequired().HasMaxLength(36);
        b.Property(x => x.CreatedAt).HasColumnType("timestamp with time zone");
        b.Property(x => x.UpdatedAt).HasColumnType("timestamp with time zone");

        // THE integrity constraint. One current choice per guest per round, held
        // by the database rather than by whichever code path remembered to
        // check: two taps arriving together cannot both insert, and a guest who
        // changes their mind updates the row they already have.
        b.HasIndex(x => new { x.PartyGameRoundId, x.PartyParticipantId }).IsUnique()
            .HasDatabaseName("ux_party_game_votes_round_participant");
        b.HasIndex(x => new { x.PartyGameRoundId, x.Value })
            .HasDatabaseName("ix_party_game_votes_round_value");

        b.HasOne<PartyGameSession>().WithMany().HasForeignKey(x => x.PartyGameSessionId)
            .OnDelete(DeleteBehavior.Cascade);
        b.HasOne<PartyGameRound>().WithMany().HasForeignKey(x => x.PartyGameRoundId)
            .OnDelete(DeleteBehavior.Cascade);
        b.HasOne<PartyParticipant>().WithMany().HasForeignKey(x => x.PartyParticipantId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class PartyGameExclusionConfiguration : IEntityTypeConfiguration<PartyGameExclusion>
{
    public void Configure(EntityTypeBuilder<PartyGameExclusion> b)
    {
        b.ToTable("party_game_exclusions");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.CreatedAt).HasColumnType("timestamp with time zone");

        // "Not in this match" is a fact, not a quantity. Two control-room
        // devices excluding the same activity at once must leave one row, and
        // the database is what says so rather than a check the planner performs
        // and then hopes still holds.
        b.HasIndex(x => new { x.PartyGameSessionId, x.PartyChallengeId }).IsUnique()
            .HasDatabaseName("ux_party_game_exclusions_session_challenge");

        b.HasOne<PartyGameSession>().WithMany().HasForeignKey(x => x.PartyGameSessionId)
            .OnDelete(DeleteBehavior.Cascade);
        // Restricting, exactly as a round's is: deleting an activity a host has
        // deliberately set aside would silently rewrite the plan.
        b.HasOne<PartyChallenge>().WithMany().HasForeignKey(x => x.PartyChallengeId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
