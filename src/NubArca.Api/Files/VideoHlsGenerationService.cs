using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Metadata;
using NubArca.Api.Security;
using NubArca.Api.Storage;

namespace NubArca.Api.Files;

// Video-hls slice 1: orchestrates one HLS ladder generation for one SOURCE
// BLOB — owner-agnostic by design (the ladder is blob-derived, shared by every
// FileItem referencing the same content; ownership is enforced by the serving
// endpoints, slice 2). Owns the BlobHlsDerivative lifecycle row, the temp
// source copy, the staging directory and the atomic publish; the transcoder
// only turns bytes into ladder files.
//
// Gates mirror the /video endpoints: server-detected video MediaCategory AND a
// trusted DetectedContentType. Probe data (codecs / audio shape / height) is
// REQUIRED — a wrong HasAudio maps a non-existent stream and fails the whole
// ffmpeg run — so a blob without completed probe fields is probed on the fly
// via the configured IVideoMetadataExtractor (result used directly, never
// persisted onto BlobMetadata: that stays the video-metadata pipeline's job).
//
// Cancellation is never recorded as a failure (job rules): the staging dir and
// the pending row this run created are rolled back and the cancellation
// propagates.
public sealed class VideoHlsGenerationService
{
    private readonly AppDbContext _db;
    private readonly IBlobStorage _storage;
    private readonly HlsDerivativeStorage _hls;
    private readonly IVideoHlsTranscoder _transcoder;
    private readonly IVideoMetadataExtractor _probe;
    private readonly IOptions<MediaOptions> _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<VideoHlsGenerationService> _logger;

    public VideoHlsGenerationService(
        AppDbContext db,
        IBlobStorage storage,
        HlsDerivativeStorage hls,
        IVideoHlsTranscoder transcoder,
        IVideoMetadataExtractor probe,
        IOptions<MediaOptions> options,
        TimeProvider clock,
        ILogger<VideoHlsGenerationService> logger)
    {
        _db = db;
        _storage = storage;
        _hls = hls;
        _transcoder = transcoder;
        _probe = probe;
        _options = options;
        _clock = clock;
        _logger = logger;
    }

    public async Task<DerivativeOutcome> EnsureGeneratedAsync(
        Guid blobObjectId,
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        var opts = _options.Value;
        if (!opts.VideoHlsEnabled)
        {
            // Operator opt-in missing — an environment/config state, not a
            // content failure: never marks the blob failed.
            _logger.LogInformation(
                "HLS generation requested but Media:VideoHlsProvider is not 'ffmpeg'; skipping.");
            return DerivativeOutcome.NotEligible;
        }

        var blob = await _db.BlobObjects.AsNoTracking()
            .Where(b => b.Id == blobObjectId)
            .Select(b => new { b.Id, b.Sha256, b.StorageKey })
            .FirstOrDefaultAsync(cancellationToken);
        if (blob is null)
        {
            return DerivativeOutcome.NotEligible;
        }

        // Server-detected video gate — same pair of checks as the /video
        // endpoints, so generation can never be coaxed onto a spoofed upload.
        var meta = await _db.BlobMetadata.AsNoTracking()
            .Where(m => m.BlobObjectId == blobObjectId)
            .Select(m => new
            {
                m.MediaCategory,
                m.DetectedContentType,
                m.VideoExtractionStatus,
                m.VideoCodec,
                m.AudioCodec,
                m.HasAudio,
                m.Width,
                m.Height,
                m.Rotation,
            })
            .FirstOrDefaultAsync(cancellationToken);
        // Generation feeds ffmpeg and publishes only ffmpeg output, so a legacy
        // container ffprobe confirmed qualifies here (see
        // SafeContentType.IsServerConfirmedVideo) — that is precisely the class
        // of file that NEEDS transcoding to become playable at all.
        if (meta is null
            || meta.MediaCategory != MediaCategories.Video
            || !SafeContentType.IsServerConfirmedVideo(
                meta.DetectedContentType, meta.VideoExtractionStatus, meta.VideoCodec))
        {
            return DerivativeOutcome.NotEligible;
        }

        var row = await _db.BlobHlsDerivatives
            .FirstOrDefaultAsync(d => d.BlobObjectId == blobObjectId, cancellationToken);
        if (row is not null && !force)
        {
            if (row.Status == VideoHlsStatuses.Ready && _hls.Exists(blob.Sha256))
            {
                return DerivativeOutcome.SkippedExisting;
            }
            if (row.Status == VideoHlsStatuses.Failed)
            {
                // Recorded content failure; retried only via an explicit force.
                return DerivativeOutcome.Failed;
            }
            // Ready-with-wiped-bytes or a stale pending (crashed run): fall
            // through and regenerate — derived artifacts are cache.
        }

        // Probe data: prefer the persisted pipeline fields; probe on the fly
        // otherwise. HasAudio must be authoritative before any mapping.
        string? videoCodec;
        string? audioCodec;
        bool hasAudio;
        int? width;
        int? height;
        int? rotation;
        if (meta.VideoExtractionStatus == MetadataStatuses.Completed)
        {
            (videoCodec, audioCodec, hasAudio, width, height, rotation)
                = (meta.VideoCodec, meta.AudioCodec, meta.HasAudio, meta.Width, meta.Height, meta.Rotation);
        }
        else
        {
            var probe = await _probe.ExtractAsync(
                ct => _storage.OpenReadAsync(blob.StorageKey, ct), cancellationToken);
            if (probe.Status != MetadataStatuses.Completed)
            {
                await RecordFailureAsync(
                    row, blobObjectId, VideoHlsErrorCodes.ProbeFailed, cancellationToken);
                return DerivativeOutcome.Failed;
            }
            (videoCodec, audioCodec, hasAudio, width, height, rotation)
                = (probe.VideoCodec, probe.AudioCodec, probe.HasAudio, probe.Width, probe.Height, probe.Rotation);
        }

        var (copyVideo, copyAudio, includeLow) = PlanFor(
            videoCodec, audioCodec, hasAudio, width, height, rotation, opts);

        // Claim/refresh the lifecycle row as pending BEFORE the long transcode.
        var createdRow = row is null;
        if (row is null)
        {
            row = new BlobHlsDerivative
            {
                Id = Guid.NewGuid(),
                BlobObjectId = blobObjectId,
                Status = VideoHlsStatuses.Pending,
                Version = FfmpegVideoHlsTranscoder.Version,
                CreatedAt = _clock.GetUtcNow().UtcDateTime,
            };
            _db.BlobHlsDerivatives.Add(row);
        }
        else
        {
            row.Status = VideoHlsStatuses.Pending;
            row.ErrorCode = null;
            row.ReadyAt = null;
        }
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException) when (createdRow)
        {
            // Lost the unique (BlobObjectId) race against a concurrent run —
            // that run owns the generation; treat as handled.
            _db.Entry(row).State = EntityState.Detached;
            return DerivativeOutcome.SkippedExisting;
        }

        var tempFile = Path.Combine(Path.GetTempPath(), $"nc-hls-{Guid.NewGuid():N}.tmp");
        var staging = _hls.CreateStagingDirectory();
        try
        {
            await using (var src = await _storage.OpenReadAsync(blob.StorageKey, cancellationToken))
            await using (var dst = new FileStream(tempFile, FileMode.Create, FileAccess.Write,
                             FileShare.None, 81920, useAsync: true))
            {
                await src.CopyToAsync(dst, cancellationToken);
            }

            var result = await _transcoder.TranscodeAsync(
                new VideoHlsTranscodeRequest(
                    tempFile, staging, copyVideo, copyAudio, hasAudio, includeLow),
                cancellationToken);

            if (!result.Success)
            {
                row.Status = VideoHlsStatuses.Failed;
                row.ErrorCode = result.ErrorCode ?? VideoHlsErrorCodes.TranscodeFailed;
                row.Version = FfmpegVideoHlsTranscoder.Version;
                await _db.SaveChangesAsync(CancellationToken.None);
                return DerivativeOutcome.Failed;
            }

            if (force)
            {
                // Regeneration replaces the published ladder; without this the
                // publish would defer to the existing directory.
                _hls.Delete(blob.Sha256);
            }
            _hls.Publish(blob.Sha256, staging);

            row.Status = VideoHlsStatuses.Ready;
            row.ErrorCode = null;
            row.Version = FfmpegVideoHlsTranscoder.Version;
            row.ReadyAt = _clock.GetUtcNow().UtcDateTime;
            await _db.SaveChangesAsync(CancellationToken.None);
            return DerivativeOutcome.Generated;
        }
        catch (OperationCanceledException)
        {
            // Cancellation must not record a failure. Roll the row back to the
            // pre-run state: remove it if this run created it, else leave the
            // previous durable state visible by resetting to pending (a later
            // run re-attempts).
            try
            {
                if (createdRow)
                {
                    _db.BlobHlsDerivatives.Remove(row);
                }
                await _db.SaveChangesAsync(CancellationToken.None);
            }
            catch
            {
                // Best-effort: a leftover pending row just means a later run
                // re-attempts.
            }
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex, "HLS generation raised an exception ({ExceptionType}).", ex.GetType().Name);
            row.Status = VideoHlsStatuses.Failed;
            row.ErrorCode = VideoHlsErrorCodes.IoError;
            row.Version = FfmpegVideoHlsTranscoder.Version;
            try
            {
                await _db.SaveChangesAsync(CancellationToken.None);
            }
            catch
            {
                // Best-effort failure recording.
            }
            return DerivativeOutcome.Failed;
        }
        finally
        {
            TryDeleteTempFile(tempFile);
            _hls.DeleteDirectoryQuietly(staging);
        }
    }

    // Pure copy-vs-encode / ladder-shape decision (internal for tests). All
    // size gates use the SHORT side (min(width, height)) — the conventional
    // meaning of "1080p"/"480p" — so a portrait 1080×1920 phone video counts
    // as 1080-class, not 1920-class (v2 fix; the transcoder scales the short
    // side too):
    //  - copy the video stream only when it is ALREADY H.264 at/below the high
    //    cap (unknown size → encode, conservative) AND carries no display
    //    rotation. A rotated source must ALWAYS be re-encoded: stream-copy
    //    preserves the rotation side-data while the encoded low rung bakes the
    //    rotation in physically and drops the tag — the two renditions would
    //    disagree on orientation and players glitch on adaptive switches
    //    (observed on ExoPlayer with a real -90° phone video). Re-encoding
    //    every rung bakes the rotation uniformly (ffmpeg autorotation);
    //  - copy the audio stream when absent or already AAC;
    //  - drop the low rung when the source is at/below the low cap
    //    (upscaling would waste CPU and bytes; unknown size → keep it).
    internal static (bool CopyVideo, bool CopyAudio, bool IncludeLow) PlanFor(
        string? videoCodec, string? audioCodec, bool hasAudio, int? width, int? height,
        int? rotation, MediaOptions o)
    {
        // min(w,h) is rotation-invariant, so the size gates need no swap for
        // rotated sources (ffprobe reports CODED dims, not display dims).
        int? shortSide = (width, height) switch
        {
            (int w, int h) => Math.Min(w, h),
            (null, int h) => h,
            (int w, null) => w,
            _ => null,
        };
        var isRotated = rotation is int r && r % 360 != 0;
        var copyVideo = !isRotated
            && string.Equals(videoCodec, "h264", StringComparison.OrdinalIgnoreCase)
            && shortSide is int s && s <= o.VideoHlsHighMaxHeight;
        var copyAudio = !hasAudio
            || string.Equals(audioCodec, "aac", StringComparison.OrdinalIgnoreCase);
        var includeLow = shortSide is not int known || known > o.VideoHlsLowHeight;
        return (copyVideo, copyAudio, includeLow);
    }

    private async Task RecordFailureAsync(
        BlobHlsDerivative? row, Guid blobObjectId, string errorCode, CancellationToken cancellationToken)
    {
        if (row is null)
        {
            row = new BlobHlsDerivative
            {
                Id = Guid.NewGuid(),
                BlobObjectId = blobObjectId,
                CreatedAt = _clock.GetUtcNow().UtcDateTime,
            };
            _db.BlobHlsDerivatives.Add(row);
        }
        row.Status = VideoHlsStatuses.Failed;
        row.ErrorCode = errorCode;
        row.Version = FfmpegVideoHlsTranscoder.Version;
        row.ReadyAt = null;
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Unique race — the concurrent run's state wins.
            _db.Entry(row).State = EntityState.Detached;
        }
    }

    private void TryDeleteTempFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex, "Could not delete HLS temp file (type: {Type}).", ex.GetType().Name);
        }
    }
}
