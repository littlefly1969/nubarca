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

    // Per-file ceilings for anonymous uploads, on top of the global
    // Storage:MaxUploadBytes. Images keep the historical 50 MiB (phone photos);
    // videos get their OWN, larger ceiling, because raising one number for both
    // would quietly grant every photo endpoint a half-gigabyte allowance.
    private const long DefaultMaxImageUploadBytes = 50L * 1024 * 1024;
    private const long DefaultMaxVideoUploadBytes = 512L * 1024 * 1024;

    private readonly AppDbContext _db;
    private readonly IFileItemService _files;
    private readonly IFolderService _folders;
    private readonly IAlbumService _albums;
    private readonly IPostIngestionMediaPipelineService _mediaPipeline;
    private readonly IPartyParticipantService _participants;
    private readonly TimeProvider _clock;
    private readonly long _maxImageBytes;
    private readonly long _maxVideoBytes;

    public PartyUploadService(
        AppDbContext db,
        IFileItemService files,
        IFolderService folders,
        IAlbumService albums,
        IPostIngestionMediaPipelineService mediaPipeline,
        IPartyParticipantService participants,
        TimeProvider clock,
        IConfiguration config)
    {
        _db = db;
        _files = files;
        _folders = folders;
        _albums = albums;
        _mediaPipeline = mediaPipeline;
        _participants = participants;
        _clock = clock;

        // Party:MaxUploadBytes is the historical, photo-oriented key and stays
        // authoritative for images so an operator who already set it keeps
        // exactly the behaviour they configured. Party:MaxImageUploadBytes is
        // the new explicit name and wins when both are present.
        var legacy = config.GetValue<long?>("Party:MaxUploadBytes");
        var image = config.GetValue<long?>("Party:MaxImageUploadBytes") ?? legacy;
        _maxImageBytes = image is > 0 ? image.Value : DefaultMaxImageUploadBytes;
        var video = config.GetValue<long?>("Party:MaxVideoUploadBytes");
        _maxVideoBytes = video is > 0 ? video.Value : DefaultMaxVideoUploadBytes;
    }

    public async Task<PartyUploadOutcome> UploadAsync(
        Guid ownerUserId,
        Guid albumId,
        string fileName,
        string? declaredContentType,
        long declaredLength,
        Stream content,
        Guid? partyAlbumLinkId = null,
        bool requireApproval = false,
        Guid? participantId = null,
        int maxPhotos = 0,
        int maxVideos = 0,
        CancellationToken cancellationToken = default)
    {
        // Cheap pre-gate: the client-declared type must be an allowed image OR an
        // allowed video. This is NOT authoritative (client MIME is untrusted) —
        // it only avoids ingesting bytes that cannot possibly be wanted. The
        // server-detected category is re-checked after ingest below.
        var declaredImage = SafeContentType.IsTrustedImage(declaredContentType);
        var declaredVideo = SafeContentType.IsTrustedVideo(declaredContentType);
        if (!declaredImage && !declaredVideo)
        {
            return PartyUploadOutcome.RejectedType;
        }

        // Size is gated per KIND, using the declared type. A liar can only ever
        // buy itself the smaller allowance or fail the authoritative gate later;
        // the global Storage:MaxUploadBytes still bounds the stream regardless.
        if (declaredLength > (declaredVideo ? _maxVideoBytes : _maxImageBytes))
        {
            return PartyUploadOutcome.RejectedTooLarge;
        }

        var safeName = BuildSafeName(fileName, declaredVideo);
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

        // AUTHORITATIVE media gate. The client MIME is untrusted, so the decisive
        // signal is what the SERVER made of the bytes:
        //   image → real pixel dimensions were decoded;
        //   video → the header sniffer or a completed ffprobe confirmed a video.
        // A file that lied about its type (HTML/executable as image/png or as
        // .mp4) satisfies neither and is removed here, so it never enters the
        // album, the owner's active library, the public party page or the TV.
        var meta = await _db.BlobMetadata
            .AsNoTracking()
            .Where(m => m.BlobObjectId == created.BlobObjectId)
            .Select(m => new { m.MediaCategory, m.DetectedContentType, m.VideoExtractionStatus, m.VideoCodec })
            .FirstOrDefaultAsync(cancellationToken);

        var isImage = meta?.MediaCategory == MediaCategories.Image
            && created.Width is > 0 && created.Height is > 0;
        var isVideo = meta?.MediaCategory == MediaCategories.Video
            && SafeContentType.IsServerConfirmedVideo(
                meta.DetectedContentType, meta.VideoExtractionStatus, meta.VideoCodec);

        if (!isImage && !isVideo)
        {
            await _files.SoftDeleteAsync(ownerUserId, created.Id, cancellationToken);
            return PartyUploadOutcome.RejectedNotMedia;
        }

        // The quota category is only knowable HERE — after the server decided
        // what the bytes actually are — which is why the slot is claimed now and
        // not at the door.
        var max = isVideo ? maxVideos : maxPhotos;
        var exhausted = isVideo
            ? PartyUploadOutcome.QuotaVideoExhausted
            : PartyUploadOutcome.QuotaPhotoExhausted;

        var now = _clock.GetUtcNow().UtcDateTime;
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            if (participantId is Guid participant)
            {
                if (!await _participants.TryClaimSlotAsync(participant, isVideo, max, cancellationToken))
                {
                    // Quota is full. Nothing is added to the album and no
                    // moderation row is written, so the file never becomes
                    // visible anywhere; the ingested bytes are dropped below.
                    await transaction.RollbackAsync(cancellationToken);
                    await _files.SoftDeleteAsync(ownerUserId, created.Id, cancellationToken);
                    return exhausted;
                    // (nothing was tracked before this point — the claim is a
                    //  single SQL statement, so the context needs no cleanup)
                }
            }

            var added = await _albums.AddItemAsync(albumId, ownerUserId, created.Id, cancellationToken);
            if (!added)
            {
                // Album vanished / no longer owned between token-resolve and now.
                // Rolling back returns the claimed slot: the guest was not the
                // reason this failed, so it must not cost them an upload.
                await transaction.RollbackAsync(cancellationToken);
                _db.ChangeTracker.Clear();
                await _files.SoftDeleteAsync(ownerUserId, created.Id, cancellationToken);
                return PartyUploadOutcome.Failed;
            }

            // Record the moderation state for this guest upload. Pending
            // (invisible) when the album requires approval, else approved
            // (immediately visible — the default). This is a VISIBILITY control
            // only; the owner's stored file is untouched. Owner-added content
            // never gets a row, so it stays visible.
            _db.PartyUploadItems.Add(new PartyUploadItem
            {
                Id = Guid.NewGuid(),
                OwnerUserId = ownerUserId,
                AlbumId = albumId,
                PartyAlbumLinkId = partyAlbumLinkId,
                PartyParticipantId = participantId,
                FileItemId = created.Id,
                Status = requireApproval ? PartyUploadStatuses.Pending : PartyUploadStatuses.Approved,
                UploadedAt = now,
                ModeratedAt = null,
                ModeratedByUserId = null,
            });
            await _db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // The counter increment, the album membership and the moderation row
            // commit together or not at all — a failure here must not leave a
            // guest charged for an upload that did not land.
            try { await transaction.RollbackAsync(cancellationToken); } catch { /* connection already gone */ }
            // A rollback undoes the DATABASE, not the change tracker: the
            // moderation row added above is still tracked as Added, and the
            // soft delete below calls SaveChanges — which would re-insert the
            // very row this failure is unwinding. Drop the tracked state first.
            _db.ChangeTracker.Clear();
            try { await _files.SoftDeleteAsync(ownerUserId, created.Id, cancellationToken); } catch { /* best effort */ }
            return PartyUploadOutcome.Failed;
        }

        // Best-effort: kick off the derivative + metadata jobs so the media
        // renders in the party grid and on TV. This is the SAME pipeline entry
        // point for both kinds — it already discriminates image (medium/small,
        // metadata, faces) from video (poster, preview strip, ffprobe, HLS) — so
        // no party-specific video path exists. A failure here never fails the
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

        return isVideo ? PartyUploadOutcome.AcceptedVideo : PartyUploadOutcome.AcceptedPhoto;
    }

    // Strip any directory components (path-traversal safety), keep a bounded,
    // printable name, and prefix a short unique token so concurrent guests
    // uploading "IMG_0001.jpg" don't collide on the owner's active-sibling name.
    private static string BuildSafeName(string? fileName, bool declaredVideo)
    {
        var baseName = Path.GetFileName(fileName ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(baseName))
        {
            // Media-neutral fallbacks. The old hardcoded "photo.jpg" would have
            // labelled an unnamed video as a JPEG, which is a lie the rest of
            // the system would then repeat in the owner's library.
            baseName = declaredVideo ? "video.mp4" : "photo.jpg";
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
