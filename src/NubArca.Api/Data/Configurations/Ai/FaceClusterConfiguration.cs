using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NubArca.Api.Domain;
using NubArca.Api.Domain.Ai;

namespace NubArca.Api.Data.Configurations.Ai;

public class FaceClusterConfiguration : IEntityTypeConfiguration<FaceCluster>
{
    public void Configure(EntityTypeBuilder<FaceCluster> builder)
    {
        builder.ToTable("face_clusters", t =>
        {
            t.HasCheckConstraint(
                "ck_face_clusters_member_count_non_negative",
                "\"MemberCount\" >= 0");
        });

        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).ValueGeneratedNever();
        builder.Property(c => c.Status).IsRequired().HasMaxLength(32);
        builder.Property(c => c.ClusterKey).HasMaxLength(128);
        builder.Property(c => c.CreatedAt).HasColumnType("timestamp with time zone");
        builder.Property(c => c.UpdatedAt).HasColumnType("timestamp with time zone");

        // Owner + model-space scoped; never cross-owner. Suggestion queries filter
        // by (owner, profile, status).
        builder.HasIndex(c => new { c.OwnerUserId, c.ProfileId, c.Status })
            .HasDatabaseName("ix_face_clusters_owner_profile_status");

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(c => c.OwnerUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<AiProfile>()
            .WithMany()
            .HasForeignKey(c => c.ProfileId)
            .OnDelete(DeleteBehavior.Restrict);

        // Naming a cluster links it to a Person; deleting the person unlinks.
        builder.HasOne<Person>()
            .WithMany()
            .HasForeignKey(c => c.PersonId)
            .OnDelete(DeleteBehavior.SetNull);

        // RepresentativeFaceDetectionId is a SOFT reference (no FK) — the cover
        // face is a blob-level detection that may be re-detected/removed; the
        // cluster tolerates a stale representative and recomputes on next cluster.
    }
}
