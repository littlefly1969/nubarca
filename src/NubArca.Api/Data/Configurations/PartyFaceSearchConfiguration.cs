using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NubArca.Api.Domain;

namespace NubArca.Api.Data.Configurations;

public class PartyFaceSearchSessionConfiguration : IEntityTypeConfiguration<PartyFaceSearchSession>
{
    public void Configure(EntityTypeBuilder<PartyFaceSearchSession> builder)
    {
        builder.ToTable("party_face_search_sessions");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();

        // Short status token ("ready"/"no_face"/"invalid_image"/"unavailable").
        // Generous bound so a future status can never overflow the column (see the
        // widened admin-import status columns lesson).
        builder.Property(s => s.Status).IsRequired().HasMaxLength(32);
        builder.Property(s => s.CreatedAt).HasColumnType("timestamp with time zone");
        builder.Property(s => s.ExpiresAt).HasColumnType("timestamp with time zone");

        // The TV "active face search" lookup: latest unexpired session per owner+album.
        builder.HasIndex(s => new { s.OwnerUserId, s.AlbumId, s.ExpiresAt })
            .HasDatabaseName("ix_party_face_search_sessions_owner_album_expires");

        builder.Property(s => s.TvActivatedAt).HasColumnType("timestamp with time zone");

        // The face-crop blob reference is released explicitly on search delete
        // (and re-counted by BlobReferenceAuditService); Restrict so a live crop
        // blob row can never be deleted out from under a session.
        builder.HasOne<BlobObject>()
            .WithMany()
            .HasForeignKey(s => s.FaceCropBlobObjectId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Album>()
            .WithMany()
            .HasForeignKey(s => s.AlbumId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(s => s.OwnerUserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public class PartyFaceSearchResultConfiguration : IEntityTypeConfiguration<PartyFaceSearchResult>
{
    public void Configure(EntityTypeBuilder<PartyFaceSearchResult> builder)
    {
        builder.ToTable("party_face_search_results");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedNever();
        builder.Property(r => r.CreatedAt).HasColumnType("timestamp with time zone");

        // Read path: a session's matches in rank order.
        builder.HasIndex(r => new { r.PartyFaceSearchSessionId, r.Rank })
            .HasDatabaseName("ix_party_face_search_results_session_rank");

        // Drop result rows with their session.
        builder.HasOne<PartyFaceSearchSession>()
            .WithMany()
            .HasForeignKey(r => r.PartyFaceSearchSessionId)
            .OnDelete(DeleteBehavior.Cascade);

        // If the matched file is hard-deleted, drop the stale rank row with it.
        builder.HasOne<FileItem>()
            .WithMany()
            .HasForeignKey(r => r.FileItemId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
