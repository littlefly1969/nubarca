using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NubArca.Api.Domain;
using NubArca.Api.Domain.Ai;

namespace NubArca.Api.Data.Configurations.Ai;

public class PersonFaceReferenceConfiguration : IEntityTypeConfiguration<PersonFaceReference>
{
    public void Configure(EntityTypeBuilder<PersonFaceReference> builder)
    {
        builder.ToTable("person_face_references", t =>
        {
            t.HasCheckConstraint(
                "ck_person_face_references_ordinal_range",
                "\"Ordinal\" >= 0 AND \"Ordinal\" < 6");
        });

        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedNever();
        builder.Property(r => r.CreatedAt).HasColumnType("timestamp with time zone");

        // A face appears at most once in a person's reference set for a profile.
        builder.HasIndex(r => new { r.OwnerUserId, r.PersonId, r.ProfileId, r.FaceDetectionId })
            .IsUnique()
            .HasDatabaseName("ux_person_face_references_owner_person_profile_face");

        // One face per slot — the max-6 cap is a database fact, not only a code rule.
        builder.HasIndex(r => new { r.OwnerUserId, r.PersonId, r.ProfileId, r.Ordinal })
            .IsUnique()
            .HasDatabaseName("ux_person_face_references_owner_person_profile_ordinal");

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(r => r.OwnerUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Person>()
            .WithMany()
            .HasForeignKey(r => r.PersonId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<AiProfile>()
            .WithMany()
            .HasForeignKey(r => r.ProfileId)
            .OnDelete(DeleteBehavior.Cascade);

        // Derived state: a re-detected/removed face takes its reference with it.
        builder.HasOne<FaceDetection>()
            .WithMany()
            .HasForeignKey(r => r.FaceDetectionId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
