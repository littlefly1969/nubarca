using Microsoft.EntityFrameworkCore;
using NubArca.Api.Ai.Documents.Sources;
using NubArca.Api.Audit;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Domain.Ai;
using NubArca.Api.Files;
using NubArca.Api.Folders;
using NubArca.Api.Ingestion;

namespace NubArca.Api.Uploads;

/// <summary>
/// What happens when the chosen name is already taken by an active sibling.
/// </summary>
public enum UploadNameConflictPolicy
{
    // The historical contract of POST /api/files: 409, nothing stored. Kept as
    // the default so the mobile sync client, which relies on it, is unchanged.
    Fail,

    // "IMG_0001.jpg" becomes "IMG_0001 (2).jpg". What a person dropping photos
    // into a folder expects: their file arrives, it does not bounce.
    Rename,
}

public static class UploadNameConflictPolicies
{
    public static bool TryParse(string? raw, out UploadNameConflictPolicy policy)
    {
        switch (raw?.Trim().ToLowerInvariant())
        {
            case null or "" or "fail":
                policy = UploadNameConflictPolicy.Fail;
                return true;
            case "rename":
                policy = UploadNameConflictPolicy.Rename;
                return true;
            default:
                policy = UploadNameConflictPolicy.Fail;
                return false;
        }
    }
}

/// <summary>One owner upload, whatever carried the bytes to the server.</summary>
public sealed record OwnerUploadRequest(
    Guid OwnerUserId,
    Guid? ParentFolderId,
    string FileName,
    string? RelativePath,
    string? ContentType,
    UploadNameConflictPolicy Conflict,
    Guid? UploadOperationClaimToken,
    string? IpAddress);

public interface IOwnerUploadIngestion
{
    /// <summary>
    /// Stores <paramref name="content"/> as the owner's file: folder chain from
    /// the relative path, name-conflict policy, content-addressed ingest, audit
    /// and the post-ingestion pipeline. Throws the same exceptions as
    /// <see cref="IFileItemService.CreateAsync"/> so every transport maps them
    /// to the same HTTP answers.
    /// </summary>
    Task<FileItem> IngestAsync(OwnerUploadRequest request, Stream content, CancellationToken cancellationToken);
}

// ONE INGEST FOR EVERY OWNER UPLOAD. The single multipart POST and the
// resumable chunked upload used to be two ways in; they now differ only in how
// the bytes arrived. Everything after that — where the file lands, what it is
// called, the audit line, the media pipeline — is decided here, once.
public sealed class OwnerUploadIngestion : IOwnerUploadIngestion
{
    // A name is retried at most this many times against a concurrent upload
    // taking the same "(n)" slot. Collisions past the first are rare; a bound
    // keeps a pathological folder from spinning.
    private const int MaxRenameAttempts = 5;

    private readonly AppDbContext _db;
    private readonly IFileItemService _files;
    private readonly IFolderService _folders;
    private readonly IAuditLogger _audit;
    private readonly IPostIngestionMediaPipelineService _mediaPipeline;
    private readonly IDocumentSourceChangeSink? _documentSources;
    private readonly ILogger<OwnerUploadIngestion> _logger;

    public OwnerUploadIngestion(
        AppDbContext db,
        IFileItemService files,
        IFolderService folders,
        IAuditLogger audit,
        IPostIngestionMediaPipelineService mediaPipeline,
        ILogger<OwnerUploadIngestion> logger,
        IDocumentSourceChangeSink? documentSources = null)
    {
        _documentSources = documentSources;
        _db = db;
        _files = files;
        _folders = folders;
        _audit = audit;
        _mediaPipeline = mediaPipeline;
        _logger = logger;
    }

    public async Task<FileItem> IngestAsync(
        OwnerUploadRequest request, Stream content, CancellationToken cancellationToken)
    {
        // Slice 76: the relative path (browser webkitRelativePath) is never
        // trusted; it is validated and its directories become owner-scoped
        // logical folders. No path → a plain upload into ParentFolderId.
        var parsed = RelativeUploadPath.Parse(
            string.IsNullOrWhiteSpace(request.RelativePath) ? null : request.RelativePath,
            request.FileName);

        var targetFolderId = await _folders.EnsureFolderPathAsync(
            request.OwnerUserId, request.ParentFolderId, parsed.Directories, cancellationToken);

        var name = request.Conflict == UploadNameConflictPolicy.Rename
            ? await FreeNameAsync(request.OwnerUserId, targetFolderId, parsed.FileName, cancellationToken)
            : parsed.FileName;

        var startPosition = content.CanSeek ? content.Position : 0;
        FileItem created;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                created = await _files.CreateAsync(
                    request.OwnerUserId,
                    targetFolderId,
                    name,
                    request.ContentType,
                    content,
                    cancellationToken,
                    uploadOperationClaimToken: request.UploadOperationClaimToken);
                break;
            }
            catch (DuplicateFileNameException)
                when (request.Conflict == UploadNameConflictPolicy.Rename
                    && content.CanSeek
                    && attempt < MaxRenameAttempts)
            {
                // Another upload took the free name between the lookup and the
                // insert. The bytes are re-read from the start under the next
                // free name; the first attempt's blob reference was released by
                // CreateAsync, so nothing is left behind.
                content.Position = startPosition;
                name = await FreeNameAsync(request.OwnerUserId, targetFolderId, parsed.FileName, cancellationToken);
            }
        }

        await _audit.LogAsync(
            actor: request.OwnerUserId,
            action: AuditActions.FileUpload,
            entityType: AuditEntityTypes.File,
            entityId: created.Id,
            ipAddress: request.IpAddress,
            metadata: new { name = created.Name, mimeType = created.MimeType, sizeBytes = created.SizeBytes, parentFolderId = created.ParentFolderId },
            cancellationToken: cancellationToken);

        // The bounded, idempotent post-ingestion pipeline (previews, AI,
        // pending metadata) is scheduled WITHOUT blocking on decode/encode.
        // A scheduling failure never fails an upload that is already durable;
        // a later backfill picks the file up.
        try
        {
            await _mediaPipeline.OnFileIngestedAsync(request.OwnerUserId, created.Id, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Post-ingest scheduling failed after upload; file will be picked up by a later backfill.");
        }

        // THE DOCUMENT CHANGE SIGNAL. Records that a source changed and returns:
        // the upload never waits for extraction or embeddings. Inert unless the
        // document capability is enabled; a non-document file enqueues nothing.
        if (_documentSources is not null)
        {
            await _documentSources.SourceChangedAsync(
                DocumentSourceKinds.File, request.OwnerUserId, created.Id, cancellationToken);
        }

        return created;
    }

    // The first of "name", "name (2)", "name (3)" … that no ACTIVE sibling in
    // the folder holds. One query reads every candidate's competitor at once.
    private async Task<string> FreeNameAsync(
        Guid ownerUserId, Guid? folderId, string requested, CancellationToken cancellationToken)
    {
        var trimmed = requested.Trim();
        var (stem, _) = SplitName(trimmed);
        var prefix = stem + " (";

        var taken = await _db.FileItems.AsNoTracking()
            .Where(f => f.OwnerUserId == ownerUserId
                && f.ParentFolderId == folderId
                && f.DeletedAt == null
                && (f.Name == trimmed || f.Name.StartsWith(prefix)))
            .Select(f => f.Name)
            .ToListAsync(cancellationToken);
        return NextFreeName(trimmed, taken);
    }

    internal static string NextFreeName(string requested, IReadOnlyCollection<string> taken)
    {
        var set = taken as ISet<string> ?? new HashSet<string>(taken, StringComparer.Ordinal);
        if (!set.Contains(requested)) return requested;

        var (stem, extension) = SplitName(requested);
        for (var n = 2; ; n++)
        {
            var suffix = $" ({n}){extension}";
            // Keep the result inside the 255-character name limit by shortening
            // the stem, never the extension or the counter.
            var room = 255 - suffix.Length;
            var candidate = (stem.Length > room ? stem[..Math.Max(1, room)] : stem) + suffix;
            if (!set.Contains(candidate)) return candidate;
        }
    }

    // "IMG_0001.jpg" → ("IMG_0001", ".jpg"); ".bashrc" and "README" keep the
    // whole name as the stem, so a counter never lands before a leading dot.
    private static (string Stem, string Extension) SplitName(string name)
    {
        var dot = name.LastIndexOf('.');
        return dot > 0 && dot < name.Length - 1
            ? (name[..dot], name[dot..])
            : (name, string.Empty);
    }
}
