using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NubArca.Api.Domain;
using NubArca.Api.Domain.Ai;

namespace NubArca.Api.Data.Configurations.Ai;

public class FaceAssignmentConfiguration : IEntityTypeConfiguration<FaceAssignment>
{
    public void Configure(EntityTypeBuilder<FaceAssignment> builder)
    {
        builder.ToTable("face_assignments");

        builder.HasKey(a => a.Id);

        builder.Property(a => a.Id)
            .ValueGeneratedNever();

        builder.Property(a => a.Source)
            .IsRequired()
            .HasMaxLength(16);

        builder.Property(a => a.CreatedAt)
            .HasColumnType("timestamp with time zone");

        builder.Property(a => a.UpdatedAt)
            .HasColumnType("timestamp with time zone");

        // One assignment per (owner, face, model-space). The explicit profile
        // column lets v1/v2 face-embedding clusterings coexist (see entity doc).
        builder.HasIndex(a => new { a.OwnerUserId, a.FaceDetectionId, a.FaceEmbeddingProfileId })
            .IsUnique()
            .HasDatabaseName("ux_face_assignments_owner_face_profile");

        builder.HasIndex(a => a.PersonGroupId)
            .HasDatabaseName("ix_face_assignments_person_group");

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(a => a.OwnerUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<FaceDetection>()
            .WithMany()
            .HasForeignKey(a => a.FaceDetectionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<PersonGroup>()
            .WithMany()
            .HasForeignKey(a => a.PersonGroupId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<AiProfile>()
            .WithMany()
            .HasForeignKey(a => a.FaceEmbeddingProfileId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
