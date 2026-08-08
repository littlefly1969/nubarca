using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NubArca.Api.Domain;

namespace NubArca.Api.Data.Configurations;

public class PasswordResetTokenConfiguration : IEntityTypeConfiguration<PasswordResetToken>
{
    public void Configure(EntityTypeBuilder<PasswordResetToken> builder)
    {
        builder.ToTable("password_reset_tokens");

        builder.HasKey(t => t.Id);

        builder.Property(t => t.Id)
            .ValueGeneratedNever();

        // 64 hex characters of SHA-256. The raw token is never stored, so this
        // column is the only thing a database copy yields — and it grants
        // nothing.
        builder.Property(t => t.TokenHash)
            .IsRequired()
            .HasMaxLength(64);

        builder.Property(t => t.CreatedAt)
            .HasColumnType("timestamp with time zone");

        builder.Property(t => t.ExpiresAt)
            .HasColumnType("timestamp with time zone");

        builder.Property(t => t.UsedAt)
            .HasColumnType("timestamp with time zone");

        // The reset request arrives carrying a token and nothing else, so the
        // digest is the lookup key. Unique: two rows with one digest would mean
        // a collision or a duplicate insert, and either is a bug worth failing.
        builder.HasIndex(t => t.TokenHash)
            .IsUnique();

        // Invalidating a user's outstanding tokens (on any credential change)
        // walks this index.
        builder.HasIndex(t => t.UserId);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(t => t.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
