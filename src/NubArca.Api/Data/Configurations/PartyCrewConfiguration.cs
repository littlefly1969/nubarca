using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NubArca.Api.Domain;
using NubArca.Api.Party;

namespace NubArca.Api.Data.Configurations;

// Party Crew's six tables.
//
// Every foreign key is Restrict, like every other Party table: what a teardown
// takes with it is stated out loud in PartyStateEraser rather than left to a
// cascade nobody reads.
//
// Closed vocabularies — the role, the capability — are held by the DATABASE as
// well as by the validators. A service bug that tried to write an unknown
// capability would otherwise create authority nobody can explain, and the row
// would look exactly like a legitimate one.
//
// Every hash column is checked for length 64. A shorter value is not a SHA-256
// of anything, and a credential table is the wrong place to find that out late.

public sealed class PartyCollaboratorConfiguration : IEntityTypeConfiguration<PartyCollaborator>
{
    public void Configure(EntityTypeBuilder<PartyCollaborator> builder)
    {
        builder.ToTable("party_collaborators", t =>
        {
            t.HasCheckConstraint(
                "ck_party_collaborators_role",
                "\"RoleKey\" IN ('co_organizer', 'director', 'dj', 'reception', 'honoree')");
        });
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).ValueGeneratedNever();

        builder.Property(c => c.DisplayName).IsRequired()
            .HasMaxLength(PartyCrewLimits.MaxDisplayNameLength);
        builder.Property(c => c.Email).IsRequired().HasMaxLength(PartyCrewLimits.MaxEmailLength);
        builder.Property(c => c.NormalizedEmail).IsRequired()
            .HasMaxLength(PartyCrewLimits.MaxEmailLength);
        builder.Property(c => c.RoleKey).IsRequired().HasMaxLength(32);
        // A CONCURRENCY TOKEN, not just a number the service compares.
        //
        // Reading the version and checking it in memory protects the sequential
        // case only: two requests holding the same stale form both read 1, both
        // pass, and both write. The damage is not a lost display name — it is
        // that a role and its grants are written by different statements, so
        // the loser can leave `RoleKey = director` standing over a
        // co-organizer's capability rows, and authorisation reads the ROWS.
        //
        // The mutation path takes this row's write lock first and re-reads
        // inside it, which is what actually orders the two. This is the second
        // line: EF puts the original value in the UPDATE's WHERE, so the write
        // fails rather than silently winning if that lock is ever lost.
        builder.Property(c => c.Version).HasDefaultValue(1).IsConcurrencyToken();
        builder.Property(c => c.CreatedAt).HasColumnType("timestamp with time zone");
        builder.Property(c => c.UpdatedAt).HasColumnType("timestamp with time zone");
        builder.Property(c => c.RevokedAt).HasColumnType("timestamp with time zone");

        // The owner's list for one party, oldest first.
        builder.HasIndex(c => new { c.PartyId, c.CreatedAt })
            .HasDatabaseName("ix_party_collaborators_party_created");

        // One live collaborator per address per party. Two rows for the same
        // person would mean two codes to the same inbox and four devices where
        // the product promises two. Filtered, so a revoked collaborator does not
        // block re-inviting the same person later.
        builder.HasIndex(c => new { c.PartyId, c.NormalizedEmail })
            .IsUnique()
            .HasFilter("\"RevokedAt\" IS NULL")
            .HasDatabaseName("ux_party_collaborators_party_email_live");

        builder.HasOne<Domain.Party>()
            .WithMany()
            .HasForeignKey(c => c.PartyId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class PartyCollaboratorGrantConfiguration : IEntityTypeConfiguration<PartyCollaboratorGrant>
{
    public void Configure(EntityTypeBuilder<PartyCollaboratorGrant> builder)
    {
        builder.ToTable("party_collaborator_grants", t =>
        {
            // The capability vocabulary, in the database. A service that tried
            // to invent authority is refused here rather than believed.
            t.HasCheckConstraint(
                "ck_party_collaborator_grants_capability",
                "\"CapabilityKey\" IN ("
                + "'party-crew.details.manage', 'party-crew.lifecycle.manage', "
                + "'party-crew.experience.manage', 'party-crew.guests.read', "
                + "'party-crew.invitations.manage', 'party-crew.attendance.manage', "
                + "'party-crew.contributions.configure', 'party-crew.contributions.moderate', "
                + "'party-crew.activities.manage', 'party-crew.activities.control', "
                + "'party-crew.screens.manage', 'party-crew.print.manage')");
        });
        builder.HasKey(g => g.Id);
        builder.Property(g => g.Id).ValueGeneratedNever();
        builder.Property(g => g.CapabilityKey).IsRequired().HasMaxLength(64);
        builder.Property(g => g.CreatedAt).HasColumnType("timestamp with time zone");

        builder.HasIndex(g => new { g.PartyCollaboratorId, g.CapabilityKey })
            .IsUnique()
            .HasDatabaseName("ux_party_collaborator_grants_collaborator_capability");

        builder.HasOne<PartyCollaborator>()
            .WithMany()
            .HasForeignKey(g => g.PartyCollaboratorId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class PartyCollaboratorInviteConfiguration : IEntityTypeConfiguration<PartyCollaboratorInvite>
{
    public void Configure(EntityTypeBuilder<PartyCollaboratorInvite> builder)
    {
        builder.ToTable("party_collaborator_invites", t =>
        {
            t.HasCheckConstraint(
                "ck_party_collaborator_invites_token_hash",
                "length(\"TokenHash\") = 64");
        });
        builder.HasKey(i => i.Id);
        builder.Property(i => i.Id).ValueGeneratedNever();
        builder.Property(i => i.TokenHash).IsRequired().HasMaxLength(64);
        builder.Property(i => i.CreatedAt).HasColumnType("timestamp with time zone");
        builder.Property(i => i.ExpiresAt).HasColumnType("timestamp with time zone");
        builder.Property(i => i.ConsumedAt).HasColumnType("timestamp with time zone");
        builder.Property(i => i.RevokedAt).HasColumnType("timestamp with time zone");

        // The pairing seam resolves an invite by its hash, and only by it.
        builder.HasIndex(i => i.TokenHash)
            .IsUnique()
            .HasDatabaseName("ux_party_collaborator_invites_token_hash");
        builder.HasIndex(i => i.PartyCollaboratorId)
            .HasDatabaseName("ix_party_collaborator_invites_collaborator");

        builder.HasOne<PartyCollaborator>()
            .WithMany()
            .HasForeignKey(i => i.PartyCollaboratorId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class PartyCollaboratorAuthChallengeConfiguration
    : IEntityTypeConfiguration<PartyCollaboratorAuthChallenge>
{
    public void Configure(EntityTypeBuilder<PartyCollaboratorAuthChallenge> builder)
    {
        builder.ToTable("party_collaborator_auth_challenges", t =>
        {
            t.HasCheckConstraint(
                "ck_party_collaborator_auth_challenges_token_hash",
                "length(\"ChallengeTokenHash\") = 64");
            // HMAC-SHA256 as hex. Not a bare hash of the code — see PartyCrewTokens.
            t.HasCheckConstraint(
                "ck_party_collaborator_auth_challenges_otp_proof",
                "length(\"OtpProof\") = 64");
            t.HasCheckConstraint(
                "ck_party_collaborator_auth_challenges_attempts",
                "\"FailedAttempts\" >= 0");
            t.HasCheckConstraint(
                "ck_party_collaborator_auth_challenges_sends",
                "\"OtpSendCount\" >= 0");
            t.HasCheckConstraint(
                "ck_party_collaborator_auth_challenges_generation",
                "\"OtpGeneration\" >= 1");
        });
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).ValueGeneratedNever();
        builder.Property(c => c.ChallengeTokenHash).IsRequired().HasMaxLength(64);
        builder.Property(c => c.OtpProof).IsRequired().HasMaxLength(64);
        builder.Property(c => c.CreatedAt).HasColumnType("timestamp with time zone");
        builder.Property(c => c.ExpiresAt).HasColumnType("timestamp with time zone");
        builder.Property(c => c.OtpSentAt).HasColumnType("timestamp with time zone");
        builder.Property(c => c.OtpSendCount).HasDefaultValue(1);
        builder.Property(c => c.OtpGeneration).HasDefaultValue(1);
        builder.Property(c => c.LastAttemptAt).HasColumnType("timestamp with time zone");
        builder.Property(c => c.VerifiedAt).HasColumnType("timestamp with time zone");
        builder.Property(c => c.CompletedAt).HasColumnType("timestamp with time zone");
        builder.Property(c => c.RevokedAt).HasColumnType("timestamp with time zone");

        builder.HasIndex(c => c.ChallengeTokenHash)
            .IsUnique()
            .HasDatabaseName("ux_party_collaborator_auth_challenges_token_hash");
        builder.HasIndex(c => new { c.PartyCollaboratorId, c.CreatedAt })
            .HasDatabaseName("ix_party_collaborator_auth_challenges_collaborator");

        builder.HasOne<PartyCollaborator>()
            .WithMany()
            .HasForeignKey(c => c.PartyCollaboratorId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<PartyCollaboratorInvite>()
            .WithMany()
            .HasForeignKey(c => c.PartyCollaboratorInviteId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class PartyCrewDeviceConfiguration : IEntityTypeConfiguration<PartyCrewDevice>
{
    public void Configure(EntityTypeBuilder<PartyCrewDevice> builder)
    {
        builder.ToTable("party_crew_devices", t =>
        {
            t.HasCheckConstraint("ck_party_crew_devices_token_hash", "length(\"TokenHash\") = 64");
        });
        builder.HasKey(d => d.Id);
        builder.Property(d => d.Id).ValueGeneratedNever();
        builder.Property(d => d.TokenHash).IsRequired().HasMaxLength(64);
        builder.Property(d => d.DeviceLabel).HasMaxLength(PartyCrewLimits.MaxDeviceLabelLength);
        builder.Property(d => d.UserAgent).HasMaxLength(PartyCrewLimits.MaxUserAgentLength);
        builder.Property(d => d.CreatedAt).HasColumnType("timestamp with time zone");
        builder.Property(d => d.LastSeenAt).HasColumnType("timestamp with time zone");
        builder.Property(d => d.ExpiresAt).HasColumnType("timestamp with time zone");
        builder.Property(d => d.RevokedAt).HasColumnType("timestamp with time zone");

        builder.HasIndex(d => d.TokenHash)
            .IsUnique()
            .HasDatabaseName("ux_party_crew_devices_token_hash");
    }
}

public sealed class PartyCollaboratorDeviceGrantConfiguration
    : IEntityTypeConfiguration<PartyCollaboratorDeviceGrant>
{
    public void Configure(EntityTypeBuilder<PartyCollaboratorDeviceGrant> builder)
    {
        builder.ToTable("party_collaborator_device_grants");
        builder.HasKey(g => g.Id);
        builder.Property(g => g.Id).ValueGeneratedNever();
        builder.Property(g => g.CreatedAt).HasColumnType("timestamp with time zone");
        builder.Property(g => g.LastUsedAt).HasColumnType("timestamp with time zone");
        builder.Property(g => g.RevokedAt).HasColumnType("timestamp with time zone");

        // One device is one collaborator once. A second pairing of a device that
        // already holds this grant must not consume a slot, and the database is
        // what makes that true under a race rather than the code that checks it.
        builder.HasIndex(g => new { g.PartyCrewDeviceId, g.PartyCollaboratorId })
            .IsUnique()
            .HasDatabaseName("ux_party_collaborator_device_grants_device_collaborator");

        // The two-device count, and the resolver's lookup.
        builder.HasIndex(g => new { g.PartyCollaboratorId, g.RevokedAt })
            .HasDatabaseName("ix_party_collaborator_device_grants_collaborator_revoked");

        builder.HasOne<PartyCrewDevice>()
            .WithMany()
            .HasForeignKey(g => g.PartyCrewDeviceId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<PartyCollaborator>()
            .WithMany()
            .HasForeignKey(g => g.PartyCollaboratorId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
