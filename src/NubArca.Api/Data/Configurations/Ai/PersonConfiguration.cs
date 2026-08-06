using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NubArca.Api.Domain;
using NubArca.Api.Domain.Ai;

namespace NubArca.Api.Data.Configurations.Ai;

public class PersonConfiguration : IEntityTypeConfiguration<Person>
{
    public void Configure(EntityTypeBuilder<Person> builder)
    {
        builder.ToTable("people");

        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedNever();
        builder.Property(p => p.DisplayName).HasMaxLength(200);
        builder.Property(p => p.CreatedAt).HasColumnType("timestamp with time zone");
        builder.Property(p => p.UpdatedAt).HasColumnType("timestamp with time zone");

        builder.HasIndex(p => new { p.OwnerUserId, p.IsArchived })
            .HasDatabaseName("ix_people_owner_archived");

        // VFACE-02: an alternate key so owner-scoped tables can carry a COMPOSITE
        // foreign key (PersonId, OwnerUserId) and let the database itself refuse
        // a cross-owner person reference. Purely additive — Id remains the
        // primary key and nothing about existing rows or queries changes.
        builder.HasAlternateKey(p => new { p.Id, p.OwnerUserId })
            .HasName("ak_people_id_owner");

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(p => p.OwnerUserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
