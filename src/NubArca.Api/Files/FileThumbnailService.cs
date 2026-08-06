using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Storage;
using SixLabors.ImageSharp;

namespace NubArca.Api.Files;

public sealed class FileThumbnailService : IFileThumbnailService
{
    // Thumbnails are always served as JPEG. JPEG is the simplest, most widely
    // supported format and avoids per-format edge cases (transparency, palettes,
    // animation). The JPEG encoder flattens any transparency over the implicit
    // black background; that's acceptable for a preview.
    public const string ThumbnailMimeType = "image/jpeg";

    private readonly AppDbContext _db;
    private readonly IBlobService _blobService;
    private readonly IBlobStorage _storage;
    private readonly IVideoPosterProvider _posterProvider;
    private readonly TimeProvider _clock;
    private readonly ILogger<FileThumbnailService> _logger;
    private readonly IOptions<ImageProcessingOptions> _options;
    // Slice 100: decode → resize → encode is delegated to a pluggable backend
    // (libvips with ImageSharp fallback). Store/row/diagnostics stay here, so a
    // backend swap can never change storage semantics. Null in legacy direct-
    // construction sites → an ImageSharp-only renderer (unchanged behaviour).
    private readonly ImageDerivativeRenderer _renderer;
    private readonly MediaDerivativesOptions _mediaOptions;
    private readonly DerivativeDiagnosticsService? _diagnostics;

    public FileThumbnailService(
        AppDbContext db,
        IBlobService blobService,
        IBlobStorage storage,
        IVideoPosterProvider posterProvider,
        TimeProvider clock,
        ILogger<FileThumbnailService> logger,
        IOptions<ImageProcessingOptions> options,
        ImageDerivativeRenderer? renderer = null,
        IOptions<MediaDerivativesOptions>? mediaOptions = null,
        DerivativeDiagnosticsService? diagnostics = null)
    {
        _db = db;
        _blobService = blobService;
        _storage = storage;
        _posterProvider = posterProvider;
        _clock = clock;
        _logger = logger;
        _options = options;
        _mediaOptions = mediaOptions?.Value ?? new MediaDerivativesOptions();
        _renderer = renderer ?? ImageDerivativeRenderer.ImageSharpOnly();
        _diagnostics = diagnostics;
    }

    public Task<bool> TryGenerateSmallAsync(
        Guid fileItemId,
        Guid sourceBlobId,
        CancellationToken cancellationToken = default)
        => TryGenerateAsync(fileItemId, sourceBlobId, ThumbnailSizes.Small, cancellationToken);

    // ── Slice 95: generation-only bundled image derivatives ─────────────────
    // The batch backfill calls this instead of EnsureAsync so that (a) a file
    // missing BOTH small and medium decodes the source exactly once, and (b)
    // the just-stored derived bytes are never reopened just to be discarded.
    // Each size is generated independently: a failure (or a lost race against
    // the lazy endpoint) for one size never affects the others.
    public async Task<ImageDerivativesResult> EnsureImageDerivativesAsync(
        Guid fileItemId,
        Guid ownerUserId,
        IReadOnlyCollection<string> sizes,
        CancellationToken cancellationToken = default)
    {
        var timings = new ImageDerivativesTimings();
        var outcomes = new List<ImageDerivativeOutcome>();
        ImageDerivativesResult Result(bool decoded) => new(decoded, outcomes, timings);

        // Image sizes only (small first — gallery usability is the priority).
        var requested = sizes
            .Where(ThumbnailSizes.IsKnown)
            .Select(ThumbnailSizes.Normalize)
            .Where(s => string.Equals(s, ThumbnailSizes.Small, StringComparison.Ordinal)
                || string.Equals(s, ThumbnailSizes.Medium, StringComparison.Ordinal))
            .Distinct()
            .OrderBy(s => string.Equals(s, ThumbnailSizes.Small, StringComparison.Ordinal) ? 0 : 1)
            .ToList();
        if (requested.Count == 0)
        {
            return Result(false);
        }

        // Owner-scoped + soft-delete-aware source lookup (same no-leak
        // contract as EnsureAsync).
        var sourceBlobId = await _db.FileItems
            .AsNoTracking()
            .Where(f => f.Id == fileItemId
                && f.OwnerUserId == ownerUserId
                && f.DeletedAt == null)
            .Select(f => (Guid?)f.BlobObjectId)
            .FirstOrDefaultAsync(cancellationToken);
        if (sourceBlobId is null)
        {
            outcomes.AddRange(requested.Select(s => new ImageDerivativeOutcome(s, DerivativeOutcome.NotEligible)));
            return Result(false);
        }

        // Idempotency: existing rows are skipped, never regenerated.
        var existing = await _db.FileThumbnails.AsNoTracking()
            .Where(t => t.FileItemId == fileItemId && requested.Contains(t.Size))
            .Select(t => t.Size)
            .ToListAsync(cancellationToken);
        var missing = new List<string>();
        foreach (var size in requested)
        {
            if (existing.Contains(size, StringComparer.Ordinal))
            {
                outcomes.Add(new ImageDerivativeOutcome(size, DerivativeOutcome.SkippedExisting));
            }
            else
            {
                missing.Add(size);
            }
        }
        if (missing.Count == 0)
        {
            return Result(false);
        }

        // Slice 99: attach the precise diagnostic code to every still-missing
        // size so the backfill can record WHY (and whether it is permanent).
        ImageDerivativesResult MissingFailed(DerivativeOutcome outcome, string code, bool permanent)
        {
            outcomes.AddRange(missing.Select(s => new ImageDerivativeOutcome(s, outcome, code, permanent)));
            return Result(false);
        }

        var options = _options.Value;
        if (!options.EnableThumbnails)
        {
            // Operator kill-switch — not a failure; re-enabling makes it retryable.
            return MissingFailed(DerivativeOutcome.NotEligible, DerivativeErrorCodes.NotEligible, permanent: false);
        }

        // Safety gates run ONCE per file: input byte cap, then a header-only
        // Identify so a decompression bomb never reaches the decoder.
        var sourceSize = await _db.BlobObjects.AsNoTracking()
            .Where(b => b.Id == sourceBlobId)
            .Select(b => (long?)b.SizeBytes)
            .FirstOrDefaultAsync(cancellationToken);
        if (sourceSize is null)
        {
            // The FileItem exists but its source BlobObject row is gone — a data
            // integrity issue. Transient: a restore + retry can resolve it.
            return MissingFailed(DerivativeOutcome.Failed, DerivativeErrorCodes.SourceBlobMissing, permanent: false);
        }
        if (sourceSize is long bytes && bytes > options.MaxThumbnailInputBytes)
        {
            return MissingFailed(DerivativeOutcome.NotEligible, DerivativeErrorCodes.TooLargeBytes, permanent: true);
        }

        var identifyStart = Stopwatch.GetTimestamp();
        var identify = await IdentifySourceAsync(sourceBlobId.Value, cancellationToken);
        timings.IdentifyMillis += (long)Stopwatch.GetElapsedTime(identifyStart).TotalMilliseconds;
        var info = identify.Info;
        if (info is null)
        {
            // unsupported_format / identify_failed are deterministic for these
            // bytes (permanent); source_blob_missing is transient (a restore
            // can fix it).
            var code = identify.FailureCode ?? DerivativeErrorCodes.IdentifyFailed;
            return MissingFailed(DerivativeOutcome.Failed, code,
                permanent: code != DerivativeErrorCodes.SourceBlobMissing);
        }
        if (info.Width > options.MaxWidth || info.Height > options.MaxHeight)
        {
            return MissingFailed(DerivativeOutcome.NotEligible, DerivativeErrorCodes.TooLargeDimensions, permanent: true);
        }
        if ((long)info.Width * info.Height > options.MaxPixels)
        {
            return MissingFailed(DerivativeOutcome.NotEligible, DerivativeErrorCodes.TooManyPixels, permanent: true);
        }

        // Read the source bytes ONCE and hand them to the backend, which
        // decodes + resizes + encodes every requested size (ImageSharp decodes
        // once and clones per size; libvips shrinks-on-load per size). A
        // post-identify byte read failure is a transient source-missing race.
        byte[] source;
        try
        {
            source = await ReadSourceBytesAsync(sourceBlobId.Value, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return MissingFailed(DerivativeOutcome.Failed, DerivativeErrorCodes.SourceBlobMissing, permanent: false);
        }

        var requests = missing
            .Select(s => new DerivativeRequest(s, _mediaOptions.EdgeFor(s), _mediaOptions.QualityFor(s)))
            .ToList();

        // RenderAsync handles backend selection + fallback internally and only
        // throws on real cancellation; an undecodable source comes back as
        // all-null Results + a FailureCode.
        var render = await _renderer.RenderAsync(source, requests, cancellationToken);
        timings.RenderMillis += render.RenderMillis;

        var anyRendered = false;
        for (var i = 0; i < requests.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var size = requests[i].Size;
            var rendered = render.Results[i];
            if (rendered is not null)
            {
                anyRendered = true;
                outcomes.Add(await StoreRenderedAsync(fileItemId, size, rendered, render, timings, cancellationToken));
            }
            else
            {
                var (code, permanent) = MapRenderFailure(render.FailureCode);
                outcomes.Add(new ImageDerivativeOutcome(
                    size, DerivativeOutcome.Failed, code, permanent, render.BackendUsed, render.FellBack));
            }
        }

        return Result(anyRendered);
    }

    // Store ONE pre-rendered JPEG derivative and insert its FileThumbnail row.
    // Owns the blob-refcount + (FileItemId, Size) race + detach handling so the
    // backend never touches storage. Backend identity flows onto the outcome so
    // the backfill can record which backend produced (or failed) the size.
    private async Task<ImageDerivativeOutcome> StoreRenderedAsync(
        Guid fileItemId,
        string size,
        RenderedDerivative rendered,
        DerivativeRenderResult render,
        ImageDerivativesTimings timings,
        CancellationToken cancellationToken,
        ExistingDerivative? existing = null)
    {
        BlobObject? thumbBlob = null;
        FileThumbnail? row = null;
        try
        {
            var storeStart = Stopwatch.GetTimestamp();
            using var encoded = new MemoryStream(rendered.Jpeg, writable: false);
            thumbBlob = await _blobService.StoreDerivedAsync(encoded, cancellationToken);
            timings.StoreMillis += (long)Stopwatch.GetElapsedTime(storeStart).TotalMilliseconds;

            var dbStart = Stopwatch.GetTimestamp();
            if (existing is not null)
            {
                var replaced = await ReplaceStoredDerivativeAsync(
                    existing,
                    thumbBlob,
                    rendered.Width,
                    rendered.Height,
                    posterSource: null,
                    cancellationToken);
                thumbBlob = null; // replacement helper owns the new reference
                timings.DbMillis += (long)Stopwatch.GetElapsedTime(dbStart).TotalMilliseconds;
                return new ImageDerivativeOutcome(
                    size,
                    replaced ? DerivativeOutcome.Generated : DerivativeOutcome.Failed,
                    replaced ? null : DerivativeErrorCodes.StorageError,
                    Permanent: false,
                    render.BackendUsed,
                    render.FellBack);
            }

            row = new FileThumbnail
            {
                Id = Guid.NewGuid(),
                FileItemId = fileItemId,
                BlobObjectId = thumbBlob.Id,
                Size = size,
                Width = rendered.Width,
                Height = rendered.Height,
                CreatedAt = _clock.GetUtcNow().UtcDateTime,
            };
            _db.FileThumbnails.Add(row);
            try
            {
                await _db.SaveChangesAsync(cancellationToken);
                timings.DbMillis += (long)Stopwatch.GetElapsedTime(dbStart).TotalMilliseconds;
                return new ImageDerivativeOutcome(
                    size, DerivativeOutcome.Generated, Backend: render.BackendUsed, FellBack: render.FellBack);
            }
            catch (DbUpdateException)
            {
                // Lost the (FileItemId, Size) unique race against the lazy
                // endpoint — release our derived refcount; the winner's row
                // serves everyone. Null the handle FIRST so the outer catches
                // never double-release (slice 97).
                _db.Entry(row).State = EntityState.Detached;
                row = null;
                await TryReleaseQuietlyAsync(thumbBlob.Id);
                thumbBlob = null;
                timings.DbMillis += (long)Stopwatch.GetElapsedTime(dbStart).TotalMilliseconds;
                return new ImageDerivativeOutcome(
                    size, DerivativeOutcome.SkippedExisting, Backend: render.BackendUsed, FellBack: render.FellBack);
            }
        }
        catch (OperationCanceledException)
        {
            DetachQuietly(row);
            if (thumbBlob is not null)
            {
                await TryReleaseQuietlyAsync(thumbBlob.Id);
            }
            throw;
        }
        catch (Exception ex)
        {
            DetachQuietly(row);
            if (thumbBlob is not null)
            {
                await TryReleaseQuietlyAsync(thumbBlob.Id);
            }
            _logger.LogWarning(
                "Derivative store ({Size}) failed for file {FileItemId} ({Type}).",
                size, fileItemId, ex.GetType().Name);
            // Rendering succeeded; a store/db failure is most likely transient
            // (disk / db hiccup), so default-retryable.
            return new ImageDerivativeOutcome(
                size, DerivativeOutcome.Failed, DerivativeErrorCodes.StorageError, Permanent: false, render.BackendUsed, render.FellBack);
        }
    }

    // Map the renderer's batch-level failure to a diagnostic code + permanence.
    // A render failure happens AFTER the identify gate, so the source is a
    // recognised image whose render nonetheless failed (decode_failed, permanent)
    // unless it timed out (transient).
    private static (string Code, bool Permanent) MapRenderFailure(string? failureCode) => failureCode switch
    {
        DerivativeErrorCodes.Timeout => (DerivativeErrorCodes.Timeout, false),
        _ => (DerivativeErrorCodes.DecodeFailed, true),
    };

    private async Task<byte[]> ReadSourceBytesAsync(Guid blobObjectId, CancellationToken cancellationToken)
    {
        await using var stream = await _blobService.OpenContentAsync(blobObjectId, cancellationToken);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken);
        return buffer.ToArray();
    }

    // Slice 95: generation-only poster ensure for the batch backfill — never
    // opens/returns the poster bytes.
    public async Task<DerivativeOutcome> EnsurePosterGeneratedAsync(
        Guid fileItemId,
        Guid ownerUserId,
        CancellationToken cancellationToken = default)
    {
        var owned = await _db.FileItems.AsNoTracking().AnyAsync(
            f => f.Id == fileItemId && f.OwnerUserId == ownerUserId && f.DeletedAt == null,
            cancellationToken);
        if (!owned)
        {
            return DerivativeOutcome.NotEligible;
        }
        var exists = await _db.FileThumbnails.AsNoTracking().AnyAsync(
            t => t.FileItemId == fileItemId && t.Size == ThumbnailSizes.Poster,
            cancellationToken);
        if (exists)
        {
            return DerivativeOutcome.SkippedExisting;
        }
        return await TryGeneratePosterAsync(fileItemId, ThumbnailSizes.Poster, cancellationToken)
            ? DerivativeOutcome.Generated
            : DerivativeOutcome.Failed;
    }

    public async Task<DerivativeOutcome> EnsureVideoPreviewStripGeneratedAsync(
        Guid fileItemId,
        Guid ownerUserId,
        CancellationToken cancellationToken = default)
    {
        var owned = await _db.FileItems.AsNoTracking().AnyAsync(
            f => f.Id == fileItemId && f.OwnerUserId == ownerUserId && f.DeletedAt == null,
            cancellationToken);
        if (!owned)
        {
            return DerivativeOutcome.NotEligible;
        }
        var exists = await _db.FileThumbnails.AsNoTracking().AnyAsync(
            t => t.FileItemId == fileItemId && t.Size == ThumbnailSizes.VideoPreviewStrip,
            cancellationToken);
        if (exists)
        {
            return DerivativeOutcome.SkippedExisting;
        }
        return await TryGenerateVideoPreviewStripAsync(fileItemId, cancellationToken)
            ? DerivativeOutcome.Generated
            : DerivativeOutcome.Failed;
    }

    public async Task<GalleryDerivativeReplacementOutcome> RegenerateGalleryDerivativeAsync(
        Guid fileItemId,
        Guid ownerUserId,
        string size,
        bool force,
        CancellationToken cancellationToken = default)
    {
        if (!ThumbnailSizes.IsKnown(size))
        {
            return GalleryDerivativeReplacementOutcome.NotEligible;
        }

        var normalized = ThumbnailSizes.Normalize(size);
        if (normalized is not (ThumbnailSizes.Small
            or ThumbnailSizes.Poster
            or ThumbnailSizes.VideoPreviewStrip))
        {
            return GalleryDerivativeReplacementOutcome.NotEligible;
        }

        var sourceBlobId = await _db.FileItems.AsNoTracking()
            .Where(f => f.Id == fileItemId
                && f.OwnerUserId == ownerUserId
                && f.DeletedAt == null)
            .Select(f => (Guid?)f.BlobObjectId)
            .FirstOrDefaultAsync(cancellationToken);
        if (sourceBlobId is null)
        {
            return GalleryDerivativeReplacementOutcome.NotEligible;
        }

        var existing = await _db.FileThumbnails.AsNoTracking()
            .Where(t => t.FileItemId == fileItemId && t.Size == normalized)
            .Select(t => new ExistingDerivative(t.Id, t.BlobObjectId))
            .FirstOrDefaultAsync(cancellationToken);
        if (existing is not null && !force)
        {
            return GalleryDerivativeReplacementOutcome.SkippedExisting;
        }

        var generated = normalized switch
        {
            ThumbnailSizes.Poster => await TryGeneratePosterAsync(
                fileItemId, normalized, cancellationToken, existing),
            ThumbnailSizes.VideoPreviewStrip => await TryGenerateVideoPreviewStripAsync(
                fileItemId, cancellationToken, existing),
            _ => await TryGenerateAsync(
                fileItemId, sourceBlobId.Value, normalized, cancellationToken, existing),
        };
        if (!generated)
        {
            return GalleryDerivativeReplacementOutcome.Failed;
        }

        if (_diagnostics is not null)
        {
            await _diagnostics.ClearAsync(fileItemId, normalized, cancellationToken);
        }
        return existing is null
            ? GalleryDerivativeReplacementOutcome.CreatedMissing
            : GalleryDerivativeReplacementOutcome.Replaced;
    }

    private async Task<bool> TryGenerateAsync(
        Guid fileItemId,
        Guid sourceBlobId,
        string size,
        CancellationToken cancellationToken,
        ExistingDerivative? existing = null)
    {
        var options = _options.Value;
        var normalized = ThumbnailSizes.Normalize(size);

        // Video poster path — delegated to the registered IVideoPosterProvider.
        // The provider may be synthetic (no external deps) or FFmpeg-backed
        // (opt-in via Media:VideoPosterProvider=ffmpeg). Either way the caller
        // doesn't need to know which is active.
        if (string.Equals(normalized, ThumbnailSizes.Poster, StringComparison.Ordinal))
        {
            return await TryGeneratePosterAsync(fileItemId, normalized, cancellationToken, existing);
        }
        if (string.Equals(normalized, ThumbnailSizes.VideoPreviewStrip, StringComparison.Ordinal))
        {
            return await TryGenerateVideoPreviewStripAsync(fileItemId, cancellationToken, existing);
        }

        var edge = _mediaOptions.EdgeFor(normalized);

        // Operator kill-switch. Useful on constrained hosts; existing files
        // keep working because the thumbnail endpoint already 404s when no
        // FileThumbnail row exists.
        if (!options.EnableThumbnails)
        {
            return false;
        }

        try
        {
            // Resource gate 1 — input byte cap. Cheap DB read; rejects bulk
            // pathological inputs (large PNG / TIFF) before any decode.
            var sourceSize = await _db.BlobObjects.AsNoTracking()
                .Where(b => b.Id == sourceBlobId)
                .Select(b => (long?)b.SizeBytes)
                .FirstOrDefaultAsync(cancellationToken);
            if (sourceSize is long sourceBytes && sourceBytes > options.MaxThumbnailInputBytes)
            {
                _logger.LogInformation(
                    "Skipping thumbnail for file {FileItemId}: source {Size} B exceeds MaxThumbnailInputBytes ({Max} B).",
                    fileItemId, sourceBytes, options.MaxThumbnailInputBytes);
                return false;
            }

            // Resource gate 2 — header-only Identify. Reads metadata without
            // allocating the pixel buffer, so a decompression bomb that declares
            // billions of pixels in a few KB cannot exhaust memory here; it will
            // simply fail the limit check below.
            var info = (await IdentifySourceAsync(sourceBlobId, cancellationToken)).Info;
            if (info is null)
            {
                // Not a recognisable image header — same behaviour as before.
                return false;
            }

            var pixels = (long)info.Width * info.Height;
            if (info.Width > options.MaxWidth
                || info.Height > options.MaxHeight
                || pixels > options.MaxPixels)
            {
                _logger.LogInformation(
                    "Skipping thumbnail for file {FileItemId}: {Width}x{Height} ({Pixels} px) exceeds dimension/pixel limits.",
                    fileItemId, info.Width, info.Height, pixels);
                return false;
            }

            // Slice 100: render via the configured backend (libvips with
            // ImageSharp fallback). The lazy path stays best-effort: a render
            // failure surfaces as null here (no diagnostic — only the operator
            // backfill records those).
            var source = await ReadSourceBytesAsync(sourceBlobId, cancellationToken);
            var requests = new[]
            {
                new DerivativeRequest(normalized, edge, _mediaOptions.QualityFor(normalized)),
            };
            var render = await _renderer.RenderAsync(source, requests, cancellationToken);
            var rendered = render.Results[0];
            if (rendered is null)
            {
                return false;
            }

            var outcome = await StoreRenderedAsync(
                fileItemId, normalized, rendered, render, new ImageDerivativesTimings(),
                cancellationToken, existing);
            return outcome.Outcome is DerivativeOutcome.Generated or DerivativeOutcome.SkippedExisting;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Thumbnail generation failed for file {FileItemId}; upload continues without a thumbnail.",
                fileItemId);
            return false;
        }
    }

    public async Task<ThumbnailContent?> EnsureAsync(
        Guid fileItemId,
        Guid ownerUserId,
        string size,
        CancellationToken cancellationToken = default)
    {
        if (!ThumbnailSizes.IsKnown(size))
        {
            return null;
        }

        // Fast path: already generated → just open.
        var existing = await OpenAsync(fileItemId, ownerUserId, size, cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        // Retry gate: never re-attempt on-the-fly generation for a derivative the
        // diagnostics already mark as blocked (permanent / not-eligible / skipped,
        // or a transient whose backoff has not elapsed). This mirrors the batch
        // backfill's gate (MediaDerivativesBackfillService) so a broken or
        // pathological source is not re-decoded on EVERY gallery request — the
        // exact "retry storm" that let a cluster of undecodable files exhaust the
        // API. A successful (re)generation via ANY path clears the diagnostic
        // (DerivativeDiagnosticsService), so a later-fixed file is never wedged.
        var normalizedSize = ThumbnailSizes.Normalize(size);
        var nowUtc = _clock.GetUtcNow().UtcDateTime;
        var blocked = await _db.DerivativeDiagnostics.AsNoTracking().AnyAsync(d =>
            d.FileItemId == fileItemId
            && d.Size == normalizedSize
            && (normalizedSize == ThumbnailSizes.VideoPreviewStrip
                // A failed hover/focus generation must never launch FFmpeg
                // again. Any diagnostic blocks this lazy-only target; an
                // explicit --retry-failed backfill bypasses this method.
                || d.Status == DerivativeStatuses.FailedPermanent
                || d.Status == DerivativeStatuses.NotEligible
                || d.Status == DerivativeStatuses.Skipped
                || (d.Status == DerivativeStatuses.FailedTransient
                    && d.NextRetryAt != null && d.NextRetryAt > nowUtc)),
            cancellationToken);
        if (blocked)
        {
            return null;
        }

        // Owner-scoped + soft-delete-aware lookup of the source blob. Missing /
        // foreign / soft-deleted all collapse to null (no-leak).
        var sourceBlobId = await _db.FileItems
            .AsNoTracking()
            .Where(f => f.Id == fileItemId
                && f.OwnerUserId == ownerUserId
                && f.DeletedAt == null)
            .Select(f => (Guid?)f.BlobObjectId)
            .FirstOrDefaultAsync(cancellationToken);
        if (sourceBlobId is null)
        {
            return null;
        }

        // Best-effort generate. TryGenerateAsync swallows decode/encode errors
        // and returns false; we then surface null (no-leak: indistinguishable
        // from missing).
        var generated = await TryGenerateAsync(fileItemId, sourceBlobId.Value, size, cancellationToken);
        if (!generated)
        {
            if (normalizedSize == ThumbnailSizes.VideoPreviewStrip && _diagnostics is not null)
            {
                // Permanent by design: repeated pointer movement is not a retry
                // policy. Operators can re-attempt explicitly after fixing
                // FFmpeg/source problems with --retry-failed.
                await _diagnostics.RecordAsync(
                    fileItemId,
                    normalizedSize,
                    DerivativeStatuses.FailedPermanent,
                    DerivativeErrorCodes.Unknown,
                    detectedContentType: null,
                    detectedFormat: null,
                    backend: DerivativeBackends.Ffmpeg,
                    generatorVersion: DerivativeGenerators.VideoPreviewStripVersion,
                    cancellationToken: cancellationToken);
            }
            return null;
        }

        if (normalizedSize == ThumbnailSizes.VideoPreviewStrip && _diagnostics is not null)
        {
            await _diagnostics.ClearAsync(fileItemId, normalizedSize, cancellationToken);
        }

        return await OpenAsync(fileItemId, ownerUserId, size, cancellationToken);
    }

    public async Task<ThumbnailContent?> OpenAsync(
        Guid fileItemId,
        Guid ownerUserId,
        string size,
        CancellationToken cancellationToken = default)
    {
        if (!ThumbnailSizes.IsKnown(size))
        {
            return null;
        }

        var normalized = ThumbnailSizes.Normalize(size);

        // Join through FileItem so owner + soft-delete checks are applied at
        // the SQL level. Missing / foreign / soft-deleted / no-thumbnail all
        // collapse to a single null result.
        var hit = await (
            from t in _db.FileThumbnails.AsNoTracking()
            join f in _db.FileItems.AsNoTracking() on t.FileItemId equals f.Id
            where t.FileItemId == fileItemId
                && t.Size == normalized
                && f.OwnerUserId == ownerUserId
                && f.DeletedAt == null
            select new { t.BlobObjectId, t.Width, t.Height })
            .FirstOrDefaultAsync(cancellationToken);

        if (hit is null)
        {
            return null;
        }

        return await OpenStoredDerivativeAsync(
            hit.BlobObjectId, hit.Width, hit.Height, normalized, cancellationToken);
    }

    // Private Vault reader: opens an existing derivative for a file that is
    // CURRENTLY inside the given owner's vault. Deliberately bypasses the global
    // "PrivateVaultId == null" filter (IgnoreQueryFilters) and re-imposes the
    // vault-scoped authorization by hand: owner + active + this exact vault id.
    // Same no-generation, no-leak, single-null contract as OpenAsync.
    public async Task<ThumbnailContent?> OpenVaultAsync(
        Guid fileItemId,
        Guid ownerUserId,
        Guid vaultId,
        string size,
        CancellationToken cancellationToken = default)
    {
        if (!ThumbnailSizes.IsKnown(size))
        {
            return null;
        }

        var normalized = ThumbnailSizes.Normalize(size);

        var hit = await (
            from t in _db.FileThumbnails.AsNoTracking()
            join f in _db.FileItems.AsNoTracking().IgnoreQueryFilters() on t.FileItemId equals f.Id
            where t.FileItemId == fileItemId
                && t.Size == normalized
                && f.OwnerUserId == ownerUserId
                && f.PrivateVaultId == vaultId
                && f.DeletedAt == null
            select new { t.BlobObjectId, t.Width, t.Height })
            .FirstOrDefaultAsync(cancellationToken);

        if (hit is null)
        {
            return null;
        }

        return await OpenStoredDerivativeAsync(
            hit.BlobObjectId, hit.Width, hit.Height, normalized, cancellationToken);
    }

    // Shared tail of OpenAsync / OpenVaultAsync: load the derived blob row, read
    // its bytes from the derived store (with the classic copy-repair fallback
    // for pre-root-split displacement), and wrap them. A null anywhere collapses
    // to a single null result (no-leak: indistinguishable from missing).
    private async Task<ThumbnailContent?> OpenStoredDerivativeAsync(
        Guid derivedBlobObjectId,
        int width,
        int height,
        string normalized,
        CancellationToken cancellationToken)
    {
        var blob = await _db.BlobObjects.AsNoTracking()
            .FirstOrDefaultAsync(b => b.Id == derivedBlobObjectId, cancellationToken);
        if (blob is null)
        {
            return null;
        }

        // Read from the DERIVED store. A null means the bytes are absent there
        // (wiped cache, or a pre-slice-72 artifact still only in the original
        // root): treat as "not present" so the caller (EnsureAsync) regenerates
        // it into the derived root. No-leak: indistinguishable from missing.
        var stream = await _blobService.OpenDerivedContentAsync(derivedBlobObjectId, cancellationToken);
        if (stream is null)
        {
            // Slice 96: the row exists but its bytes are not where serving
            // reads them. Before the caller falls back to CPU-heavy
            // regeneration, try the cheap repair: stream the bytes across from
            // the original root (the classic post-root-split displacement).
            // Race-safe — the copy stages into a temp file and renames
            // atomically; losing the race to a concurrent request is success.
            // Logs carry the size and the action only — never keys, ids,
            // names, or paths.
            if (await _blobService.TryRestoreDerivedFromOriginalAsync(derivedBlobObjectId, cancellationToken))
            {
                stream = await _blobService.OpenDerivedContentAsync(derivedBlobObjectId, cancellationToken);
            }
            if (stream is null)
            {
                _logger.LogWarning(
                    "Derived artifact missing from derived root; size={Size}; source=lazy; action=regenerate",
                    normalized);
                return null;
            }
            _logger.LogInformation(
                "Derived artifact missing from derived root; size={Size}; source=lazy; action=copy-repair; outcome=served",
                normalized);
        }
        return new ThumbnailContent(
            stream,
            ThumbnailMimeType,
            width,
            height,
            blob.SizeBytes);
    }

    // Slice 99: header-only Identify that classifies its failure so the
    // bundled derivative path can record a precise diagnostic code. Info is
    // non-null on success; otherwise FailureCode is one of source_blob_missing
    // (bytes gone), unsupported_format (no decoder), or identify_failed.
    private async Task<IdentifyResult> IdentifySourceAsync(Guid blobObjectId, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await _blobService.OpenContentAsync(blobObjectId, cancellationToken);
            return new IdentifyResult(await Image.IdentifyAsync(stream, cancellationToken), null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (UnknownImageFormatException)
        {
            return new IdentifyResult(null, DerivativeErrorCodes.UnsupportedFormat);
        }
        catch (Exception ex) when (
            ex is FileNotFoundException or DirectoryNotFoundException
            || (ex is InvalidOperationException && ex.Message.Contains("was not found", StringComparison.Ordinal)))
        {
            return new IdentifyResult(null, DerivativeErrorCodes.SourceBlobMissing);
        }
        catch
        {
            return new IdentifyResult(null, DerivativeErrorCodes.IdentifyFailed);
        }
    }

    private readonly record struct IdentifyResult(ImageInfo? Info, string? FailureCode);

    // Slice 68: delegate poster generation to the registered IVideoPosterProvider.
    // The provider returns JPEG bytes (MemoryStream) or null. A null means the
    // provider gave up and we skip creating a FileThumbnail row (the endpoint
    // will re-try on the next request). Blob store + row creation mirrors the
    // small/medium thumbnail path.
    private async Task<bool> TryGeneratePosterAsync(
        Guid fileItemId,
        string normalized,
        CancellationToken cancellationToken,
        ExistingDerivative? existing = null)
    {
        // Lazy factory: opens the video blob stream only if the provider needs it
        // (FFmpeg does; Synthetic does not). We look up the BlobObject StorageKey
        // from the DB and open it via IBlobStorage. The key is never logged.
        async Task<Stream> OpenVideoAsync(CancellationToken ct)
        {
            var storageKey = await _db.FileItems
                .Where(f => f.Id == fileItemId)
                .Join(_db.BlobObjects, f => f.BlobObjectId, b => b.Id, (_, b) => b.StorageKey)
                .FirstOrDefaultAsync(ct)
                ?? throw new InvalidOperationException(
                    "Cannot open video blob for poster: FileItem or BlobObject not found.");
            return await _storage.OpenReadAsync(storageKey, ct);
        }

        VideoPosterResult? poster;
        try
        {
            poster = await _posterProvider.TryGetPosterAsync(OpenVideoAsync, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Poster provider threw for file {FileItemId}; no poster row will be created.",
                fileItemId);
            return false;
        }

        if (poster is null)
        {
            _logger.LogWarning(
                "Poster provider returned null for file {FileItemId}; no poster row will be created.",
                fileItemId);
            return false;
        }

        BlobObject? thumbBlob = null;
        FileThumbnail? row = null;
        var posterSize = _mediaOptions.PosterSize;
        try
        {
            using (poster.Content)
            {
                poster.Content.Position = 0;
                thumbBlob = await _blobService.StoreDerivedAsync(poster.Content, cancellationToken);
            }

            if (existing is not null)
            {
                return await ReplaceStoredDerivativeAsync(
                    existing,
                    thumbBlob,
                    posterSize.Width,
                    posterSize.Height,
                    poster.Source,
                    cancellationToken);
            }

            row = new FileThumbnail
            {
                Id = Guid.NewGuid(),
                FileItemId = fileItemId,
                BlobObjectId = thumbBlob.Id,
                Size = normalized,
                Width = posterSize.Width,
                Height = posterSize.Height,
                CreatedAt = _clock.GetUtcNow().UtcDateTime,
                // Slice 95: persist provenance so synthetic placeholders are
                // distinguishable and selectively regenerable later.
                PosterSource = poster.Source,
            };

            _db.FileThumbnails.Add(row);
            try
            {
                await _db.SaveChangesAsync(cancellationToken);
                return true;
            }
            catch
            {
                _db.Entry(row).State = EntityState.Detached;
                row = null;
                // Slice 97: release exactly once (null the handle before the
                // probe) and probe with a non-cancellable token — see the
                // image path for the rationale.
                if (thumbBlob is not null)
                {
                    await TryReleaseQuietlyAsync(thumbBlob.Id);
                    thumbBlob = null;
                }
                return await _db.FileThumbnails.AsNoTracking()
                    .AnyAsync(t => t.FileItemId == fileItemId && t.Size == normalized,
                        CancellationToken.None);
            }
        }
        catch (OperationCanceledException)
        {
            DetachQuietly(row);
            if (thumbBlob is not null)
            {
                await TryReleaseQuietlyAsync(thumbBlob.Id);
            }
            throw;
        }
        catch (Exception ex)
        {
            DetachQuietly(row);
            if (thumbBlob is not null)
            {
                await TryReleaseQuietlyAsync(thumbBlob.Id);
            }
            _logger.LogWarning(
                ex,
                "Poster generation failed for file {FileItemId}; the file row keeps no poster row.",
                fileItemId);
            return false;
        }
    }

    private async Task<bool> TryGenerateVideoPreviewStripAsync(
        Guid fileItemId,
        CancellationToken cancellationToken,
        ExistingDerivative? existing = null)
    {
        async Task<Stream> OpenVideoAsync(CancellationToken ct)
        {
            var storageKey = await _db.FileItems
                .Where(f => f.Id == fileItemId)
                .Join(_db.BlobObjects, f => f.BlobObjectId, b => b.Id, (_, b) => b.StorageKey)
                .FirstOrDefaultAsync(ct)
                ?? throw new InvalidOperationException(
                    "Cannot open video blob for preview strip: source not found.");
            return await _storage.OpenReadAsync(storageKey, ct);
        }

        var duration = await _db.FileItems.AsNoTracking()
            .Where(f => f.Id == fileItemId)
            .Join(_db.BlobMetadata.AsNoTracking(),
                f => f.BlobObjectId,
                m => m.BlobObjectId,
                (_, m) => m.DurationSeconds)
            .FirstOrDefaultAsync(cancellationToken);

        VideoPreviewStripResult? strip;
        try
        {
            strip = await _posterProvider.TryGetPreviewStripAsync(
                OpenVideoAsync, duration, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Preview strip provider threw for file {FileItemId} ({Type}).",
                fileItemId, ex.GetType().Name);
            return false;
        }

        var stripSpec = _mediaOptions.VideoPreviewStripSize;
        if (strip is null
            || strip.FrameCount != stripSpec.FrameCount
            || strip.Width != stripSpec.Width
            || strip.Height != stripSpec.Height)
        {
            _logger.LogWarning(
                "Preview strip provider returned no usable strip for file {FileItemId}.",
                fileItemId);
            strip?.Content.Dispose();
            return false;
        }

        BlobObject? derivedBlob = null;
        FileThumbnail? row = null;
        try
        {
            using (strip.Content)
            {
                strip.Content.Position = 0;
                derivedBlob = await _blobService.StoreDerivedAsync(strip.Content, cancellationToken);
            }
            if (existing is not null)
            {
                return await ReplaceStoredDerivativeAsync(
                    existing,
                    derivedBlob,
                    strip.Width,
                    strip.Height,
                    posterSource: null,
                    cancellationToken);
            }
            row = new FileThumbnail
            {
                Id = Guid.NewGuid(),
                FileItemId = fileItemId,
                BlobObjectId = derivedBlob.Id,
                Size = ThumbnailSizes.VideoPreviewStrip,
                Width = strip.Width,
                Height = strip.Height,
                CreatedAt = _clock.GetUtcNow().UtcDateTime,
            };
            _db.FileThumbnails.Add(row);
            try
            {
                await _db.SaveChangesAsync(cancellationToken);
                return true;
            }
            catch
            {
                _db.Entry(row).State = EntityState.Detached;
                row = null;
                await TryReleaseQuietlyAsync(derivedBlob.Id);
                derivedBlob = null;
                return await _db.FileThumbnails.AsNoTracking().AnyAsync(
                    t => t.FileItemId == fileItemId
                        && t.Size == ThumbnailSizes.VideoPreviewStrip,
                    CancellationToken.None);
            }
        }
        catch (OperationCanceledException)
        {
            DetachQuietly(row);
            if (derivedBlob is not null) await TryReleaseQuietlyAsync(derivedBlob.Id);
            throw;
        }
        catch (Exception ex)
        {
            DetachQuietly(row);
            if (derivedBlob is not null) await TryReleaseQuietlyAsync(derivedBlob.Id);
            _logger.LogWarning(
                ex,
                "Preview strip persistence failed for file {FileItemId} ({Type}).",
                fileItemId, ex.GetType().Name);
            return false;
        }
    }

    private async Task TryReleaseQuietlyAsync(Guid blobId)
    {
        try
        {
            await _blobService.ReleaseAsync(blobId, CancellationToken.None);
        }
        catch
        {
            // Best-effort: a transient release failure leaves the row at
            // ReferenceCount > 0 until the next sweeper/janitor pass.
        }
    }

    // The new bytes/refcount are created before this method is entered. The row
    // swap and old-refcount decrement share one DB transaction, so a crash or
    // cancellation cannot publish a half-replaced derivative. If another worker
    // already changed the row, our unused new reference is released.
    private async Task<bool> ReplaceStoredDerivativeAsync(
        ExistingDerivative existing,
        BlobObject replacementBlob,
        int width,
        int height,
        string? posterSource,
        CancellationToken cancellationToken)
    {
        var committed = false;
        try
        {
            await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
            var updated = await _db.FileThumbnails
                .Where(t => t.Id == existing.RowId
                    && t.BlobObjectId == existing.BlobObjectId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(t => t.BlobObjectId, replacementBlob.Id)
                    .SetProperty(t => t.Width, width)
                    .SetProperty(t => t.Height, height)
                    .SetProperty(t => t.CreatedAt, _clock.GetUtcNow().UtcDateTime)
                    .SetProperty(t => t.PosterSource, posterSource),
                    cancellationToken);
            if (updated == 0)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                await TryReleaseQuietlyAsync(replacementBlob.Id);
                return true; // idempotent lost race: another replacement won
            }

            await _blobService.ReleaseAsync(existing.BlobObjectId, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            committed = true;
            return true;
        }
        catch (OperationCanceledException)
        {
            // The caller still owns replacementBlob on an exception and its
            // cancellation catch releases it exactly once.
            throw;
        }
        catch
        {
            if (!committed)
            {
                await TryReleaseQuietlyAsync(replacementBlob.Id);
            }
            return false;
        }
    }

    // Slice 97: a FileThumbnail entity that failed (or was cancelled) before
    // its SaveChanges must not stay tracked in this scoped context — a later
    // SaveChanges on the same scope (batch backfills reuse it across items)
    // would insert it as an orphan row whose blob reference was already
    // released, corrupting the refcount accounting.
    private void DetachQuietly(FileThumbnail? row)
    {
        if (row is null) return;
        var entry = _db.Entry(row);
        if (entry.State != EntityState.Detached)
        {
            entry.State = EntityState.Detached;
        }
    }

    private sealed record ExistingDerivative(Guid RowId, Guid BlobObjectId);
}
