using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NubArca.Api.Domain;
using NubArca.Api.Domain.Ai;

namespace NubArca.Api.Data.Configurations.Ai;

public class VideoFaceTrackPersonDecisionConfiguration
    : IEntityTypeConfiguration<VideoFaceTrackPersonDecision>
{
    public void Configure(EntityTypeBuilder<VideoFaceTrackPersonDecision> builder)
    {
        builder.ToTable("video_face_track_person_decisions", t =>
        {
            // The decision/person pairing is a DATA invariant, not just a service
            // rule: an assignment without a person, or an ignore that secretly
            // names one, must be impossible to store.
            t.HasCheckConstraint(
                "ck_video_face_track_person_decisions_person_matches_decision",
                "(\"Decision\" = 'assigned' AND \"PersonId\" IS NOT NULL) "
                + "OR (\"Decision\" = 'ignored' AND \"PersonId\" IS NULL)");
        });

        builder.HasKey(d => d.Id);

        builder.Property(d => d.Id).ValueGeneratedNever();
        builder.Property(d => d.Decision).IsRequired().HasMaxLength(32);
        builder.Property(d => d.Source).IsRequired().HasMaxLength(32);
        builder.Property(d => d.CreatedAt).HasColumnType("timestamp with time zone");
        builder.Property(d => d.UpdatedAt).HasColumnType("timestamp with time zone");
        builder.Property(d => d.ConfirmedAt).HasColumnType("timestamp with time zone");

        // ONE decision per owner and track — the core invariant. Two owners
        // sharing a deduplicated blob each get their own row.
        builder.HasIndex(d => new { d.OwnerUserId, d.VideoFaceTrackId })
            .IsUnique()
            .HasDatabaseName("ux_video_face_track_person_decisions_owner_track");

        // "Which videos show this person" — the person-media read path.
        builder.HasIndex(d => new { d.OwnerUserId, d.PersonId })
            .HasDatabaseName("ix_video_face_track_person_decisions_owner_person");

        // "What has this owner still not decided / already ignored" — the review
        // queue.
        builder.HasIndex(d => new { d.OwnerUserId, d.Decision })
            .HasDatabaseName("ix_video_face_track_person_decisions_owner_decision");

        // Track-first lookup (a track's decisions, cascade maintenance).
        builder.HasIndex(d => d.VideoFaceTrackId)
            .HasDatabaseName("ix_video_face_track_person_decisions_track");

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(d => d.OwnerUserId)
            .OnDelete(DeleteBehavior.Restrict);

        // Re-analysing a video replaces its track set; the owner's decisions
        // about the replaced tracks go with them.
        builder.HasOne<VideoFaceTrack>()
            .WithMany()
            .HasForeignKey(d => d.VideoFaceTrackId)
            .OnDelete(DeleteBehavior.Cascade);

        // COMPOSITE foreign key against Person's (Id, OwnerUserId) alternate
        // key: the assigned person must belong to the SAME owner. This makes a
        // cross-owner assignment unrepresentable rather than merely rejected, so
        // no future code path can reintroduce the leak.
        builder.HasOne<Person>()
            .WithMany()
            .HasForeignKey(d => new { d.PersonId, d.OwnerUserId })
            .HasPrincipalKey(p => new { p.Id, p.OwnerUserId })
            .OnDelete(DeleteBehavior.Cascade);
    }
}
