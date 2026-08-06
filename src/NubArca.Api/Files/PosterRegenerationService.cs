using Microsoft.EntityFrameworkCore;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.MediaLibrary;
using NubArca.Api.Storage;

namespace NubArca.Api.Files;

// Slice 95: operator-driven poster regeneration. Primary use case: synthetic
// placeholder posters were stored before a real provider (FFmpeg) was enabled —
// `media posters regenerate --only-synthetic` replaces exactly those. Each
// poster is regenerated independently (delete row + release derived refcount,
// then the normal generation-only ensure); a crash in between simply leaves
// the poster missing, which the lazy endpoint / derivatives backfill repairs.
// Logs counts only — never file names, paths, or metadata.
public sealed class PosterRegenerationService
{
    private readonly AppDbContext _db;
    private readonly IFileThumbnailService _thumbnails;
    private readonly IBlobService _blobs;
    private readonly IMediaLibraryService? _mediaLibrary;

    public PosterRegenerationService(
        AppDbContext db,
        IFileThumbnailService thumbnails,
        IBlobService blobs,
        IMediaLibraryService? mediaLibrary = null)
    {
        _db = db;
        _thumbnails = thumbnails;
        _blobs = blobs;
        _mediaLibrary = mediaLibrary;
    }

    public async Task<PosterRegenerationResult> RunAsync(
        PosterRegenerationOptions options,
        Action<string>? log = null,
        CancellationToken cancellationToken = default,
        // Coarse progress sink (processed, total, message). Wired to
        // JobContext.ReportProgressAsync when run as a background job so the
        // Admin Jobs dashboard shows live counts AND the processor flushes a
        // heartbeat (renewing the lease during a long single-pass run). Null CLI.
        Func<int, int?, string?, CancellationToken, Task>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        // Batch media work honours media-library exclusion (slice 94); the
        // lazy poster endpoint still serves/generates excluded files directly.
        // Slice 3: also skip per-file Excluded files — existing posters remain
        // and are still served in the Excluded tab; no new work is scheduled.
        var files = MediaLibrary.MediaLibraryScopePolicy.ApplyScope(
            _db.FileItems.AsNoTracking().Where(f => f.DeletedAt == null),
            MediaLibrary.MediaLibraryScope.Active);
        if (_mediaLibrary is not null)
        {
            files = _mediaLibrary.ApplyMediaLibraryVisibility(files, MediaKind.Video);
        }

        var posters = _db.FileThumbnails.AsNoTracking()
            .Where(t => t.Size == ThumbnailSizes.Poster);
        if (!options.Force)
        {
            // Default/explicit --only-synthetic: pre-provenance rows (null)
            // are deliberately NOT matched — use --force to redo everything.
            posters = posters.Where(t => t.PosterSource == VideoPosterSources.Synthetic);
        }

        var candidates = await posters
            .Join(files, t => t.FileItemId, f => f.Id,
                (t, f) => new { t.Id, t.FileItemId, f.OwnerUserId, PosterBlobId = t.BlobObjectId, t.CreatedAt })
            .OrderBy(c => c.CreatedAt)
            .ToListAsync(cancellationToken);
        if (options.Limit is int limit && candidates.Count > limit)
        {
            candidates = candidates.Take(limit).ToList();
        }

        if (options.DryRun)
        {
            log?.Invoke($"media posters regenerate (dry-run): {candidates.Count} poster(s) would be regenerated.");
            return new PosterRegenerationResult(candidates.Count, 0, 0, DryRun: true);
        }

        var regenerated = 0;
        var failed = 0;
        // Regenerated but STILL a synthetic placeholder (ffmpeg could not pull a
        // real frame) — surfaced so a repeat run is visibly pointless.
        var stillPlaceholder = 0;
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                // Remove the old poster row first (idempotent if it raced
                // away), release its derived blob reference, then regenerate
                // through the standard generation-only path — which stamps the
                // CURRENT provider's source on the new row.
                var deleted = await _db.FileThumbnails
                    .Where(t => t.Id == candidate.Id)
                    .ExecuteDeleteAsync(cancellationToken);
                if (deleted == 1)
                {
                    try { await _blobs.ReleaseAsync(candidate.PosterBlobId, CancellationToken.None); }
                    catch { /* best effort; janitor reconciles */ }
                }

                var outcome = await _thumbnails.EnsurePosterGeneratedAsync(
                    candidate.FileItemId, candidate.OwnerUserId, cancellationToken);
                if (outcome is DerivativeOutcome.Generated or DerivativeOutcome.SkippedExisting)
                {
                    // The provider falls back to a synthetic placeholder when it
                    // cannot extract a real frame, and that still counts as
                    // "generated" — so check what actually landed. Without this
                    // the run reports success while the poster is unchanged, and
                    // every re-run redoes the same futile work.
                    var source = await _db.FileThumbnails.AsNoTracking()
                        .Where(t => t.FileItemId == candidate.FileItemId
                            && t.Size == ThumbnailSizes.Poster)
                        .Select(t => t.PosterSource)
                        .FirstOrDefaultAsync(cancellationToken);
                    if (string.Equals(source, VideoPosterSources.Synthetic, StringComparison.Ordinal))
                    {
                        stillPlaceholder++;
                    }
                    else
                    {
                        regenerated++;
                    }
                }
                else
                {
                    failed++;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                failed++;
            }

            var done = regenerated + stillPlaceholder + failed;
            if (done % 25 == 0)
            {
                log?.Invoke($"media posters regenerate: {done}/{candidates.Count} (ok {regenerated}, still placeholder {stillPlaceholder}, failed {failed}).");
                if (progress is not null)
                {
                    await progress(done, candidates.Count,
                        $"regenerating posters ({regenerated} ok, {stillPlaceholder} still placeholder, {failed} failed)",
                        cancellationToken);
                }
            }
        }

        log?.Invoke($"media posters regenerate: done — {candidates.Count} examined, {regenerated} regenerated, {stillPlaceholder} still placeholder, {failed} failed.");
        return new PosterRegenerationResult(
            candidates.Count, regenerated, failed, DryRun: false, StillPlaceholder: stillPlaceholder);
    }
}

public sealed record PosterRegenerationOptions
{
    // Default: only posters recorded as synthetic (the safe, intended use).
    public bool Force { get; init; }
    public bool DryRun { get; init; }
    public int? Limit { get; init; }
}

public sealed record PosterRegenerationResult(
    int Examined, int Regenerated, int Failed, bool DryRun, int StillPlaceholder = 0);
