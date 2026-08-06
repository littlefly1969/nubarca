using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NubArca.Api.Domain;

namespace NubArca.Api.Data.Configurations;

public class AlbumTransferConfiguration : IEntityTypeConfiguration<AlbumTransfer>
{
    public void Configure(EntityTypeBuilder<AlbumTransfer> builder)
    {
        builder.ToTable("album_transfers", t =>
        {
            t.HasCheckConstraint(
                "ck_album_transfers_state",
                "\"State\" IN ('pending', 'accepted', 'declined', 'cancelled', 'expired', 'failed')");

            // A user can never send a copy to themselves. Enforced by the
            // service too, but stated here so the invariant survives any future
            // write path.
            t.HasCheckConstraint(
                "ck_album_transfers_recipient_not_sender",
                "\"RecipientUserId\" <> \"SenderUserId\"");

            // Accepted is the only state that may name a destination album, and
            // it must name one. This is what the idempotent-accept path relies
            // on: if the row says accepted, CreatedAlbumId is trustworthy.
            t.HasCheckConstraint(
                "ck_album_transfers_created_album",
                "(\"State\" = 'accepted' AND \"CreatedAlbumId\" IS NOT NULL) "
                    + "OR (\"State\" <> 'accepted' AND \"CreatedAlbumId\" IS NULL)");
        });

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();

        builder.Property(x => x.Title).IsRequired().HasMaxLength(255);
        builder.Property(x => x.Description).HasMaxLength(1000);
        builder.Property(x => x.State).IsRequired().HasMaxLength(32);
        builder.Property(x => x.CreatedAt).HasColumnType("timestamp with time zone");
        builder.Property(x => x.ExpiresAt).HasColumnType("timestamp with time zone");
        builder.Property(x => x.RespondedAt).HasColumnType("timestamp with time zone");
        builder.Property(x => x.CancelledAt).HasColumnType("timestamp with time zone");
        builder.Property(x => x.UpdatedAt).HasColumnType("timestamp with time zone");

        // "What has been sent to me" — the recipient's inbox.
        builder.HasIndex(x => new { x.RecipientUserId, x.State })
            .HasDatabaseName("ix_album_transfers_recipient_state");

        // "What have I sent" — the sender's outbox, and the lookup that stops a
        // second pending transfer for the same (album, recipient) pair.
        builder.HasIndex(x => new { x.SenderUserId, x.State })
            .HasDatabaseName("ix_album_transfers_sender_state");

        // Expiry sweep: find live transfers past their window without scanning
        // terminal history.
        builder.HasIndex(x => new { x.State, x.ExpiresAt })
            .HasDatabaseName("ix_album_transfers_state_expires");

        // Repeated "send this album to this person" must not pile up pending
        // offers. A PARTIAL unique index so the constraint applies only while a
        // transfer is live — once declined, cancelled or expired the sender may
        // legitimately send again, and once accepted the recipient may
        // legitimately be sent an updated copy later.
        //
        // PostgreSQL only: SQLite (the endpoint-test fixture) supports partial
        // indexes with the same syntax, so this is exercised by the tests too.
        builder.HasIndex(x => new { x.SourceAlbumId, x.RecipientUserId })
            .IsUnique()
            .HasFilter("\"State\" = 'pending'")
            .HasDatabaseName("ux_album_transfers_pending_album_recipient");

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(x => x.SenderUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(x => x.RecipientUserId)
            .OnDelete(DeleteBehavior.Restrict);

        // SourceAlbumId and CreatedAlbumId are deliberately NOT foreign keys —
        // see AlbumTransfer. The source album may be deleted while a transfer is
        // pending; the recipient may delete the copy they were given. Neither
        // event may block the other party or rewrite history.
    }
}

public class AlbumTransferItemConfiguration : IEntityTypeConfiguration<AlbumTransferItem>
{
    public void Configure(EntityTypeBuilder<AlbumTransferItem> builder)
    {
        builder.ToTable("album_transfer_items");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();

        builder.Property(x => x.Name).IsRequired().HasMaxLength(255);
        builder.Property(x => x.MimeType).IsRequired().HasMaxLength(255);
        builder.Property(x => x.EffectiveDateTaken).HasColumnType("timestamp with time zone");

        builder.HasIndex(x => new { x.AlbumTransferId, x.SortOrder })
            .HasDatabaseName("ix_album_transfer_items_transfer_order");

        // Reverse lookup for the reference audit and for the release path.
        builder.HasIndex(x => x.BlobObjectId)
            .HasDatabaseName("ix_album_transfer_items_blob");

        builder.HasOne<AlbumTransfer>()
            .WithMany()
            .HasForeignKey(x => x.AlbumTransferId)
            .OnDelete(DeleteBehavior.Cascade);

        // RESTRICT on top of the reference count. The refcount is derived
        // accounting; this is a hard database guarantee that the bytes behind a
        // pending copy cannot be dropped, even if the accounting drifted. The
        // BlobJanitor already treats a remaining FK owner as its final safety
        // net and skips the blob.
        builder.HasOne<BlobObject>()
            .WithMany()
            .HasForeignKey(x => x.BlobObjectId)
            .OnDelete(DeleteBehavior.Restrict);

        // SourceFileItemId is NOT a foreign key: the source file may be
        // permanently deleted while the transfer is pending, and that must not
        // block the sender or destroy the snapshot. It is audit provenance only
        // and is never dereferenced during acceptance.
    }
}
