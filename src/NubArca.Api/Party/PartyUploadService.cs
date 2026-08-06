using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using NubArca.Api.Albums;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Files;
using NubArca.Api.Folders;
using NubArca.Api.Ingestion;
using NubArca.Api.Security;
using NubArca.Api.Storage;

namespace NubArca.Api.Party;

public sealed class PartyUploadService : IPartyUploadService
{
    // Anonymous uploads land in a single dedicated owner folder so they don't
    // clutter the owner's root; they're then added to the target party album.
    private const string PartyUploadsFolder = "Party uploads";

    // Conservative per-file ceiling for anonymous uploads (phone photos), on top
    // of the global Storage:MaxUploadBytes. Overridable via Party:MaxUploadBytes.
    private const long DefaultMaxUploadBytes = 50L * 1024 * 1024;

    private readonly AppDbContext _db;
    private readonly IFileItemService _files;
    private readonly IFolderService _folders;
    private readonly IAlbumService _albums;
    private readonly IPostIngestionMediaPipelineService _mediaPipeline;
    private readonly long _maxBytes;

    public PartyUploadService(
        AppDbContext db,
        IFileItemService files,
        IFolderService folders,
        IAlbumService albums,
        IPostIngestionMediaPipelineService mediaPipeline,
        TimeProvider clock,
        IConfiguration config)
    {
        _db = db;
        _files = files;
        _folders = folders;
        _albums = albums;
        _mediaPipeline = mediaPipeline;
        _clock = clock;
        var configured = config.GetValue<long?>("Party:MaxUploadBytes");
        _maxBytes = configured is > 0 ? configured.Value : DefaultMaxUploadBytes;
    }

    private readonly TimeProvider _clock;

    public async Task<PartyUploadOutcome> UploadAsync(
        Guid ownerUserId,
        Guid albumId,
        string fileName,
        string? declaredContentType,
        long declaredLength,
        Stream content,
        Guid? partyAlbumLinkId = null,
        bool requireApproval = false,
        CancellationToken cancellationToken = default)
    {
        if (declaredLength > _maxBytes)
        {
            return PartyUploadOutcome.RejectedTooLarge;
        }

        // Cheap pre-gate: the client-declared type must be an allowed image. This
        // is NOT authoritative (client MIME is untrusted) — the server-detected
        // media category is re-checked after ingest below.
        if (!SafeContentType.IsTrustedImage(declaredContentType))
        {
            return PartyUploadOutcome.RejectedType;
        }

        var safeName = BuildSafeName(fileName);
        var folderId = await _folders.EnsureFolderPathAsync(
            ownerUserId, null, [PartyUploadsFolder], cancellationToken);

        FileItem created;
        try
        {
            created = await _files.CreateAsync(
                ownerUserId, folderId, safeName, declaredContentType, content, cancellationToken);
        }
        catch (UploadTooLargeException)
        {
            return PartyUploadOutcome.RejectedTooLarge;
        }
        catch (QuotaExceededException)
        {
            return PartyUploadOutcome.Failed;
        }
        catch (Exception)
        {
            // Never surface storage/decoder internals or stack traces to an
            // anonymous caller — collapse to a safe failure.
            return PartyUploadOutcome.Failed;
        }

        // Authoritative image gate. The client MIME is untrusted (MediaCategory
        // can be derived from it), so the decisive signal is that the server
        // actually DECODED the bytes as an image — i.e. real pixel dimensions
        // were extracted. A file that lied about its type (e.g. HTML/executable
        // as image/png) has no dimensions and is rejected + removed here, so it
        // never enters the album or the owner's active library.
        var category = await _db.BlobMetadata
            .AsNoTracking()
            .Where(m => m.BlobObjectId == created.BlobObjectId)
            .Select(m => m.MediaCategory)
            .FirstOrDefaultAsync(cancellationToken);
        var decodedAsImage = created.Width is > 0 && created.Height is > 0;
        if (!decodedAsImage || category != MediaCategories.Image)
        {
            await _files.SoftDeleteAsync(ownerUserId, created.Id, cancellationToken);
            return PartyUploadOutcome.RejectedNotImage;
        }

        var added = await _albums.AddItemAsync(albumId, ownerUserId, created.Id, cancellationToken);
        if (!added)
        {
            // Album vanished / no longer owned between token-resolve and now.
            await _files.SoftDeleteAsync(ownerUserId, created.Id, cancellationToken);
            return PartyUploadOutcome.Failed;
        }

        // Record the moderation state for this guest upload. Pending (invisible)
        // when the album requires approval, else approved (immediately visible —
        // the default). This is a VISIBILITY control only; the owner's stored file
        // is untouched. Owner-added content never gets a row, so it stays visible.
        var now = _clock.GetUtcNow().UtcDateTime;
        _db.PartyUploadItems.Add(new PartyUploadItem
        {
            Id = Guid.NewGuid(),
            OwnerUserId = ownerUserId,
            AlbumId = albumId,
            PartyAlbumLinkId = partyAlbumLinkId,
            FileItemId = created.Id,
            Status = requireApproval ? PartyUploadStatuses.Pending : PartyUploadStatuses.Approved,
            UploadedAt = now,
            ModeratedAt = null,
            ModeratedByUserId = null,
        });
        await _db.SaveChangesAsync(cancellationToken);

        // Best-effort: kick off the medium-preview/derivative + metadata jobs so
        // the photo renders in the party grid. A failure here never fails the
        // upload (party media is also generated lazily on first request).
        try
        {
            await _mediaPipeline.OnPartyFileIngestedAsync(ownerUserId, created.Id, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // swallowed — safe, aggregate-only
        }

        return PartyUploadOutcome.Accepted;
    }

    // Strip any directory components (path-traversal safety), keep a bounded,
    // printable name, and prefix a short unique token so concurrent guests
    // uploading "IMG_0001.jpg" don't collide on the owner's active-sibling name.
    private static string BuildSafeName(string? fileName)
    {
        var baseName = Path.GetFileName(fileName ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(baseName))
        {
            baseName = "photo.jpg";
        }
        baseName = new string(baseName.Where(c => !char.IsControl(c)).ToArray());
        if (baseName.Length > 120)
        {
            baseName = baseName[^120..];
        }
        var unique = Guid.NewGuid().ToString("N")[..8];
        return $"party-{unique}-{baseName}";
    }
}
