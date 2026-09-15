using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NubArca.Api.Domain;

namespace NubArca.Api.Data.Configurations;

// The guest list's six tables. Every foreign key is Restrict, like every other
// Party table: what a teardown or a group removal takes with it is stated out
// loud in PartyStateEraser and PartyInvitationService, never left to a cascade
// nobody reads. Closed vocabularies are held by the DATABASE as well as by the
// validators, so a bad write is refused rather than stored.

public sealed class PartyInvitationGroupConfiguration : IEntityTypeConfiguration<PartyInvitationGroup>
{
    public void Configure(EntityTypeBuilder<PartyInvitationGroup> builder)
    {
        builder.ToTable("party_invitation_groups", t =>
        {
            t.HasCheckConstraint(
                "ck_party_invitation_groups_max_additional",
                "\"MaxAdditionalGuests\" BETWEEN 0 AND 10");
            // SHA-256 hex, exactly. A shorter value is not a hash of anything.
            t.HasCheckConstraint(
                "ck_party_invitation_groups_token_hash",
                "length(\"TokenHash\") = 64");
        });
        builder.HasKey(g => g.Id);
        builder.Property(g => g.Id).ValueGeneratedNever();

        builder.Property(g => g.Label).IsRequired().HasMaxLength(PartyInvitationLimits.MaxLabelLength);
        builder.Property(g => g.RecipientEmail).IsRequired().HasMaxLength(PartyInvitationLimits.MaxEmailLength);
        builder.Property(g => g.Phone).HasMaxLength(PartyInvitationLimits.MaxPhoneLength);
        builder.Property(g => g.TokenHash).IsRequired().HasMaxLength(64);
        builder.Property(g => g.Version).HasDefaultValue(1);
        builder.Property(g => g.CapabilityIssuedAt).HasColumnType("timestamp with time zone");
        builder.Property(g => g.CreatedAt).HasColumnType("timestamp with time zone");
        builder.Property(g => g.UpdatedAt).HasColumnType("timestamp with time zone");

        // The public seam resolves a personal link by its hash, and only by it.
        builder.HasIndex(g => g.TokenHash)
            .IsUnique()
            .HasDatabaseName("ux_party_invitation_groups_token_hash");
        // The host's guest list, in the order the groups were added.
        builder.HasIndex(g => new { g.PartyId, g.CreatedAt })
            .HasDatabaseName("ix_party_invitation_groups_party_created");

        builder.HasOne<Domain.Party>()
            .WithMany()
            .HasForeignKey(g => g.PartyId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class PartyGuestConfiguration : IEntityTypeConfiguration<PartyGuest>
{
    public void Configure(EntityTypeBuilder<PartyGuest> builder)
    {
        builder.ToTable("party_guests");
        builder.HasKey(g => g.Id);
        builder.Property(g => g.Id).ValueGeneratedNever();

        builder.Property(g => g.Name).IsRequired().HasMaxLength(PartyInvitationLimits.MaxGuestNameLength);
        builder.Property(g => g.Email).HasMaxLength(PartyInvitationLimits.MaxEmailLength);
        builder.Property(g => g.Phone).HasMaxLength(PartyInvitationLimits.MaxPhoneLength);
        builder.Property(g => g.CreatedAt).HasColumnType("timestamp with time zone");
        builder.Property(g => g.UpdatedAt).HasColumnType("timestamp with time zone");

        builder.HasIndex(g => new { g.PartyInvitationGroupId, g.IsAdditionalGuest, g.SortOrder })
            .HasDatabaseName("ix_party_guests_group_order");

        builder.HasOne<PartyInvitationGroup>()
            .WithMany()
            .HasForeignKey(g => g.PartyInvitationGroupId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class PartyRsvpConfiguration : IEntityTypeConfiguration<PartyRsvp>
{
    public void Configure(EntityTypeBuilder<PartyRsvp> builder)
    {
        builder.ToTable("party_rsvps", t =>
        {
            t.HasCheckConstraint(
                "ck_party_rsvps_status",
                "\"Status\" IN ('pending', 'attending', 'declined')");
        });
        // The key IS the "one RSVP per person" rule.
        builder.HasKey(r => r.PartyGuestId);
        builder.Property(r => r.PartyGuestId).ValueGeneratedNever();

        builder.Property(r => r.Status)
            .IsRequired()
            .HasMaxLength(16)
            .HasDefaultValue(PartyRsvpStatuses.Pending);
        builder.Property(r => r.DietaryNotes).HasMaxLength(PartyInvitationLimits.MaxDietaryNotesLength);
        builder.Property(r => r.RespondedAt).HasColumnType("timestamp with time zone");
        builder.Property(r => r.UpdatedAt).HasColumnType("timestamp with time zone");

        builder.HasOne<PartyGuest>()
            .WithOne()
            .HasForeignKey<PartyRsvp>(r => r.PartyGuestId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class PartyRsvpQuestionConfiguration : IEntityTypeConfiguration<PartyRsvpQuestion>
{
    public void Configure(EntityTypeBuilder<PartyRsvpQuestion> builder)
    {
        builder.ToTable("party_rsvp_questions", t =>
        {
            t.HasCheckConstraint(
                "ck_party_rsvp_questions_kind",
                "\"Kind\" IN ('short_text', 'single_choice', 'yes_no')");
            // Options exist exactly for a single choice. Both halves: a choice
            // with no options cannot be answered, and options on a yes/no
            // question would be a second vocabulary nobody validates.
            t.HasCheckConstraint(
                "ck_party_rsvp_questions_options",
                "(\"Kind\" = 'single_choice') = (\"OptionsJson\" IS NOT NULL)");
        });
        builder.HasKey(q => q.Id);
        builder.Property(q => q.Id).ValueGeneratedNever();

        builder.Property(q => q.Prompt).IsRequired().HasMaxLength(PartyInvitationLimits.MaxQuestionPromptLength);
        builder.Property(q => q.Kind).IsRequired().HasMaxLength(16);
        // Twenty options of 120 code points, serialized, fits comfortably.
        builder.Property(q => q.OptionsJson).HasMaxLength(8192);
        builder.Property(q => q.IsActive).HasDefaultValue(true);
        builder.Property(q => q.Version).HasDefaultValue(1);
        builder.Property(q => q.CreatedAt).HasColumnType("timestamp with time zone");
        builder.Property(q => q.UpdatedAt).HasColumnType("timestamp with time zone");

        builder.HasIndex(q => new { q.PartyId, q.SortOrder })
            .HasDatabaseName("ix_party_rsvp_questions_party_order");

        builder.HasOne<Domain.Party>()
            .WithMany()
            .HasForeignKey(q => q.PartyId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class PartyRsvpAnswerConfiguration : IEntityTypeConfiguration<PartyRsvpAnswer>
{
    public void Configure(EntityTypeBuilder<PartyRsvpAnswer> builder)
    {
        builder.ToTable("party_rsvp_answers");
        // One answer per group per question, and no second way to write it.
        builder.HasKey(a => new { a.PartyInvitationGroupId, a.PartyRsvpQuestionId });

        builder.Property(a => a.ValueJson).IsRequired().HasMaxLength(2048);
        builder.Property(a => a.CreatedAt).HasColumnType("timestamp with time zone");
        builder.Property(a => a.UpdatedAt).HasColumnType("timestamp with time zone");

        // "Has anybody answered this yet?" — the question that freezes it.
        builder.HasIndex(a => a.PartyRsvpQuestionId)
            .HasDatabaseName("ix_party_rsvp_answers_question");

        builder.HasOne<PartyInvitationGroup>()
            .WithMany()
            .HasForeignKey(a => a.PartyInvitationGroupId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<PartyRsvpQuestion>()
            .WithMany()
            .HasForeignKey(a => a.PartyRsvpQuestionId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class PartyInvitationDeliveryConfiguration : IEntityTypeConfiguration<PartyInvitationDelivery>
{
    public void Configure(EntityTypeBuilder<PartyInvitationDelivery> builder)
    {
        builder.ToTable("party_invitation_deliveries", t =>
        {
            t.HasCheckConstraint(
                "ck_party_invitation_deliveries_kind",
                "\"Kind\" IN ('initial', 'resend', 'reminder')");
            t.HasCheckConstraint(
                "ck_party_invitation_deliveries_status",
                "\"Status\" IN ('pending', 'sent', 'failed')");
            // A completed delivery says when; a pending one cannot.
            t.HasCheckConstraint(
                "ck_party_invitation_deliveries_completion",
                "(\"Status\" = 'pending') = (\"CompletedAt\" IS NULL)");
        });
        builder.HasKey(d => d.Id);
        builder.Property(d => d.Id).ValueGeneratedNever();

        builder.Property(d => d.Kind).IsRequired().HasMaxLength(16);
        builder.Property(d => d.Status).IsRequired().HasMaxLength(16);
        builder.Property(d => d.CreatedAt).HasColumnType("timestamp with time zone");
        builder.Property(d => d.CompletedAt).HasColumnType("timestamp with time zone");

        // THE IDEMPOTENCY RULE. Two requests carrying one click's id cannot both
        // become deliveries, however they race: the second insert loses here.
        builder.HasIndex(d => new { d.PartyInvitationGroupId, d.ClientRequestId })
            .IsUnique()
            .HasDatabaseName("ux_party_invitation_deliveries_request");
        builder.HasIndex(d => new { d.PartyInvitationGroupId, d.CreatedAt })
            .HasDatabaseName("ix_party_invitation_deliveries_group_created");

        builder.HasOne<PartyInvitationGroup>()
            .WithMany()
            .HasForeignKey(d => d.PartyInvitationGroupId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
