using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NubArca.Api.Domain;
using NubArca.Api.Domain.Ai;

namespace NubArca.Api.Data.Configurations.Ai;

public class DocumentTextConfiguration : IEntityTypeConfiguration<DocumentText>
{
    public void Configure(EntityTypeBuilder<DocumentText> builder)
    {
        builder.ToTable("document_texts", t =>
        {
            t.HasCheckConstraint(
                "ck_document_texts_char_count_non_negative",
                "\"CharCount\" IS NULL OR \"CharCount\" >= 0");
        });

        builder.HasKey(d => d.Id);

        builder.Property(d => d.Id)
            .ValueGeneratedNever();

        builder.Property(d => d.Source)
            .IsRequired()
            .HasMaxLength(32);

        builder.Property(d => d.Status)
            .IsRequired()
            .HasMaxLength(32);

        builder.Property(d => d.ErrorCode)
            .HasMaxLength(100);

        builder.Property(d => d.TextHash)
            .HasMaxLength(64);

        // Internal-only full text. text on PostgreSQL; SQLite uses dynamic typing.
        builder.Property(d => d.Text)
            .HasColumnType("text");

        builder.Property(d => d.Language)
            .HasMaxLength(32);

        builder.Property(d => d.CreatedAt)
            .HasColumnType("timestamp with time zone");

        builder.Property(d => d.UpdatedAt)
            .HasColumnType("timestamp with time zone");

        builder.HasIndex(d => new { d.FileItemId, d.ProfileId })
            .IsUnique()
            .HasDatabaseName("ux_document_texts_file_profile");

        builder.HasIndex(d => d.OwnerUserId)
            .HasDatabaseName("ix_document_texts_owner");

        builder.HasOne<FileItem>()
            .WithMany()
            .HasForeignKey(d => d.FileItemId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(d => d.OwnerUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<AiProfile>()
            .WithMany()
            .HasForeignKey(d => d.ProfileId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
