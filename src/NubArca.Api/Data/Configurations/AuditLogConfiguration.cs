using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NubArca.Api.Domain;

namespace NubArca.Api.Data.Configurations;

public class AuditLogConfiguration : IEntityTypeConfiguration<AuditLog>
{
    public void Configure(EntityTypeBuilder<AuditLog> builder)
    {
        builder.ToTable("audit_logs");

        builder.HasKey(a => a.Id);

        builder.Property(a => a.Id)
            .ValueGeneratedNever();

        builder.Property(a => a.Action)
            .IsRequired()
            .HasMaxLength(100);

        builder.Property(a => a.EntityType)
            .IsRequired()
            .HasMaxLength(100);

        builder.Property(a => a.IpAddress)
            .HasMaxLength(45);

        builder.Property(a => a.CreatedAt)
            .HasColumnType("timestamp with time zone");

        builder.Property(a => a.MetadataJson)
            .HasColumnType("jsonb");

        // Deliberately NO foreign key to party_collaborators, unlike the one
        // to users below. A restricting key would make the audit trail refuse
        // a party teardown, and a cascading one would erase the record of
        // what a collaborator did the moment they were removed. The audit
        // outlives the actor; that is what it is for.
        builder.HasIndex(a => new { a.PartyCollaboratorId, a.CreatedAt })
            .HasDatabaseName("ix_audit_logs_collaborator_created")
            .HasFilter("\"PartyCollaboratorId\" IS NOT NULL");

        builder.HasIndex(a => new { a.UserId, a.CreatedAt })
            .HasDatabaseName("ix_audit_logs_user_created");

        builder.HasIndex(a => new { a.Action, a.CreatedAt })
            .HasDatabaseName("ix_audit_logs_action_created");

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(a => a.UserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
