using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NubArca.Api.Domain;
using NubArca.Api.Domain.Ai;

namespace NubArca.Api.Data.Configurations.Ai;

public class PersonGroupConfiguration : IEntityTypeConfiguration<PersonGroup>
{
    public void Configure(EntityTypeBuilder<PersonGroup> builder)
    {
        builder.ToTable("person_groups");

        builder.HasKey(g => g.Id);

        builder.Property(g => g.Id)
            .ValueGeneratedNever();

        builder.Property(g => g.DisplayName)
            .HasMaxLength(200);

        builder.Property(g => g.ClusterKey)
            .HasMaxLength(128);

        builder.Property(g => g.CreatedAt)
            .HasColumnType("timestamp with time zone");

        builder.Property(g => g.UpdatedAt)
            .HasColumnType("timestamp with time zone");

        // Owner + model-space scoped; never cross-owner.
        builder.HasIndex(g => new { g.OwnerUserId, g.ProfileId })
            .HasDatabaseName("ix_person_groups_owner_profile");

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(g => g.OwnerUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<AiProfile>()
            .WithMany()
            .HasForeignKey(g => g.ProfileId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
