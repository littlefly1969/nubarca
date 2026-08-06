using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NubArca.Api.Domain;

namespace NubArca.Api.Data.Configurations;

public class BlobHlsDerivativeConfiguration : IEntityTypeConfiguration<BlobHlsDerivative>
{
    public void Configure(EntityTypeBuilder<BlobHlsDerivative> builder)
    {
        builder.ToTable("blob_hls_derivatives");

        builder.HasKey(d => d.Id);

        builder.Property(d => d.Id)
            .ValueGeneratedNever();

        builder.Property(d => d.Status)
            .IsRequired()
            .HasMaxLength(16);

        builder.Property(d => d.ErrorCode)
            .HasMaxLength(64);

        builder.Property(d => d.CreatedAt)
            .HasColumnType("timestamp with time zone");

        builder.Property(d => d.ReadyAt)
            .HasColumnType("timestamp with time zone");

        // One ladder per source blob — the whole point of keying by blob.
        builder.HasIndex(d => d.BlobObjectId)
            .IsUnique()
            .HasDatabaseName("ux_blob_hls_derivatives_blob");

        builder.HasOne<BlobObject>()
            .WithMany()
            .HasForeignKey(d => d.BlobObjectId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
