using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NubArca.Api.Domain;
using NubArca.Api.Domain.Mail;

namespace NubArca.Api.Data.Configurations.Mail;

// MAILARCHIVE-01 tables. Indexes and uniqueness carry the idempotency: an
// archive is (owner, fingerprint); a folder, a manifest item and a message are
// (archive, source key); an attachment link is (message, attachment key); a
// tombstone is (owner, fingerprint, message key). See contract.md §3.
public class MailArchiveConfiguration : IEntityTypeConfiguration<Domain.Mail.MailArchive>
{
    public void Configure(EntityTypeBuilder<Domain.Mail.MailArchive> builder)
    {
        builder.ToTable("mail_archives");
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).ValueGeneratedNever();

        builder.Property(a => a.SourceFingerprint).IsRequired().HasMaxLength(128);
        builder.Property(a => a.FingerprintScheme).IsRequired().HasMaxLength(32);
        builder.Property(a => a.SourceFileName).HasMaxLength(255);
        builder.Property(a => a.DisplayName).IsRequired().HasMaxLength(255);
        builder.Property(a => a.Format).HasMaxLength(16);
        builder.Property(a => a.CreatedAt).HasColumnType("timestamp with time zone");
        builder.Property(a => a.UpdatedAt).HasColumnType("timestamp with time zone");
        builder.Property(a => a.LastImportedAt).HasColumnType("timestamp with time zone");
        builder.Property(a => a.ScanCompletedAt).HasColumnType("timestamp with time zone");

        builder.HasIndex(a => new { a.OwnerUserId, a.SourceFingerprint })
            .IsUnique()
            .HasDatabaseName("ux_mail_archives_owner_fingerprint");
        builder.HasIndex(a => a.SourceFileItemId)
            .HasDatabaseName("ix_mail_archives_source_file");

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(a => a.OwnerUserId)
            .OnDelete(DeleteBehavior.Restrict);
        // Purging the PST file never deletes the archive or its messages.
        builder.HasOne<FileItem>()
            .WithMany()
            .HasForeignKey(a => a.SourceFileItemId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}

public class MailImportRunConfiguration : IEntityTypeConfiguration<MailImportRun>
{
    public void Configure(EntityTypeBuilder<MailImportRun> builder)
    {
        builder.ToTable("mail_import_runs");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedNever();

        builder.Property(r => r.Status).IsRequired().HasMaxLength(32);
        builder.Property(r => r.Phase).HasMaxLength(32);
        builder.Property(r => r.ErrorCode).HasMaxLength(64);
        builder.Property(r => r.ErrorMessage).HasMaxLength(300);
        builder.Property(r => r.CreatedAt).HasColumnType("timestamp with time zone");
        builder.Property(r => r.StartedAt).HasColumnType("timestamp with time zone");
        builder.Property(r => r.CompletedAt).HasColumnType("timestamp with time zone");
        builder.Property(r => r.UpdatedAt).HasColumnType("timestamp with time zone");

        builder.HasIndex(r => new { r.ArchiveId, r.CreatedAt })
            .HasDatabaseName("ix_mail_import_runs_archive_created");
        // At most one queued/running run per archive: two concurrent imports of
        // the same PST can never interleave on the same manifest.
        builder.HasIndex(r => r.ArchiveId)
            .IsUnique()
            .HasFilter("\"Status\" IN ('queued', 'running')")
            .HasDatabaseName("ux_mail_import_runs_one_active");

        builder.HasOne<Domain.Mail.MailArchive>()
            .WithMany()
            .HasForeignKey(r => r.ArchiveId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public class MailImportItemConfiguration : IEntityTypeConfiguration<MailImportItem>
{
    public void Configure(EntityTypeBuilder<MailImportItem> builder)
    {
        builder.ToTable("mail_import_items");
        builder.HasKey(i => i.Id);
        builder.Property(i => i.Id).ValueGeneratedNever();

        builder.Property(i => i.SourceMessageKey).IsRequired().HasMaxLength(32);
        builder.Property(i => i.SourceFolderKey).IsRequired().HasMaxLength(32);
        builder.Property(i => i.Status).IsRequired().HasMaxLength(32);
        builder.Property(i => i.FailureCode).HasMaxLength(64);
        builder.Property(i => i.FailureMessage).HasMaxLength(300);
        builder.Property(i => i.LastOutcome).HasMaxLength(32);
        builder.Property(i => i.CreatedAt).HasColumnType("timestamp with time zone");
        builder.Property(i => i.UpdatedAt).HasColumnType("timestamp with time zone");

        builder.HasIndex(i => new { i.ArchiveId, i.SourceMessageKey })
            .IsUnique()
            .HasDatabaseName("ux_mail_import_items_archive_message");
        builder.HasIndex(i => new { i.ArchiveId, i.Ordinal })
            .HasDatabaseName("ix_mail_import_items_archive_ordinal");
        builder.HasIndex(i => new { i.LastRunId, i.LastOutcome })
            .HasDatabaseName("ix_mail_import_items_run_outcome");

        builder.HasOne<Domain.Mail.MailArchive>()
            .WithMany()
            .HasForeignKey(i => i.ArchiveId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public class MailFolderConfiguration : IEntityTypeConfiguration<MailFolder>
{
    public void Configure(EntityTypeBuilder<MailFolder> builder)
    {
        builder.ToTable("mail_folders");
        builder.HasKey(f => f.Id);
        builder.Property(f => f.Id).ValueGeneratedNever();

        builder.Property(f => f.SourceFolderKey).IsRequired().HasMaxLength(32);
        builder.Property(f => f.Name).IsRequired().HasMaxLength(255);
        builder.Property(f => f.PathJson).IsRequired();
        builder.Property(f => f.ContainerClass).HasMaxLength(64);

        builder.HasIndex(f => new { f.ArchiveId, f.SourceFolderKey })
            .IsUnique()
            .HasDatabaseName("ux_mail_folders_archive_source");
        builder.HasIndex(f => new { f.ArchiveId, f.Ordinal })
            .HasDatabaseName("ix_mail_folders_archive_ordinal");

        builder.HasOne<Domain.Mail.MailArchive>()
            .WithMany()
            .HasForeignKey(f => f.ArchiveId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<MailFolder>()
            .WithMany()
            .HasForeignKey(f => f.ParentId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public class MailMessageConfiguration : IEntityTypeConfiguration<MailMessage>
{
    public void Configure(EntityTypeBuilder<MailMessage> builder)
    {
        builder.ToTable("mail_messages");
        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id).ValueGeneratedNever();

        builder.Property(m => m.SourceMessageKey).IsRequired().HasMaxLength(32);
        builder.Property(m => m.Status).IsRequired().HasMaxLength(16);
        builder.Property(m => m.DocumentFormat).IsRequired().HasMaxLength(64);
        builder.Property(m => m.MessageClass).HasMaxLength(255);
        builder.Property(m => m.Subject).HasMaxLength(1000);
        builder.Property(m => m.FromName).HasMaxLength(320);
        builder.Property(m => m.FromAddress).HasMaxLength(320);
        builder.Property(m => m.ToSummary).HasMaxLength(1000);
        builder.Property(m => m.Preview).HasMaxLength(400);
        builder.Property(m => m.InternetMessageId).HasMaxLength(998);
        builder.Property(m => m.SearchSubject).IsRequired().HasMaxLength(1000);
        builder.Property(m => m.SearchFrom).IsRequired().HasMaxLength(700);
        builder.Property(m => m.SearchRecipients).IsRequired().HasMaxLength(8000);
        builder.Property(m => m.SentAt).HasColumnType("timestamp with time zone");
        builder.Property(m => m.ReceivedAt).HasColumnType("timestamp with time zone");
        builder.Property(m => m.SortDate).HasColumnType("timestamp with time zone");
        builder.Property(m => m.CreatedAt).HasColumnType("timestamp with time zone");
        builder.Property(m => m.ImportedAt).HasColumnType("timestamp with time zone");

        builder.HasIndex(m => new { m.ArchiveId, m.SourceMessageKey })
            .IsUnique()
            .HasDatabaseName("ux_mail_messages_archive_source");
        // Folder list: newest first, keyset (SortDate, Id).
        builder.HasIndex(m => new { m.FolderId, m.SortDate, m.Id })
            .HasDatabaseName("ix_mail_messages_folder_date");
        // Owner-wide search ordered by date.
        builder.HasIndex(m => new { m.OwnerUserId, m.SortDate, m.Id })
            .HasDatabaseName("ix_mail_messages_owner_date");
        builder.HasIndex(m => new { m.ArchiveId, m.SortDate, m.Id })
            .HasDatabaseName("ix_mail_messages_archive_date");
        builder.HasIndex(m => m.BlobObjectId)
            .HasDatabaseName("ix_mail_messages_blob_object");

        builder.HasOne<Domain.Mail.MailArchive>()
            .WithMany()
            .HasForeignKey(m => m.ArchiveId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<MailFolder>()
            .WithMany()
            .HasForeignKey(m => m.FolderId)
            .OnDelete(DeleteBehavior.Restrict);
        // RESTRICT: the janitor can never remove a document a message still
        // owns; the reference is released with the row's hard delete.
        builder.HasOne<BlobObject>()
            .WithMany()
            .HasForeignKey(m => m.BlobObjectId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public class MailAttachmentConfiguration : IEntityTypeConfiguration<MailAttachment>
{
    public void Configure(EntityTypeBuilder<MailAttachment> builder)
    {
        builder.ToTable("mail_attachments");
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).ValueGeneratedNever();

        builder.Property(a => a.AttachmentKey).IsRequired().HasMaxLength(64);
        builder.Property(a => a.OriginalName).IsRequired().HasMaxLength(255);
        builder.Property(a => a.MimeType).HasMaxLength(255);
        builder.Property(a => a.ContentId).HasMaxLength(512);
        builder.Property(a => a.Method).IsRequired().HasMaxLength(32);
        builder.Property(a => a.Status).IsRequired().HasMaxLength(16);
        builder.Property(a => a.SkipReason).HasMaxLength(64);
        builder.Property(a => a.CreatedAt).HasColumnType("timestamp with time zone");

        builder.HasIndex(a => new { a.MessageId, a.AttachmentKey })
            .IsUnique()
            .HasDatabaseName("ux_mail_attachments_message_key");
        // Reverse lookup: which message did this file come from?
        builder.HasIndex(a => a.FileItemId)
            .HasDatabaseName("ix_mail_attachments_file_item");

        builder.HasOne<MailMessage>()
            .WithMany()
            .HasForeignKey(a => a.MessageId)
            .OnDelete(DeleteBehavior.Cascade);
        // A link never pins a file: purging the attachment's FileItem (through
        // any trigger) nulls the link instead of being refused by it.
        builder.HasOne<FileItem>()
            .WithMany()
            .HasForeignKey(a => a.FileItemId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}

public class MailDeletionTombstoneConfiguration : IEntityTypeConfiguration<MailDeletionTombstone>
{
    public void Configure(EntityTypeBuilder<MailDeletionTombstone> builder)
    {
        builder.ToTable("mail_deletion_tombstones");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).ValueGeneratedNever();

        builder.Property(t => t.SourceFingerprint).IsRequired().HasMaxLength(128);
        builder.Property(t => t.SourceMessageKey).IsRequired().HasMaxLength(32);
        builder.Property(t => t.DeletedAt).HasColumnType("timestamp with time zone");

        builder.HasIndex(t => new { t.OwnerUserId, t.SourceFingerprint, t.SourceMessageKey })
            .IsUnique()
            .HasDatabaseName("ux_mail_tombstones_owner_source_message");
        builder.HasIndex(t => new { t.OwnerUserId, t.DeletedMessageId })
            .HasDatabaseName("ix_mail_tombstones_owner_message");

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(t => t.OwnerUserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
