using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NubArca.Api.Domain;

namespace NubArca.Api.Data.Configurations;

public sealed class LithophaneProjectConfiguration : IEntityTypeConfiguration<LithophaneProject>
{
    public void Configure(EntityTypeBuilder<LithophaneProject> b)
    {
        b.ToTable("lithophane_projects");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Name).HasMaxLength(255).IsRequired();
        b.Property(x => x.SettingsJson).HasMaxLength(4096).IsRequired();
        b.Property(x => x.PixelMapStorageKey).HasMaxLength(255);
        b.Property(x => x.ArtifactStorageKey).HasMaxLength(255);
        b.Property(x => x.CreatedAt).HasColumnType("timestamp with time zone");
        b.HasIndex(x => new { x.OwnerUserId, x.RequestId }).IsUnique();
        b.HasIndex(x => new { x.OwnerUserId, x.CreatedAt, x.Id });
        b.HasIndex(x => x.PixelMapStorageKey);
        b.HasIndex(x => x.ArtifactStorageKey);
        b.HasOne<User>().WithMany().HasForeignKey(x => x.OwnerUserId).OnDelete(DeleteBehavior.Restrict);
        // A generated object must never pin or block the purge of its source.
        b.HasOne<FileItem>().WithMany().HasForeignKey(x => x.FileItemId).OnDelete(DeleteBehavior.SetNull);
    }
}

public sealed class LithophaneStripConfiguration : IEntityTypeConfiguration<LithophaneStrip>
{
    public void Configure(EntityTypeBuilder<LithophaneStrip> b)
    {
        b.ToTable("lithophane_strips");
        b.HasKey(x => new { x.ProjectId, x.FirstRow });
        b.Property(x => x.StorageKey).HasMaxLength(255).IsRequired();
        b.HasIndex(x => x.StorageKey);
        b.HasOne<LithophaneProject>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
    }
}
