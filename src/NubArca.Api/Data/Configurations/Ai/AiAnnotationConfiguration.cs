using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NubArca.Api.Domain;
using NubArca.Api.Domain.Ai;

namespace NubArca.Api.Data.Configurations.Ai;

public class AiAnnotationConfiguration : IEntityTypeConfiguration<AiAnnotation>
{
    public void Configure(EntityTypeBuilder<AiAnnotation> builder)
    {
        builder.ToTable("ai_annotations");

        builder.HasKey(a => a.Id);

        builder.Property(a => a.Id)
            .ValueGeneratedNever();

        builder.Property(a => a.Kind)
            .IsRequired()
            .HasMaxLength(32);

        builder.Property(a => a.Label)
            .HasMaxLength(200);

        // Caption/description text. Internal until an owner-private DTO exists.
        builder.Property(a => a.Text)
            .HasColumnType("text");

        builder.Property(a => a.CreatedAt)
            .HasColumnType("timestamp with time zone");

        builder.Property(a => a.UpdatedAt)
            .HasColumnType("timestamp with time zone");

        builder.HasIndex(a => new { a.FileItemId, a.ProfileId, a.Kind })
            .HasDatabaseName("ix_ai_annotations_file_profile_kind");

        builder.HasIndex(a => a.OwnerUserId)
            .HasDatabaseName("ix_ai_annotations_owner");

        builder.HasOne<FileItem>()
            .WithMany()
            .HasForeignKey(a => a.FileItemId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(a => a.OwnerUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<AiProfile>()
            .WithMany()
            .HasForeignKey(a => a.ProfileId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
