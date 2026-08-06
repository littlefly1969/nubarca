using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.MediaLibrary;
using NubArca.Api.Security;

namespace NubArca.Api.Files;

// Slice 63: operator-driven, idempotent prewarm of media derivative artefacts.
// Slice 95: FILE-FIRST — instead of one pass per size (which decoded the same
// original once for medium and AGAIN for small), each eligible image is one
// work unit: its missing derivatives are determined up front and generated
// from a SINGLE decode via IFileThumbnailService.EnsureImageDerivativesAsync
// (generation-only — no derived stream is ever reopened just to be disposed).
// Video posters remain a separate (cheap, synthetic-by-default) unit.
//
// Source blobs are never mutated; derivative blobs are content-addressed and
// dedup naturally; per-size idempotency/races are handled by the unique
// (FileItemId, Size) index. Logs counts and millisecond aggregates only — no
// file names, paths, or metadata. Not run on startup.
public sealed class MediaDerivativesBackfillService
{
    private readonly AppDbContext _db;
    private readonly IFileThumbnailService _thumbnails;
    // Slice 94: batch derivative generation skips media-library-excluded
    // files (lazy on-request generation still works for them). Optional for
    // direct-construction test sites; null = no exclusion filter.
    private readonly IMediaLibraryService? _mediaLibrary;
    // Slice 99: durable failure/skip diagnostics + retry gating. Optional for
    // direct-construction test sites; null = legacy behaviour (no diagnostics
    // recorded, no retry gating — every missing derivative is attempted).
    private readonly DerivativeDiagnosticsService? _diagnostics;
    private readonly TimeProvider _clock;

    public MediaDerivativesBackfillService(
        AppDbContext db,
        IFileThumbnailService thumbnails,
        IMediaLibraryService? mediaLibrary = null,
        DerivativeDiagnosticsService? diagnostics = null,
        TimeProvider? clock = null)
    {
        _db = db;
        _thumbnails = thumbnails;
        _mediaLibrary = mediaLibrary;
        _diagnostics = diagnostics;
        _clock = clock ?? TimeProvider.System;
    }

    // Page fetch chunk. Because resolved items drop out of the "missing" query
    // and failed ids are excluded, this is only a fetch-batch size — NOT the
    // per-slice budget (which the scheduler enforces via shouldYield). There is
    // no re-processing across pages or slices, so a moderate fixed size is fine.
    private const int PageSize = 100;
    private const int MaxFailedIds = 2000;

    // Slice-aware, keyset-paged backfill (scheduler v2).
    //   * `checkpointJson` resumes a previous slice (null = start fresh).
    //   * `shouldYield(processedThisSlice)` is polled at SAFE per-item
    //     boundaries; when it returns true the slice checkpoints and stops and
    //     the result reports MoreWorkRemaining = true. Null (CLI) runs the
    //     whole backfill to completion, never loading all candidates at once.
    // The operator/global `options.Limit` is honoured CUMULATIVELY across
    // slices (tracked in the checkpoint), distinct from the slice budget.
    public async Task<MediaDerivativesBackfillResult> RunAsync(
        MediaDerivativesBackfillOptions options,
        Action<string>? log = null,
        CancellationToken cancellationToken = default,
        // Coarse progress sink (processed, total-or-null, message). Wired to
        // JobContext.ReportProgressAsync when run as a background job; null CLI.
        Func<int, int?, string?, CancellationToken, Task>? progress = null,
        string? checkpointJson = null,
        Func<long, bool>? shouldYield = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.DryRun)
        {
            var (imgCount, videoCount) = await CountMissingAsync(
                options,
                gate: _diagnostics is not null && !options.RetryFailed,
                _clock.GetUtcNow().UtcDateTime,
                cancellationToken);
            log?.Invoke(
                $"media derivatives backfill (dry-run): {imgCount} image(s) and "
                + $"{videoCount} video(s) with missing derivatives would be processed.");
            return new MediaDerivativesBackfillResult { Examined = imgCount + videoCount, DryRun = true };
        }

        var freshStart = string.IsNullOrWhiteSpace(checkpointJson);
        var checkpoint = MediaDerivativesCheckpoint.TryParse(checkpointJson) ?? new MediaDerivativesCheckpoint();
        var stats = new MediaDerivativesBackfillStats { RetriedFailed = options.RetryFailed };
        var failed = new HashSet<Guid>(checkpoint.FailedIds);
        var phase = checkpoint.Phase;
        var processedTotal = checkpoint.ProcessedTotal;
        var failedTotal = checkpoint.FailedTotal;
        var examinedThisSlice = 0;
        long processedThisSlice = 0;
        var yielded = false;
        var logBatch = Math.Max(1, options.BatchSize);
        var globalLimit = options.Limit;
        var now = _clock.GetUtcNow().UtcDateTime;
        // Diagnostic retry gating: when ON (the default, once diagnostics are
        // wired) the candidate query skips files whose missing derivatives are
        // blocked by a permanent / not-eligible / not-yet-due-transient
        // diagnostic — so a broken file is never re-decoded every run. A
        // --retry-failed run turns gating OFF (attempt everything missing,
        // including previously-failed). With no recorder, gating is OFF
        // (legacy behaviour).
        var gate = _diagnostics is not null && !options.RetryFailed;

        // Supersede stale diagnostics whose derivative already exists (e.g.
        // produced by the lazy endpoint, which does not touch diagnostics).
        // Once per logical job (fresh start) — slices clear their own as they go.
        if (_diagnostics is not null && freshStart)
        {
            await _diagnostics.PruneResolvedAsync(cancellationToken);
        }

        bool LimitReached() => globalLimit is int gl && processedTotal >= gl;

        async Task ReportAsync()
        {
            if (progress is not null)
            {
                await progress(processedTotal, null,
                    $"generating derivatives ({processedTotal} done, {failedTotal} failed)",
                    cancellationToken);
            }
        }

        while (phase != MediaDerivativesPhases.Done && !yielded && !LimitReached())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (phase == MediaDerivativesPhases.Images)
            {
                var page = await FetchImagePageAsync(failed, PageSize, gate, now, options.TargetFileItemId, cancellationToken);
                if (page.Count == 0)
                {
                    phase = MediaDerivativesPhases.Posters;
                    continue;
                }
                foreach (var image in page)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    examinedThisSlice++;
                    processedTotal++;
                    processedThisSlice++;
                    stats.ImagesProcessed++;

                    var wanted = new List<string>(2);
                    if (image.MissingSmall) wanted.Add(ThumbnailSizes.Small);
                    else stats.SmallSkipped++;
                    if (image.MissingMedium) wanted.Add(ThumbnailSizes.Medium);
                    else stats.MediumSkipped++;

                    var resolved = await ProcessImageAsync(image, wanted, stats, cancellationToken);
                    if (!resolved && failed.Add(image.FileItemId)) failedTotal++;

                    if (examinedThisSlice % logBatch == 0) log?.Invoke(ProgressLine(examinedThisSlice, examinedThisSlice, stats));
                    await ReportAsync();

                    if (LimitReached()) break;
                    if (shouldYield is not null && shouldYield(processedThisSlice)) { yielded = true; break; }
                }
            }
            else // Posters
            {
                var page = await FetchPosterPageAsync(failed, PageSize, gate, now, options.TargetFileItemId, cancellationToken);
                if (page.Count == 0)
                {
                    phase = MediaDerivativesPhases.Done;
                    break;
                }
                foreach (var video in page)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    examinedThisSlice++;
                    processedTotal++;
                    processedThisSlice++;
                    stats.VideosProcessed++;

                    var resolved = await ProcessVideoAsync(video, stats, cancellationToken);
                    if (!resolved && failed.Add(video.FileItemId)) failedTotal++;

                    if (examinedThisSlice % logBatch == 0) log?.Invoke(ProgressLine(examinedThisSlice, examinedThisSlice, stats));
                    await ReportAsync();

                    if (LimitReached()) break;
                    if (shouldYield is not null && shouldYield(processedThisSlice)) { yielded = true; break; }
                }
            }
        }

        // A yield triggered by CANCELLATION must surface as cancellation (the
        // engine then marks the job cancelled), not a clean continuation.
        if (yielded)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }

        var moreWork = phase != MediaDerivativesPhases.Done && !LimitReached();
        var nextCheckpointJson = moreWork
            ? new MediaDerivativesCheckpoint
            {
                Phase = phase,
                ProcessedTotal = processedTotal,
                FailedTotal = failedTotal,
                FailedIds = failed.Take(MaxFailedIds).ToArray(),
            }.Serialize()
            : null;

        await ReportAsync();
        log?.Invoke(
            $"media derivatives backfill: {(moreWork ? "yielded" : "done")} — "
            + $"{ProgressLine(examinedThisSlice, examinedThisSlice, stats)} "
            + $"(total {processedTotal}, failed {failedTotal})");
        log?.Invoke(
            "media derivatives backfill timings: "
            + $"avg {(stats.ImagesProcessed > 0 ? stats.ImageMillis / stats.ImagesProcessed : 0)} ms/image "
            + $"(identify {stats.IdentifyMillis} ms, render {stats.RenderMillis} ms, "
            + $"store {stats.StoreMillis} ms, db {stats.DbMillis} ms).");
        log?.Invoke(
            "media derivatives backfill backends: "
            + $"vips {stats.VipsImages}, imagesharp {stats.ImageSharpImages}, fallback {stats.FallbackImages}.");
        if (stats.FailuresByCode.Count > 0)
        {
            var byCode = string.Join(", ", stats.FailuresByCode
                .OrderByDescending(kv => kv.Value)
                .Select(kv => $"{kv.Key}={kv.Value}"));
            log?.Invoke($"media derivatives backfill: failure/skip reasons — {byCode}"
                + (stats.RetriedFailed ? " (retry-failed run)" : ""));
        }

        return new MediaDerivativesBackfillResult
        {
            Examined = examinedThisSlice,
            Stats = stats,
            MoreWorkRemaining = moreWork,
            NextCheckpointJson = nextCheckpointJson,
            ProcessedTotal = processedTotal,
            FailedTotal = failedTotal,
        };
    }

    // Generate one image's missing derivatives. Returns true when the item is
    // fully resolved (every requested size now exists), false when it should be
    // skipped on the next slice (decode error / ineligible / partial failure).
    private async Task<bool> ProcessImageAsync(
        ImageCandidate image, List<string> wanted, MediaDerivativesBackfillStats stats, CancellationToken ct)
    {
        var fileStart = Stopwatch.GetTimestamp();
        try
        {
            var one = await _thumbnails.EnsureImageDerivativesAsync(
                image.FileItemId, image.OwnerUserId, wanted, ct);
            if (one.SourceDecoded) stats.ImagesDecoded++;
            AccumulateTimings(stats, one.Timings);
            foreach (var outcome in one.Outcomes)
            {
                CountImage(stats, outcome);
                if (_diagnostics is not null)
                {
                    // Records the precise reason on non-success, clears the
                    // stale diagnostic on success (req #7). Writes to a separate
                    // table — no blob reference, so it can never leak a refcount.
                    await _diagnostics.ApplyImageOutcomeAsync(
                        image.FileItemId, outcome,
                        image.DetectedContentType, image.DetectedFormat, ct);
                }
            }
            // Backend usage is FILE-level: every size of one file shares the same
            // render call (and therefore the same backend / fallback flag).
            CountBackend(stats, one.Outcomes);
            stats.ImageMillis += (long)Stopwatch.GetElapsedTime(fileStart).TotalMilliseconds;
            return one.Outcomes.All(o =>
                o.Outcome is DerivativeOutcome.Generated or DerivativeOutcome.SkippedExisting);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // A whole-file failure (vanished mid-run, I/O error) is non-fatal;
            // the per-size counters reflect what was wanted. No exception detail
            // is logged (could echo metadata/paths).
            if (image.MissingSmall) stats.SmallFailed++;
            if (image.MissingMedium) stats.MediumFailed++;
            stats.ImageMillis += (long)Stopwatch.GetElapsedTime(fileStart).TotalMilliseconds;
            return false;
        }
    }

    private async Task<bool> ProcessVideoAsync(
        VideoCandidate video, MediaDerivativesBackfillStats stats, CancellationToken ct)
    {
        try
        {
            var resolved = true;
            if (video.MissingPoster)
            {
                var posterOutcome = await _thumbnails.EnsurePosterGeneratedAsync(
                    video.FileItemId, video.OwnerUserId, ct);
                CountPoster(stats, posterOutcome);
                resolved &= posterOutcome is DerivativeOutcome.Generated or DerivativeOutcome.SkippedExisting;
                if (_diagnostics is not null)
                {
                    await _diagnostics.ApplyPosterOutcomeAsync(
                        video.FileItemId, posterOutcome,
                        video.DetectedContentType, video.DetectedFormat, ct);
                }
            }
            else
            {
                stats.PosterSkipped++;
            }

            if (video.MissingPreviewStrip)
            {
                var stripOutcome = await _thumbnails.EnsureVideoPreviewStripGeneratedAsync(
                    video.FileItemId, video.OwnerUserId, ct);
                CountPreviewStrip(stats, stripOutcome);
                resolved &= stripOutcome is DerivativeOutcome.Generated or DerivativeOutcome.SkippedExisting;
                if (_diagnostics is not null)
                {
                    await _diagnostics.ApplyVideoPreviewStripOutcomeAsync(
                        video.FileItemId, stripOutcome,
                        video.DetectedContentType, video.DetectedFormat, ct);
                }
            }
            else
            {
                stats.PreviewStripSkipped++;
            }
            return resolved;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            if (video.MissingPoster) stats.PosterFailed++;
            if (video.MissingPreviewStrip) stats.PreviewStripFailed++;
            return false;
        }
    }

    // +generated / ~skipped-existing / !failed / ⊘not-eligible per size.
    private static string ProgressLine(int processed, int examined, MediaDerivativesBackfillStats s)
        => $"processed {processed}/{examined} "
            + $"(small +{s.SmallGenerated}/~{s.SmallSkipped}/!{s.SmallFailed}/o{s.SmallNotEligible}, "
            + $"medium +{s.MediumGenerated}/~{s.MediumSkipped}/!{s.MediumFailed}/o{s.MediumNotEligible}, "
            + $"poster +{s.PosterGenerated}/~{s.PosterSkipped}/!{s.PosterFailed}/o{s.PosterNotEligible}, "
            + $"video-strip +{s.PreviewStripGenerated}/~{s.PreviewStripSkipped}/!{s.PreviewStripFailed}/o{s.PreviewStripNotEligible})";

    private static void AccumulateTimings(MediaDerivativesBackfillStats stats, ImageDerivativesTimings t)
    {
        stats.IdentifyMillis += t.IdentifyMillis;
        stats.RenderMillis += t.RenderMillis;
        stats.StoreMillis += t.StoreMillis;
        stats.DbMillis += t.DbMillis;
    }

    // Per-file backend attribution (req #7). All sizes of one file render in a
    // single backend call, so the backend/fallback is read once from any outcome
    // that a backend actually touched.
    private static void CountBackend(MediaDerivativesBackfillStats stats, IReadOnlyList<ImageDerivativeOutcome> outcomes)
    {
        var touched = outcomes.FirstOrDefault(o => o.Backend is not null);
        if (touched is null)
        {
            return; // resolved entirely by a gate (no render) — no backend used
        }
        if (string.Equals(touched.Backend, DerivativeBackends.Vips, StringComparison.Ordinal))
        {
            stats.VipsImages++;
        }
        else
        {
            stats.ImageSharpImages++;
        }
        if (touched.FellBack)
        {
            stats.FallbackImages++;
        }
    }

    private static void CountImage(MediaDerivativesBackfillStats stats, ImageDerivativeOutcome o)
    {
        switch (o.Size)
        {
            case ThumbnailSizes.Small:
                if (o.Outcome == DerivativeOutcome.Generated) stats.SmallGenerated++;
                else if (o.Outcome == DerivativeOutcome.SkippedExisting) stats.SmallSkipped++;
                else if (o.Outcome == DerivativeOutcome.NotEligible) stats.SmallNotEligible++;
                else stats.SmallFailed++;
                break;
            case ThumbnailSizes.Medium:
                if (o.Outcome == DerivativeOutcome.Generated) stats.MediumGenerated++;
                else if (o.Outcome == DerivativeOutcome.SkippedExisting) stats.MediumSkipped++;
                else if (o.Outcome == DerivativeOutcome.NotEligible) stats.MediumNotEligible++;
                else stats.MediumFailed++;
                break;
        }
        BumpCode(stats, o.Outcome, o.ErrorCode);
    }

    private static void CountPoster(MediaDerivativesBackfillStats stats, DerivativeOutcome outcome)
    {
        if (outcome == DerivativeOutcome.Generated) stats.PosterGenerated++;
        else if (outcome == DerivativeOutcome.SkippedExisting) stats.PosterSkipped++;
        else if (outcome == DerivativeOutcome.NotEligible) stats.PosterNotEligible++;
        else stats.PosterFailed++;
        // The poster path returns a coarse enum; mirror the recorder's codes.
        var code = outcome switch
        {
            DerivativeOutcome.NotEligible => DerivativeErrorCodes.NotEligible,
            DerivativeOutcome.Failed => DerivativeErrorCodes.Unknown,
            _ => null,
        };
        BumpCode(stats, outcome, code);
    }

    private static void CountPreviewStrip(MediaDerivativesBackfillStats stats, DerivativeOutcome outcome)
    {
        if (outcome == DerivativeOutcome.Generated) stats.PreviewStripGenerated++;
        else if (outcome == DerivativeOutcome.SkippedExisting) stats.PreviewStripSkipped++;
        else if (outcome == DerivativeOutcome.NotEligible) stats.PreviewStripNotEligible++;
        else stats.PreviewStripFailed++;
        var code = outcome switch
        {
            DerivativeOutcome.NotEligible => DerivativeErrorCodes.NotEligible,
            DerivativeOutcome.Failed => DerivativeErrorCodes.Unknown,
            _ => null,
        };
        BumpCode(stats, outcome, code);
    }

    // Aggregate failure/skip reasons by code for the job result (req #10).
    private static void BumpCode(MediaDerivativesBackfillStats stats, DerivativeOutcome outcome, string? code)
    {
        if (code is null
            || outcome is DerivativeOutcome.Generated or DerivativeOutcome.SkippedExisting)
        {
            return;
        }
        stats.FailuresByCode.TryGetValue(code, out var n);
        stats.FailuresByCode[code] = n + 1;
    }

    // Slice 94: folder-media-library-excluded files are skipped by the BATCH
    // backfill; lazy first-request endpoints still generate on demand. Slice 3:
    // the same batch backfill also skips per-file Excluded files — existing
    // derivatives are preserved and still served in the "Esclusi" tab, but no
    // NEW derivative work is scheduled for them.
    private IQueryable<FileItem> Eligible(IQueryable<FileItem> query, MediaKind kind)
    {
        var scoped = MediaLibrary.MediaLibraryScopePolicy.ApplyScope(
            query, MediaLibrary.MediaLibraryScope.Active);
        return _mediaLibrary is null ? scoped : _mediaLibrary.ApplyMediaLibraryVisibility(scoped, kind);
    }

    // Images missing small and/or medium, oldest-first, excluding ids that
    // already failed to resolve this run. Resolved items drop out on their own
    // (they gain their FileThumbnail rows), so paging needs no positional
    // cursor — each call returns the next unresolved chunk.
    private async Task<List<ImageCandidate>> FetchImagePageAsync(
        IReadOnlyCollection<Guid> exclude, int pageSize, bool gate, DateTime now, Guid? targetFileItemId, CancellationToken ct)
    {
        var query = _db.FileItems
            .AsNoTracking()
            .Where(f => f.DeletedAt == null
                && (targetFileItemId == null || f.Id == targetFileItemId)
                && !exclude.Contains(f.Id)
                && _db.BlobMetadata.Any(m => m.BlobObjectId == f.BlobObjectId
                    && m.MediaCategory == MediaCategories.Image
                    && m.DetectedContentType != null)
                && (!_db.FileThumbnails.Any(t => t.FileItemId == f.Id && t.Size == ThumbnailSizes.Small)
                    || !_db.FileThumbnails.Any(t => t.FileItemId == f.Id && t.Size == ThumbnailSizes.Medium))
                // Retry gating (req #6): skip files with a blocking diagnostic so
                // a broken image is not re-decoded every run. A --retry-failed
                // run sets gate=false and attempts them anyway. Inlined (not a
                // helper call) so EF can translate it; `now` becomes a parameter.
                && (!gate || !_db.DerivativeDiagnostics.Any(d => d.FileItemId == f.Id
                    && (d.Status == DerivativeStatuses.FailedPermanent
                        || d.Status == DerivativeStatuses.NotEligible
                        || d.Status == DerivativeStatuses.Skipped
                        || (d.Status == DerivativeStatuses.FailedTransient
                            && d.NextRetryAt != null && d.NextRetryAt > now)))));
        return await Eligible(query, MediaKind.Photo)
            .OrderBy(f => f.CreatedAt).ThenBy(f => f.Id)
            .Select(f => new ImageCandidate(
                f.Id,
                f.OwnerUserId,
                !_db.FileThumbnails.Any(t => t.FileItemId == f.Id && t.Size == ThumbnailSizes.Small),
                !_db.FileThumbnails.Any(t => t.FileItemId == f.Id && t.Size == ThumbnailSizes.Medium),
                _db.BlobMetadata.Where(m => m.BlobObjectId == f.BlobObjectId)
                    .Select(m => m.DetectedContentType).FirstOrDefault(),
                _db.BlobMetadata.Where(m => m.BlobObjectId == f.BlobObjectId)
                    .Select(m => m.DetectedFormat).FirstOrDefault()))
            .Take(pageSize)
            .ToListAsync(ct);
    }

    private async Task<List<VideoCandidate>> FetchPosterPageAsync(
        IReadOnlyCollection<Guid> exclude, int pageSize, bool gate, DateTime now, Guid? targetFileItemId, CancellationToken ct)
    {
        var query = _db.FileItems
            .AsNoTracking()
            .Where(f => f.DeletedAt == null
                && (targetFileItemId == null || f.Id == targetFileItemId)
                && !exclude.Contains(f.Id)
                // Server-confirmed video: header-sniffed OR ffprobe-confirmed.
                // Posters/strips are ffmpeg output, so legacy containers
                // (AVI/DivX/MJPEG/DV) belong here too — they were previously
                // skipped and left without any poster.
                && _db.BlobMetadata.Any(m => m.BlobObjectId == f.BlobObjectId
                    && m.MediaCategory == MediaCategories.Video
                    && (m.DetectedContentType != null
                        || (m.VideoExtractionStatus == MetadataStatuses.Completed
                            && m.VideoCodec != null)))
                && ((
                    !_db.FileThumbnails.Any(t => t.FileItemId == f.Id && t.Size == ThumbnailSizes.Poster)
                    && (!gate || !_db.DerivativeDiagnostics.Any(d => d.FileItemId == f.Id
                        && d.Size == ThumbnailSizes.Poster
                        && (d.Status == DerivativeStatuses.FailedPermanent
                            || d.Status == DerivativeStatuses.NotEligible
                            || d.Status == DerivativeStatuses.Skipped
                            || (d.Status == DerivativeStatuses.FailedTransient
                                && d.NextRetryAt != null && d.NextRetryAt > now)))))
                    || (
                    !_db.FileThumbnails.Any(t => t.FileItemId == f.Id && t.Size == ThumbnailSizes.VideoPreviewStrip)
                    && (!gate || !_db.DerivativeDiagnostics.Any(d => d.FileItemId == f.Id
                        && d.Size == ThumbnailSizes.VideoPreviewStrip
                        && (d.Status == DerivativeStatuses.FailedPermanent
                            || d.Status == DerivativeStatuses.NotEligible
                            || d.Status == DerivativeStatuses.Skipped
                            || (d.Status == DerivativeStatuses.FailedTransient
                                && d.NextRetryAt != null && d.NextRetryAt > now)))))));
        return await Eligible(query, MediaKind.Video)
            .OrderBy(f => f.CreatedAt).ThenBy(f => f.Id)
            .Select(f => new VideoCandidate(
                f.Id,
                f.OwnerUserId,
                !_db.FileThumbnails.Any(t => t.FileItemId == f.Id && t.Size == ThumbnailSizes.Poster)
                    && (!gate || !_db.DerivativeDiagnostics.Any(d => d.FileItemId == f.Id
                        && d.Size == ThumbnailSizes.Poster
                        && (d.Status == DerivativeStatuses.FailedPermanent
                            || d.Status == DerivativeStatuses.NotEligible
                            || d.Status == DerivativeStatuses.Skipped
                            || (d.Status == DerivativeStatuses.FailedTransient
                                && d.NextRetryAt != null && d.NextRetryAt > now)))),
                !_db.FileThumbnails.Any(t => t.FileItemId == f.Id && t.Size == ThumbnailSizes.VideoPreviewStrip)
                    && (!gate || !_db.DerivativeDiagnostics.Any(d => d.FileItemId == f.Id
                        && d.Size == ThumbnailSizes.VideoPreviewStrip
                        && (d.Status == DerivativeStatuses.FailedPermanent
                            || d.Status == DerivativeStatuses.NotEligible
                            || d.Status == DerivativeStatuses.Skipped
                            || (d.Status == DerivativeStatuses.FailedTransient
                                && d.NextRetryAt != null && d.NextRetryAt > now)))),
                _db.BlobMetadata.Where(m => m.BlobObjectId == f.BlobObjectId)
                    .Select(m => m.DetectedContentType).FirstOrDefault(),
                _db.BlobMetadata.Where(m => m.BlobObjectId == f.BlobObjectId)
                    .Select(m => m.DetectedFormat).FirstOrDefault()))
            .Take(pageSize)
            .ToListAsync(ct);
    }


    // Dry-run helper: how many files (images-first, capped by --limit) WOULD be
    // processed under the current retry-gating policy. Counts only — no
    // mutation, no checkpoint.
    private async Task<(int Images, int Videos)> CountMissingAsync(
        MediaDerivativesBackfillOptions options, bool gate, DateTime now, CancellationToken ct)
    {
        var images = await Eligible(_db.FileItems
            .AsNoTracking()
            .Where(f => f.DeletedAt == null
                && (options.TargetFileItemId == null || f.Id == options.TargetFileItemId)
                && _db.BlobMetadata.Any(m => m.BlobObjectId == f.BlobObjectId
                    && m.MediaCategory == MediaCategories.Image
                    && m.DetectedContentType != null)
                && (!_db.FileThumbnails.Any(t => t.FileItemId == f.Id && t.Size == ThumbnailSizes.Small)
                    || !_db.FileThumbnails.Any(t => t.FileItemId == f.Id && t.Size == ThumbnailSizes.Medium))
                && (!gate || !_db.DerivativeDiagnostics.Any(d => d.FileItemId == f.Id
                    && (d.Status == DerivativeStatuses.FailedPermanent
                        || d.Status == DerivativeStatuses.NotEligible
                        || d.Status == DerivativeStatuses.Skipped
                        || (d.Status == DerivativeStatuses.FailedTransient
                            && d.NextRetryAt != null && d.NextRetryAt > now))))), MediaKind.Photo)
            .CountAsync(ct);

        var videos = await Eligible(_db.FileItems
            .AsNoTracking()
            .Where(f => f.DeletedAt == null
                && (options.TargetFileItemId == null || f.Id == options.TargetFileItemId)
                // Server-confirmed video: header-sniffed OR ffprobe-confirmed.
                // Posters/strips are ffmpeg output, so legacy containers
                // (AVI/DivX/MJPEG/DV) belong here too — they were previously
                // skipped and left without any poster.
                && _db.BlobMetadata.Any(m => m.BlobObjectId == f.BlobObjectId
                    && m.MediaCategory == MediaCategories.Video
                    && (m.DetectedContentType != null
                        || (m.VideoExtractionStatus == MetadataStatuses.Completed
                            && m.VideoCodec != null)))
                && ((
                    !_db.FileThumbnails.Any(t => t.FileItemId == f.Id && t.Size == ThumbnailSizes.Poster)
                    && (!gate || !_db.DerivativeDiagnostics.Any(d => d.FileItemId == f.Id
                        && d.Size == ThumbnailSizes.Poster
                        && (d.Status == DerivativeStatuses.FailedPermanent
                            || d.Status == DerivativeStatuses.NotEligible
                            || d.Status == DerivativeStatuses.Skipped
                            || (d.Status == DerivativeStatuses.FailedTransient
                                && d.NextRetryAt != null && d.NextRetryAt > now)))))
                    || (
                    !_db.FileThumbnails.Any(t => t.FileItemId == f.Id && t.Size == ThumbnailSizes.VideoPreviewStrip)
                    && (!gate || !_db.DerivativeDiagnostics.Any(d => d.FileItemId == f.Id
                        && d.Size == ThumbnailSizes.VideoPreviewStrip
                        && (d.Status == DerivativeStatuses.FailedPermanent
                            || d.Status == DerivativeStatuses.NotEligible
                            || d.Status == DerivativeStatuses.Skipped
                            || (d.Status == DerivativeStatuses.FailedTransient
                                && d.NextRetryAt != null && d.NextRetryAt > now))))))), MediaKind.Video)
            .CountAsync(ct);

        // --limit caps the combined number of FILES (not outputs), images first.
        if (options.Limit is int limit)
        {
            if (images > limit) { images = limit; videos = 0; }
            else if (images + videos > limit) { videos = limit - images; }
        }

        // FailedOnly remains a legacy alias of --retry-failed (handled by the
        // RetryFailed gate); kept for back-compat with the existing payload/CLI.
        _ = options.FailedOnly;

        return (images, videos);
    }

    private readonly record struct ImageCandidate(
        Guid FileItemId, Guid OwnerUserId, bool MissingSmall, bool MissingMedium,
        string? DetectedContentType, string? DetectedFormat);

    private readonly record struct VideoCandidate(
        Guid FileItemId, Guid OwnerUserId, bool MissingPoster, bool MissingPreviewStrip,
        string? DetectedContentType, string? DetectedFormat);
}

public sealed record MediaDerivativesBackfillOptions
{
    public int? Limit { get; init; }
    public bool MissingOnly { get; init; } = true;
    public bool FailedOnly { get; init; }
    public bool DryRun { get; init; }
    public int BatchSize { get; init; } = 50;
    // Slice 99: when true, the candidate query ignores blocking diagnostics and
    // re-attempts previously-failed derivatives (the "forced retry" path).
    public bool RetryFailed { get; init; }
    // Post-ingest single-target scope: when set, restrict to this one FileItem
    // (bounded point-lookup, no library scan). Null = the global backfill.
    public Guid? TargetFileItemId { get; init; }
}

// Slice 95: per-size counters + millisecond aggregates (counts only — never
// names/paths/metadata). Mutable so the run loop can accumulate in place.
public sealed class MediaDerivativesBackfillStats
{
    public int ImagesProcessed;
    public int ImagesDecoded;
    public int VideosProcessed;
    public int SmallGenerated;
    public int SmallSkipped;
    public int SmallFailed;
    public int SmallNotEligible;
    public int MediumGenerated;
    public int MediumSkipped;
    public int MediumFailed;
    public int MediumNotEligible;
    public int PosterGenerated;
    public int PosterSkipped;
    public int PosterFailed;
    public int PosterNotEligible;
    public int PreviewStripGenerated;
    public int PreviewStripSkipped;
    public int PreviewStripFailed;
    public int PreviewStripNotEligible;
    // Slice 99: failure/skip reasons by stable code, aggregated across sizes
    // (req #10). Counts only — never names/paths/metadata.
    public Dictionary<string, int> FailuresByCode = new(StringComparer.Ordinal);
    // Whether this run bypassed retry gating (--retry-failed).
    public bool RetriedFailed;
    public long ImageMillis;
    public long IdentifyMillis;
    // Slice 100: single backend decode+resize+encode step (replaces the separate
    // decode/resize/encode timers).
    public long RenderMillis;
    public long StoreMillis;
    public long DbMillis;
    // Slice 100: backend attribution (per image), for the speedup/operability story.
    public int VipsImages;
    public int ImageSharpImages;
    public int FallbackImages;
}

public sealed class MediaDerivativesBackfillResult
{
    // Files examined in THIS call/slice (a single CLI call examines all).
    public int Examined { get; init; }
    public bool DryRun { get; init; }
    public MediaDerivativesBackfillStats Stats { get; init; } = new();

    // Scheduler v2: set when the slice stopped at a safe checkpoint with more
    // work to do; NextCheckpointJson is the resume state to persist. Cumulative
    // counts span all slices of the logical job.
    public bool MoreWorkRemaining { get; init; }
    public string? NextCheckpointJson { get; init; }
    public int ProcessedTotal { get; init; }
    public int FailedTotal { get; init; }

    // Back-compat style helpers for callers that only need per-slice totals.
    public int Succeeded => Stats.SmallGenerated + Stats.MediumGenerated + Stats.PosterGenerated
        + Stats.PreviewStripGenerated;
    public int Failed => Stats.SmallFailed + Stats.MediumFailed + Stats.PosterFailed
        + Stats.PreviewStripFailed;
    public int NotEligible => Stats.SmallNotEligible + Stats.MediumNotEligible + Stats.PosterNotEligible
        + Stats.PreviewStripNotEligible;
    public int Processed => Stats.ImagesProcessed + Stats.VideosProcessed;
}
