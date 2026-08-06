using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Metadata;
using NubArca.Api.Security;
using NubArca.Api.Storage;

namespace NubArca.Api.Files;

// Video-hls slice 2: owner-scoped serving of the HLS ladder for one FileItem.
// Shared by the web endpoints (/api/files/{id}/video...) and the TV endpoints
// (/api/tv/media/{fileId}/video...) — both resolve their own owner id first,
// then delegate here, so the ownership + server-detected-video gate stays
// centralized (same no-leak collapse to "not found" as the legacy stream).
//
// Master resolution semantics:
//  - ready row + published bytes  → the master playlist body, with rendition
//    URIs prefixed "video/" so they resolve UNDER the /video URL segment
//    (HLS resolves relative URIs against the playlist URL's directory).
//  - failed row                   → NotFound (a recorded content failure is
//    permanent until an operator --force; the player treats it as absent).
//  - anything else (no row, pending, ready-with-wiped-bytes) → Preparing,
//    after an idempotent enqueue of the generation job (self-healing: the
//    per-blob idempotency key collapses duplicates while one is queued or
//    running, and re-creates the job if a previous one died).
public sealed class VideoHlsServingService
{
    private readonly AppDbContext _db;
    private readonly HlsDerivativeStorage _hls;
    private readonly IJobQueueAccessor _jobs;
    private readonly IOptions<MediaOptions> _options;

    public VideoHlsServingService(
        AppDbContext db,
        HlsDerivativeStorage hls,
        IJobQueueAccessor jobs,
        IOptions<MediaOptions> options)
    {
        _db = db;
        _hls = hls;
        _jobs = jobs;
        _options = options;
    }

    public bool Enabled => _options.Value.VideoHlsEnabled;

    public async Task<VideoHlsMasterResult> GetMasterAsync(
        Guid fileId, Guid ownerUserId, CancellationToken cancellationToken)
    {
        var gate = await ResolveGateAsync(fileId, ownerUserId, cancellationToken);
        if (gate is null)
        {
            return new VideoHlsMasterResult(VideoHlsMasterStatus.NotFound, null);
        }

        var row = await _db.BlobHlsDerivatives.AsNoTracking()
            .Where(d => d.BlobObjectId == gate.BlobObjectId)
            .Select(d => new { d.Status })
            .FirstOrDefaultAsync(cancellationToken);

        if (row?.Status == VideoHlsStatuses.Failed)
        {
            return new VideoHlsMasterResult(VideoHlsMasterStatus.NotFound, null);
        }

        if (row?.Status == VideoHlsStatuses.Ready && _hls.Exists(gate.Sha256))
        {
            await using var stream = _hls.OpenServableFile(gate.Sha256, "master.m3u8");
            if (stream is not null)
            {
                using var reader = new StreamReader(stream);
                var master = RewriteMasterUris(await reader.ReadToEndAsync(cancellationToken));
                return new VideoHlsMasterResult(VideoHlsMasterStatus.Ready, master);
            }
            // Row says ready but the root playlist vanished mid-check — fall
            // through to the preparing/enqueue path (cache semantics).
        }

        await _jobs.EnqueueVideoHlsGenerateAsync(gate.BlobObjectId, cancellationToken);
        return new VideoHlsMasterResult(VideoHlsMasterStatus.Preparing, null);
    }

    // One ladder file under the /video/{rendition}/{file} child route.
    // `relativePath` is untrusted URL input — HlsDerivativeStorage whitelists
    // the exact shapes and enforces root containment; anything else is null
    // (→ 404, indistinguishable from missing).
    public async Task<VideoHlsFileContent?> OpenLadderFileAsync(
        Guid fileId, Guid ownerUserId, string relativePath, CancellationToken cancellationToken)
    {
        var gate = await ResolveGateAsync(fileId, ownerUserId, cancellationToken);
        if (gate is null)
        {
            return null;
        }

        var stream = _hls.OpenServableFile(gate.Sha256, relativePath);
        if (stream is null)
        {
            return null;
        }

        var contentType = relativePath.EndsWith(".m3u8", StringComparison.Ordinal)
            ? MasterContentType
            : relativePath.EndsWith(".m4s", StringComparison.Ordinal)
                ? "video/iso.segment"
                : "video/mp4"; // init segment
        return new VideoHlsFileContent(stream, contentType);
    }

    public const string MasterContentType = "application/vnd.apple.mpegurl";

    // Advertised on the "preparing" 202 so a client does not have to guess how
    // soon a ladder might exist. Retry-After is a MINIMUM wait (RFC 9110
    // 10.2.3), not an appointment: this endpoint is stateless and cannot
    // estimate a transcode, so it states the floor it is willing to be asked
    // again at and leaves the backoff to the caller. Two seconds is short
    // enough that a small clip's ladder is picked up almost as soon as it
    // lands, and long enough that a poll is not a busy-wait.
    //
    // The body and the status code are unchanged; this is additive.
    public const int PreparingRetryAfterSeconds = 2;

    /// <summary>The "still preparing" response: 202, plus the Retry-After floor.</summary>
    public static IResult Preparing(HttpResponse response)
    {
        response.Headers.RetryAfter =
            PreparingRetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
        return Results.Accepted();
    }

    // Owner-scoped + soft-delete-aware + server-detected-video gate — the
    // exact pair of checks the legacy /video stream performs, so both worlds
    // have identical visibility for the same file.
    private async Task<HlsGate?> ResolveGateAsync(
        Guid fileId, Guid ownerUserId, CancellationToken cancellationToken)
    {
        var fileBlob = await _db.FileItems.AsNoTracking()
            .Where(f => f.Id == fileId
                && f.OwnerUserId == ownerUserId
                && f.DeletedAt == null)
            .Join(
                _db.BlobObjects.AsNoTracking(),
                f => f.BlobObjectId,
                b => b.Id,
                (f, b) => new { b.Id, b.Sha256 })
            .FirstOrDefaultAsync(cancellationToken);
        if (fileBlob is null)
        {
            return null;
        }

        var blobMeta = await _db.BlobMetadata.AsNoTracking()
            .Where(m => m.BlobObjectId == fileBlob.Id)
            .Select(m => new
            {
                m.MediaCategory, m.DetectedContentType, m.VideoExtractionStatus, m.VideoCodec,
            })
            .FirstOrDefaultAsync(cancellationToken);
        // Everything this service serves is ffmpeg-PRODUCED (master playlist and
        // fMP4 segments), never the original bytes — so a legacy container that
        // ffprobe confirmed is safe to serve here even though the header sniff
        // does not trust it. See SafeContentType.IsServerConfirmedVideo.
        if (blobMeta is null
            || blobMeta.MediaCategory != MediaCategories.Video
            || !SafeContentType.IsServerConfirmedVideo(
                blobMeta.DetectedContentType, blobMeta.VideoExtractionStatus, blobMeta.VideoCodec))
        {
            return null;
        }

        return new HlsGate(fileBlob.Id, fileBlob.Sha256);
    }

    // ffmpeg writes rendition URIs relative to the master ("high/stream.m3u8").
    // Served from ".../video", the client resolves those against ".../" (the
    // URL's directory), so each URI line gets a "video/" prefix to land on the
    // ".../video/{rendition}/stream.m3u8" child route. Comment/tag lines are
    // untouched.
    internal static string RewriteMasterUris(string master)
    {
        var lines = master.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (line.Length > 0 && !line.StartsWith('#'))
            {
                lines[i] = "video/" + line;
            }
            else
            {
                lines[i] = line;
            }
        }
        return string.Join('\n', lines);
    }

    private sealed record HlsGate(Guid BlobObjectId, string Sha256);
}

public enum VideoHlsMasterStatus
{
    NotFound,
    Preparing,
    Ready,
}

public sealed record VideoHlsMasterResult(VideoHlsMasterStatus Status, string? MasterPlaylist);

public sealed record VideoHlsFileContent(Stream Content, string ContentType);

// Thin seam over IJobQueue so this Files-layer service does not take a direct
// dependency on the Jobs namespace types in its signature (and tests can fake
// the enqueue without a queue). Implemented in the Jobs layer.
public interface IJobQueueAccessor
{
    Task EnqueueVideoHlsGenerateAsync(Guid blobObjectId, CancellationToken cancellationToken);
}
