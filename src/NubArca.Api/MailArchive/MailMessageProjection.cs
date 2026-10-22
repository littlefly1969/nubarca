using System.Text;
using Microsoft.EntityFrameworkCore;
using NubArca.Api.Data;
using NubArca.Api.Domain.Mail;

namespace NubArca.Api.MailArchive;

// Projects a normalized message document onto the queryable columns of its
// mail_messages row. The document stays the source of truth (it is the blob);
// these columns exist for lists and structured search.
public static class MailMessageProjection
{
    private const int PreviewChars = 280;

    public static void Apply(MailMessage message, PstMessageDocument document, long documentBytes, DateTime now)
    {
        message.DocumentFormat = MailDocumentFormats.Message;
        message.DocumentVersion = document.Version ?? MailDocumentFormats.CurrentVersion;
        message.DocumentSizeBytes = documentBytes;
        message.MessageClass = MailImportRunner.Truncate(document.MessageClass, 255);
        message.Subject = MailImportRunner.Truncate(Clean(document.Subject), 1000);
        var from = document.From ?? document.Sender;
        message.FromName = MailImportRunner.Truncate(Clean(from?.Name), 320);
        message.FromAddress = MailImportRunner.Truncate(Clean(from?.Address), 320);
        message.ToSummary = MailImportRunner.Truncate(Summary(document.To), 1000);
        message.Preview = MailImportRunner.Truncate(Preview(document.Body?.Text), 400);
        message.SentAt = Utc(document.SentAt);
        message.ReceivedAt = Utc(document.ReceivedAt);
        message.SortDate = message.ReceivedAt ?? message.SentAt ?? Utc(document.CreatedAt) ?? now;
        message.InternetMessageId = MailImportRunner.Truncate(Clean(document.InternetMessageId), 998);
        var attachments = FlattenAttachments(document);
        message.AttachmentCount = attachments.Count(a => !a.Hidden);
        message.HasAttachments = message.AttachmentCount > 0 || document.HasAttachments == true;

        message.SearchSubject = MailImportRunner.Truncate(Lower(message.Subject), 1000)!;
        message.SearchFrom = MailImportRunner.Truncate(Lower(Join(from?.Name, from?.Address)), 700)!;
        message.SearchRecipients = MailImportRunner.Truncate(Lower(string.Join(' ',
            (document.To ?? []).Concat(document.Cc ?? []).Concat(document.Bcc ?? [])
                .Select(r => Join(r.Name, r.Address)))), 8000)!;
    }

    // Top-level attachments and, depth-first, the attachments of embedded
    // messages (their keys are dotted paths, contract §2.4).
    public static List<PstAttachmentDescriptor> FlattenAttachments(PstMessageDocument document)
    {
        var result = new List<PstAttachmentDescriptor>();
        Walk(document, result, depth: 0);
        return result;
    }

    private static void Walk(PstMessageDocument document, List<PstAttachmentDescriptor> result, int depth)
    {
        if (depth > 8 || document.Attachments is null)
        {
            return;
        }
        foreach (var attachment in document.Attachments)
        {
            if (string.IsNullOrWhiteSpace(attachment.Key) || attachment.Key.Length > 64)
            {
                continue;
            }
            if (attachment.EmbeddedMessage is { } embedded)
            {
                Walk(embedded, result, depth + 1);
                continue; // the embedded message itself is shown inline, not as a file
            }
            result.Add(attachment);
        }
    }

    public static string Lower(string? value) => (value ?? string.Empty).ToLowerInvariant();

    private static string Join(string? name, string? address) =>
        string.Join(' ', new[] { name, address }.Where(s => !string.IsNullOrWhiteSpace(s)));

    private static string? Summary(IReadOnlyList<PstMailAddress>? recipients)
    {
        if (recipients is null || recipients.Count == 0)
        {
            return null;
        }
        return string.Join(", ", recipients.Select(r => Clean(r.Name) ?? Clean(r.Address)).Where(s => s is not null));
    }

    private static string? Preview(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }
        var builder = new StringBuilder(PreviewChars);
        var space = false;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch) || char.IsControl(ch))
            {
                space = builder.Length > 0;
                continue;
            }
            if (space)
            {
                builder.Append(' ');
                space = false;
            }
            builder.Append(ch);
            if (builder.Length >= PreviewChars)
            {
                break;
            }
        }
        return builder.ToString();
    }

    private static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        var cleaned = new string(value.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return cleaned.Length == 0 ? null : cleaned;
    }

    private static DateTime? Utc(DateTime? value) => value switch
    {
        null => null,
        { Kind: DateTimeKind.Utc } v => v,
        { Kind: DateTimeKind.Local } v => v.ToUniversalTime(),
        var v => DateTime.SpecifyKind(v.Value, DateTimeKind.Utc),
    };
}

// Denormalized counts (archive and folder message counts), recomputed from
// the complete message rows. Called at the end of a run and after deletes.
public static class MailCounts
{
    public static async Task RecountAsync(AppDbContext db, Guid archiveId, CancellationToken cancellationToken)
    {
        var perFolder = await db.MailMessages.AsNoTracking()
            .Where(m => m.ArchiveId == archiveId && m.Status == MailMessageStatuses.Complete)
            .GroupBy(m => m.FolderId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);
        var counts = perFolder.ToDictionary(p => p.Key, p => p.Count);

        var folders = await db.MailFolders.Where(f => f.ArchiveId == archiveId).ToListAsync(cancellationToken);
        foreach (var folder in folders)
        {
            folder.MessageCount = counts.GetValueOrDefault(folder.Id);
        }
        var total = counts.Values.Sum();
        await db.SaveChangesAsync(cancellationToken);
        await db.MailArchives
            .Where(a => a.Id == archiveId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(a => a.MessageCount, total)
                .SetProperty(a => a.FolderCount, folders.Count), cancellationToken);
    }
}
