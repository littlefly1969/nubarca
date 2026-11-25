using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NubArca.Api.Domain;

namespace NubArca.Api.Data.Configurations;

public sealed class SubjectCutoutQueueItemConfiguration : IEntityTypeConfiguration<SubjectCutoutQueueItem>
{
    public void Configure(EntityTypeBuilder<SubjectCutoutQueueItem> b)
    {
        b.ToTable("subject_cutout_queue_items");
        b.HasKey(x => new { x.OwnerUserId, x.FileItemId });
        b.Property(x => x.AddedAt).HasColumnType("timestamp with time zone");
        b.HasIndex(x => new { x.OwnerUserId, x.AddedAt });
        b.HasIndex(x => x.FileItemId);
        b.HasOne<User>().WithMany().HasForeignKey(x => x.OwnerUserId).OnDelete(DeleteBehavior.Cascade);
        // A waiting reference never pins its source: a purge simply ends it.
        b.HasOne<FileItem>().WithMany().HasForeignKey(x => x.FileItemId).OnDelete(DeleteBehavior.Cascade);
    }
}
