using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Metadata;
using NubArca.Api.Security;

namespace NubArca.Api.Files;

public sealed record VideoHlsBackfillOptions
{
    public int? Limit { get; init; }
    // Re-attempt blobs whose ladder generation recorded a failure.
    public bool RetryFailed { get; init; }
    // Regenerate even ready ladders (implies re-attempting failures too).
    public bool Force { get; init; }
    public bool DryRun { get; init; }
}

public sealed record VideoHlsBackfillResult(
    int Candidates,
    int Generated,
    int Skipped,
    int Failed,
    bool DryRun,
    bool MoreWorkRemaining = false,
    string? NextCheckpointJson = null);

// Admin console: bulk HLS pre-warm («prepara tutti i video»). Walks eligible
// video blobs (server-detected video + trusted type) whose ladder is missing —
// plus recorded failures with RetryFailed, plus ready ones with Force — in
// bounded pages (never the whole candidate set in memory), delegating each blob
// to VideoHlsGenerationService (which owns the row lifecycle, temp files and
// the atomic publish). Worker runs are cooperatively sliced at item boundaries
// and resume from a durable checkpoint; cancellation never records a failure
// (the in-flight generation rolls itself back).
public sealed class VideoHlsBackfillService
{
    private const int PageSize = 100;

    private readonly AppDbContext _db;
    private readonly VideoHlsGenerationService _generation;
    private readonly IOptions<MediaOptions> _options;
    private readonly ILogger<VideoHlsBackfillService> _logger;

    public VideoHlsBackfillService(
        AppDbContext db,
        VideoHlsGenerationService generation,
        IOptions<MediaOptions> options,
        ILogger<VideoHlsBackfillService> logger)
    {
        _db = db;
        _generation = generation;
        _options = options;
        _logger = logger;
    }

    public async Task<VideoHlsBackfillResult> RunAsync(
        VideoHlsBackfillOptions options,
        Action<string>? log = null,
        Func<int, int, string?, CancellationToken, Task>? progress = null,
        string? checkpointJson = null,
        Func<long, bool>? shouldYield = null,
        CancellationToken cancellationToken = default)
    {
        if (!_options.Value.VideoHlsEnabled)
        {
            // Environment/config state — not a failure and nothing to walk.
            log?.Invoke("hls backfill: Media:VideoHlsProvider is not 'ffmpeg'; nothing to do.");
            return new VideoHlsBackfillResult(0, 0, 0, 0, options.DryRun);
        }

        var checkpoint = VideoHlsBackfillCheckpoint.TryParse(checkpointJson);
        var total = checkpoint?.Candidates
            ?? await CandidateIds(options).CountAsync(cancellationToken);
        var target = checkpoint?.Target
            ?? (options.Limit is int l ? Math.Min(l, total) : total);
        log?.Invoke($"hls backfill: {total} candidates, processing {target}{(options.DryRun ? " (dry-run)" : "")}.");
        if (options.DryRun || target == 0)
        {
            return new VideoHlsBackfillResult(total, 0, 0, 0, options.DryRun);
        }

        var generated = checkpoint?.Generated ?? 0;
        var skipped = checkpoint?.Skipped ?? 0;
        var failed = checkpoint?.Failed ?? 0;
        var processed = checkpoint?.Processed ?? 0;
        var failedIds = new HashSet<Guid>(checkpoint?.FailedIds ?? Array.Empty<Guid>());
        long processedThisSlice = 0;
        var yielded = false;
        var exhausted = false;

        while (processed < target && !yielded)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var candidates = CandidateIds(options);
            var skip = 0;
            if (options.Force)
            {
                // Force leaves every row eligible after processing. Its candidate
                // set is stable, so the cumulative offset is the resume cursor.
                skip = processed;
            }
            else if (options.RetryFailed && failedIds.Count > 0)
            {
                // Successful rows leave this candidate set naturally; failures
                // remain eligible and must be carried across continuations.
                candidates = candidates.Where(id => !failedIds.Contains(id));
            }

            var page = await candidates
                .OrderBy(id => id)
                .Skip(skip)
                .Take(Math.Min(PageSize, target - processed))
                .ToListAsync(cancellationToken);
            if (page.Count == 0)
            {
                exhausted = true;
                break;
            }

            foreach (var blobId in page)
            {
                cancellationToken.ThrowIfCancellationRequested();
                processed++;
                processedThisSlice++;

                // RetryFailed must actually re-run a recorded-failed row, which
                // EnsureGeneratedAsync only does under force. Ready rows are
                // already excluded from the candidate set unless Force is on, so
                // forcing here never reprocesses a ready ladder unintentionally.
                var force = options.Force || options.RetryFailed;
                var outcome = await _generation.EnsureGeneratedAsync(
                    blobId, force, cancellationToken);
                switch (outcome)
                {
                    case DerivativeOutcome.Generated:
                        generated++;
                        break;
                    case DerivativeOutcome.Failed:
                        failed++;
                        if (options.RetryFailed && !options.Force)
                        {
                            failedIds.Add(blobId);
                        }
                        break;
                    default:
                        skipped++;
                        break;
                }

                if (progress is not null)
                {
                    await progress(processed, target,
                        $"generated {generated}, skipped {skipped}, failed {failed}",
                        cancellationToken);
                }

                if (processed < target
                    && shouldYield is not null
                    && shouldYield(processedThisSlice))
                {
                    yielded = true;
                    break;
                }
            }
        }

        var moreWork = yielded || (!exhausted && processed < target);
        var nextCheckpointJson = moreWork
            ? new VideoHlsBackfillCheckpoint(
                total, target, processed, generated, skipped, failed, failedIds.ToArray()).Serialize()
            : null;

        log?.Invoke($"hls backfill: {(moreWork ? "yielded" : "done")} — generated {generated}, skipped {skipped}, failed {failed} of {processed}.");
        return new VideoHlsBackfillResult(
            total, generated, skipped, failed, DryRun: false,
            MoreWorkRemaining: moreWork, NextCheckpointJson: nextCheckpointJson);
    }

    // Eligible blob ids = server-detected video with a trusted detected type,
    // LEFT-joined against the ladder rows: missing row (implicit pending) always
    // qualifies; failed rows qualify with RetryFailed/Force; ready/pending rows
    // qualify only with Force. Returns bare Guids so the Count + keyset page
    // stay fully translatable (no wrapper-record projection to break EF).
    private IQueryable<Guid> CandidateIds(VideoHlsBackfillOptions options)
    {
        var trusted = SafeContentType.TrustedVideoTypeList;
        var retryFailed = options.RetryFailed || options.Force;

        // Server-confirmed video: header-sniffed as a trusted container OR
        // ffprobe completed with a real video codec. The EF-translatable form of
        // SafeContentType.IsServerConfirmedVideo — legacy containers
        // (AVI/DivX/MJPEG/DV) qualify, spoofed uploads (ffprobe failed → no
        // codec) do not.
        var query =
            from m in _db.BlobMetadata.AsNoTracking()
            where m.MediaCategory == MediaCategories.Video
                && ((m.DetectedContentType != null && trusted.Contains(m.DetectedContentType))
                    || (m.VideoExtractionStatus == MetadataStatuses.Completed
                        && m.VideoCodec != null))
            join d0 in _db.BlobHlsDerivatives.AsNoTracking()
                on m.BlobObjectId equals d0.BlobObjectId into dj
            from d in dj.DefaultIfEmpty()
            select new { m.BlobObjectId, Status = d != null ? d.Status : null };

        if (options.Force)
        {
            // everything eligible
        }
        else if (retryFailed)
        {
            query = query.Where(x => x.Status == null || x.Status == VideoHlsStatuses.Failed);
        }
        else
        {
            query = query.Where(x => x.Status == null);
        }

        return query.Select(x => x.BlobObjectId);
    }
}

internal sealed record VideoHlsBackfillCheckpoint(
    int Candidates = 0,
    int Target = 0,
    int Processed = 0,
    int Generated = 0,
    int Skipped = 0,
    int Failed = 0,
    Guid[]? FailedIds = null)
{
    public string Serialize() => JsonSerializer.Serialize(this);

    public static VideoHlsBackfillCheckpoint? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<VideoHlsBackfillCheckpoint>(json);
        }
        catch
        {
            return null;
        }
    }
}
