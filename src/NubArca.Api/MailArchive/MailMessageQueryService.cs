using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NubArca.Api.Data;
using NubArca.Api.Domain.Mail;
using NubArca.Api.Storage;

namespace NubArca.Api.MailArchive;

/// <summary>
/// Owner-private reads: message lists and structured search (keyset by date),
/// the message reader, and the reverse lookup from an attachment file to the
/// message it came from. Only complete messages are ever returned.
/// </summary>
public sealed class MailMessageQueryService
{
    public const int MaxLimit = 100;
    public const int DefaultLimit = 50;
    private const int MaxTermLength = 200;

    private readonly AppDbContext _db;
    private readonly IBlobService _blobs;
    private readonly MailTextBackfillService _texts;
    private readonly ILogger<MailMessageQueryService> _logger;

    public MailMessageQueryService(AppDbContext db, IBlobService blobs, MailTextBackfillService texts,
        ILogger<MailMessageQueryService> logger)
    {
        _db = db;
        _blobs = blobs;
        _texts = texts;
        _logger = logger;
    }

    public async Task<MailMessagePage?> SearchAsync(Guid owner, MailMessageQuery query, CancellationToken cancellationToken)
    {
        var limit = Math.Clamp(query.Limit <= 0 ? DefaultLimit : query.Limit, 1, MaxLimit);
        if (Page(owner, query, limit) is not { } page)
        {
            return null;
        }
        var rows = await page
            .Select(m => new
            {
                m.Id, m.ArchiveId, m.FolderId, m.Subject, m.FromName, m.FromAddress, m.ToSummary,
                m.SortDate, m.HasAttachments, m.AttachmentCount, m.Preview,
            })
            .ToListAsync(cancellationToken);

        var items = rows.Take(limit)
            .Select(m => new MailMessageSummaryDto(
                m.Id, m.ArchiveId, m.FolderId, m.Subject, Address(m.FromName, m.FromAddress), m.ToSummary,
                m.SortDate, m.HasAttachments, m.AttachmentCount, m.Preview))
            .ToList();
        var next = rows.Count > limit ? EncodeCursor(rows[limit - 1].SortDate, rows[limit - 1].Id) : null;
        // Only a term with words reaches the text; say when it cannot reach all of it.
        var bodySearchIncomplete = Term(query.Q) is { } q && Words(q).Count > 0
            && await _texts.EnsureAsync(owner, cancellationToken);
        return new MailMessagePage(items, next, bodySearchIncomplete);
    }

    // The filtered, ordered page (one row more than the limit, to know if
    // there is a next page); null for a malformed cursor. Separate so the
    // PostgreSQL plan of exactly this query can be measured.
    internal IQueryable<MailMessage>? Page(Guid owner, MailMessageQuery query, int limit)
    {
        var messages = _db.MailMessages.AsNoTracking()
            .Where(m => m.OwnerUserId == owner && m.Status == MailMessageStatuses.Complete);

        if (query.ArchiveId is Guid archiveId)
        {
            messages = messages.Where(m => m.ArchiveId == archiveId);
        }
        if (query.FolderId is Guid folderId)
        {
            messages = messages.Where(m => m.FolderId == folderId);
        }
        if (Term(query.Subject) is { } subject)
        {
            messages = messages.Where(m => m.SearchSubject.Contains(subject));
        }
        if (Term(query.From) is { } from)
        {
            messages = messages.Where(m => m.SearchFrom.Contains(from));
        }
        if (Term(query.To) is { } to)
        {
            messages = messages.Where(m => m.SearchRecipients.Contains(to));
        }
        if (query.After is DateTime after)
        {
            var a = DateTime.SpecifyKind(after, DateTimeKind.Utc);
            messages = messages.Where(m => m.SortDate >= a);
        }
        if (query.Before is DateTime before)
        {
            var b = DateTime.SpecifyKind(before, DateTimeKind.Utc);
            messages = messages.Where(m => m.SortDate < b);
        }
        if (query.HasAttachments is bool hasAttachments)
        {
            messages = messages.Where(m => m.HasAttachments == hasAttachments);
        }
        if (query.Cursor is not null)
        {
            if (!TryParseCursor(query.Cursor, out var cursorDate, out var cursorId))
            {
                return null;
            }
            messages = messages.Where(m => m.SortDate < cursorDate
                || (m.SortDate == cursorDate && m.Id.CompareTo(cursorId) < 0));
        }

        if (Term(query.Q) is { } q)
        {
            // Two pages, merged: the header matches (trigram indexes) and the
            // body matches (full-text index), each its own top-N, so neither
            // search has to filter every message of the owner.
            var header = Newest(messages.Where(m => m.SearchSubject.Contains(q)
                || m.SearchFrom.Contains(q) || m.SearchRecipients.Contains(q)), limit);
            if (BodyMatches(owner, q) is { } texts)
            {
                // Ordered by the text row's own copy of the date: no index
                // yields that order, so PostgreSQL takes every match from the
                // full-text index and sorts them, instead of walking the
                // owner's messages by date and testing each one's text.
                var body = texts
                    .Join(messages, t => t.MessageId, m => m.Id, (t, m) => new { t.SortDate, t.MessageId, Message = m })
                    .OrderByDescending(x => x.SortDate)
                    .ThenByDescending(x => x.MessageId)
                    .Take(limit + 1)
                    .Select(x => x.Message);
                return Newest(header.Union(body), limit);
            }
            return header;
        }
        return Newest(messages, limit);
    }

    private static IQueryable<MailMessage> Newest(IQueryable<MailMessage> messages, int limit) => messages
        .OrderByDescending(m => m.SortDate)
        .ThenByDescending(m => m.Id)
        .Take(limit + 1);

    public async Task<MailMessageDetailDto?> GetAsync(Guid owner, Guid messageId, CancellationToken cancellationToken)
    {
        var message = await _db.MailMessages.AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == messageId && m.OwnerUserId == owner
                && m.Status == MailMessageStatuses.Complete, cancellationToken);
        if (message?.BlobObjectId is not Guid blobId)
        {
            return null;
        }
        var archiveName = await _db.MailArchives.AsNoTracking()
            .Where(a => a.Id == message.ArchiveId)
            .Select(a => a.DisplayName)
            .FirstAsync(cancellationToken);
        var folderPath = await _db.MailFolders.AsNoTracking()
            .Where(f => f.Id == message.FolderId)
            .Select(f => f.PathJson)
            .FirstAsync(cancellationToken);

        var links = await _db.MailAttachments.AsNoTracking()
            .Where(a => a.MessageId == message.Id)
            .OrderBy(a => a.Ordinal)
            .GroupJoin(
                _db.FileItems.AsNoTracking().Where(f => f.OwnerUserId == owner && f.DeletedAt == null),
                a => a.FileItemId, f => (Guid?)f.Id,
                (a, files) => new { Link = a, Files = files })
            .SelectMany(x => x.Files.DefaultIfEmpty(), (x, f) => new { x.Link, FileName = f == null ? null : f.Name, Live = f != null })
            .ToListAsync(cancellationToken);

        var attachments = links.Select(x => new MailAttachmentViewDto(
                x.Link.Id,
                x.Link.OriginalName,
                x.Link.MimeType,
                x.Link.SizeBytes,
                x.Link.IsInline,
                x.Link.ContentId,
                EmbeddedPath(x.Link.AttachmentKey),
                x.Link.Status != MailAttachmentStatuses.Linked ? MailAttachmentStates.NotExtracted
                    : x.Live ? MailAttachmentStates.Available : MailAttachmentStates.FileDeleted,
                x.Link.Status == MailAttachmentStatuses.Linked ? null : x.Link.SkipReason ?? "not_extracted",
                x.Live ? x.Link.FileItemId : null,
                x.FileName))
            .ToList();

        var document = await ReadDocumentAsync(blobId, cancellationToken);
        var cids = links
            .Where(x => x.Live && x.Link.ContentId is not null && x.Link.FileItemId is not null)
            .GroupBy(x => NormalizeCid(x.Link.ContentId!), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Link.FileItemId!.Value, StringComparer.OrdinalIgnoreCase);

        return new MailMessageDetailDto(
            message.Id,
            message.ArchiveId,
            message.FolderId,
            message.Subject,
            Address(message.FromName, message.FromAddress),
            message.ToSummary,
            message.SortDate,
            message.HasAttachments,
            message.AttachmentCount,
            message.Preview,
            archiveName,
            MailArchiveService.ParsePath(folderPath),
            Addresses(document?.To),
            Addresses(document?.Cc),
            Addresses(document?.Bcc),
            message.SentAt,
            message.ReceivedAt,
            message.InternetMessageId,
            message.MessageClass,
            Body(document, cids),
            attachments,
            document is null ? [] : Embedded(document, prefix: null, depth: 0));
    }

    public async Task<IReadOnlyList<MailFileOriginDto>> FileOriginsAsync(Guid owner, Guid fileItemId, CancellationToken cancellationToken)
    {
        return await _db.MailAttachments.AsNoTracking()
            .Where(a => a.FileItemId == fileItemId && a.OwnerUserId == owner)
            // A file in the trash no longer leads anywhere (same rule as the
            // attachment state in the message detail).
            .Where(a => _db.FileItems.Any(f => f.Id == fileItemId && f.OwnerUserId == owner && f.DeletedAt == null))
            .Join(_db.MailMessages.AsNoTracking().Where(m => m.OwnerUserId == owner && m.Status == MailMessageStatuses.Complete),
                a => a.MessageId, m => m.Id, (a, m) => new { a, m })
            .Join(_db.MailArchives.AsNoTracking(), x => x.m.ArchiveId, ar => ar.Id, (x, ar) => new { x.a, x.m, ar.DisplayName })
            .OrderByDescending(x => x.m.SortDate)
            .Take(20)
            .Select(x => new MailFileOriginDto(
                x.m.Id, x.m.ArchiveId, x.DisplayName, x.m.Subject,
                x.m.FromName == null && x.m.FromAddress == null ? null : new MailAddressDto(x.m.FromName, x.m.FromAddress),
                x.m.SortDate, x.a.OriginalName))
            .ToListAsync(cancellationToken);
    }

    // ---- document -----------------------------------------------------------------

    private async Task<PstMessageDocument?> ReadDocumentAsync(Guid blobId, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await _blobs.OpenContentAsync(blobId, cancellationToken);
            return await JsonSerializer.DeserializeAsync<PstMessageDocument>(stream, PstProtocol.Json, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or FileNotFoundException)
        {
            // Never the content, never the key: only that it could not be read.
            _logger.LogWarning("MailArchive: a message document could not be read ({Error}).", ex.GetType().Name);
            return null;
        }
    }

    private static MailBodyDto Body(PstMessageDocument? document, IReadOnlyDictionary<string, Guid> cids)
    {
        var body = document?.Body;
        var truncated = body?.Truncated is { } t && (t.Text || t.Html || t.Rtf);
        if (!string.IsNullOrWhiteSpace(body?.Html))
        {
            var html = MailHtmlSanitizer.Sanitize(body.Html, cid =>
                cids.TryGetValue(NormalizeCid(cid), out var fileId) ? $"/api/files/{fileId}/content" : null);
            return new MailBodyDto("html", html, body.Text, truncated);
        }
        if (!string.IsNullOrWhiteSpace(body?.Text))
        {
            return new MailBodyDto("text", null, body.Text, truncated);
        }
        if (!string.IsNullOrWhiteSpace(body?.Rtf))
        {
            return new MailBodyDto("rtf", null, body.Rtf, truncated);
        }
        return new MailBodyDto("none", null, null, truncated);
    }

    private static List<MailEmbeddedMessageDto> Embedded(PstMessageDocument document, string? prefix, int depth)
    {
        var result = new List<MailEmbeddedMessageDto>();
        if (depth > 8 || document.Attachments is null)
        {
            return result;
        }
        foreach (var attachment in document.Attachments)
        {
            if (attachment.EmbeddedMessage is not { } embedded)
            {
                continue;
            }
            var from = embedded.From ?? embedded.Sender;
            result.Add(new MailEmbeddedMessageDto(
                attachment.Key,
                embedded.Subject,
                from is null ? null : new MailAddressDto(from.Name, from.Address),
                embedded.ReceivedAt ?? embedded.SentAt,
                embedded.Body?.Text ?? embedded.Body?.Rtf));
            result.AddRange(Embedded(embedded, attachment.Key, depth + 1));
        }
        return result;
    }

    private static IReadOnlyList<MailAddressDto> Addresses(IReadOnlyList<PstMailAddress>? list) =>
        list is null ? [] : list.Select(a => new MailAddressDto(a.Name, a.Address)).ToList();

    private static MailAddressDto? Address(string? name, string? address) =>
        name is null && address is null ? null : new MailAddressDto(name, address);

    private static string? EmbeddedPath(string key)
    {
        var dot = key.LastIndexOf('.');
        return dot > 0 ? key[..dot] : null;
    }

    private static string NormalizeCid(string cid) => cid.Trim().Trim('<', '>');

    // The owner's messages whose text holds every word of the term: on
    // PostgreSQL as word prefixes, through the full-text GIN index on
    // to_tsvector('simple', "Text") ("fattur" finds "fatture"); elsewhere as
    // substrings. Null when the term has no word of two characters or more.
    private IQueryable<MailMessageText>? BodyMatches(Guid owner, string term)
    {
        var words = Words(term);
        if (words.Count == 0)
        {
            return null;
        }
        var texts = _db.MailMessageTexts.AsNoTracking().Where(t => t.OwnerUserId == owner);
        if (_db.Database.IsNpgsql())
        {
            // Letters and digits only, so nothing in it is tsquery syntax.
            var tsQuery = string.Join(" & ", words.Select(w => w + ":*"));
            texts = texts.Where(t => EF.Functions.ToTsVector("simple", t.Text)
                .Matches(EF.Functions.ToTsQuery("simple", tsQuery)));
        }
        else
        {
            foreach (var word in words)
            {
                texts = texts.Where(t => t.Text.Contains(word));
            }
        }
        return texts;
    }

    internal static List<string> Words(string term)
    {
        var words = new List<string>();
        var current = new StringBuilder();
        foreach (var ch in term + " ")
        {
            if (char.IsLetterOrDigit(ch))
            {
                current.Append(char.ToLowerInvariant(ch));
                continue;
            }
            if (current.Length >= 2 && current.Length <= 64 && !words.Contains(current.ToString()))
            {
                words.Add(current.ToString());
            }
            current.Clear();
        }
        return words.Take(8).ToList();
    }

    private static string? Term(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }
        var trimmed = raw.Trim();
        return (trimmed.Length > MaxTermLength ? trimmed[..MaxTermLength] : trimmed).ToLowerInvariant();
    }

    internal static string EncodeCursor(DateTime sortDate, Guid id) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(
            $"1|{sortDate.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture)}|{id:N}"))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    internal static bool TryParseCursor(string cursor, out DateTime sortDate, out Guid id)
    {
        sortDate = default;
        id = default;
        try
        {
            var padded = cursor.Replace('-', '+').Replace('_', '/');
            padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(padded)).Split('|');
            if (parts.Length != 3 || parts[0] != "1"
                || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
                || ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks
                || !Guid.TryParseExact(parts[2], "N", out id))
            {
                return false;
            }
            sortDate = new DateTime(ticks, DateTimeKind.Utc);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
