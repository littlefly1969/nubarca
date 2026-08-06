using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NubArca.Api.Domain.Ai;

namespace NubArca.Api.Data.Configurations.Ai;

public class FaceClusterMemberConfiguration : IEntityTypeConfiguration<FaceClusterMember>
{
    public void Configure(EntityTypeBuilder<FaceClusterMember> builder)
    {
        builder.ToTable("face_cluster_members");

        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id).ValueGeneratedNever();
        builder.Property(m => m.MembershipSource).IsRequired().HasMaxLength(32);
        builder.Property(m => m.CreatedAt).HasColumnType("timestamp with time zone");
        builder.Property(m => m.UpdatedAt).HasColumnType("timestamp with time zone");

        // A face appears at most once in a given cluster.
        builder.HasIndex(m => new { m.FaceClusterId, m.FaceDetectionId })
            .IsUnique()
            .HasDatabaseName("ux_face_cluster_members_cluster_face");

        builder.HasIndex(m => m.FaceDetectionId)
            .HasDatabaseName("ix_face_cluster_members_face");

        builder.HasOne<FaceCluster>()
            .WithMany()
            .HasForeignKey(m => m.FaceClusterId)
            .OnDelete(DeleteBehavior.Cascade);

        // Membership dies with its blob-level detection (which cascades from blob).
        builder.HasOne<FaceDetection>()
            .WithMany()
            .HasForeignKey(m => m.FaceDetectionId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
