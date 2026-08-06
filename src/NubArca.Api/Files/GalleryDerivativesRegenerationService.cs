using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.MediaLibrary;

namespace NubArca.Api.Files;

// Durable maintenance traversal for the derivative contract used by the
// unified photo/video gallery. Work is phase ordered and keyset paged. It never
// touches originals, medium previews, HLS, face/AI derivatives or metadata.
public sealed class GalleryDerivativesRegenerationService
{
    private const int DefaultBatchSize = 50;
    private const int MaxBatchSize = 500;

    private static readonly string[] OrderedSizes =
    [
        ThumbnailSizes.Small,
        ThumbnailSizes.Poster,
        ThumbnailSizes.VideoPreviewStrip,
    ];

    private readonly AppDbContext _db;
    private readonly IFileThumbnailService _thumbnails;
    private readonly IMediaLibraryService? _mediaLibrary;
    private readonly MediaOptions _media;

    public GalleryDerivativesRegenerationService(
        AppDbContext db,
        IFileThumbnailService thumbnails,
        IOptions<MediaOptions> media,
        IMediaLibraryService? mediaLibrary = null)
    {
        _db = db;
        _thumbnails = thumbnails;
        _media = media.Value;
        _mediaLibrary = mediaLibrary;
    }

    public async Task<GalleryDerivativesRegenerationResult> RunAsync(
        GalleryDerivativesRegenerationOptions options,
        Action<string>? log = null,
        CancellationToken cancellationToken = default,
        Func<int, int?, string?, CancellationToken, Task>? progress = null,
        string? checkpointJson = null,
        Func<long, bool>? shouldYield = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        var selected = NormalizeSizes(options.Sizes);
        if (selected.Count == 0)
        {
            throw new ArgumentException("At least one gallery derivative size is required.");
        }
        if (options.Limit is <= 0)
        {
            throw new ArgumentException("Limit must be a positive integer.");
        }
        if (!options.DryRun
            && selected.Any(IsVideoSize)
            && !string.Equals(_media.VideoPosterProvider, "ffmpeg", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Real video derivative regeneration requires Media:VideoPosterProvider=ffmpeg.");
        }

        var checkpoint = GalleryDerivativesRegenerationCheckpoint.TryParse(checkpointJson)
            ?? GalleryDerivativesRegenerationCheckpoint.Start(selected);
        var phase = ResolvePhase(checkpoint.Phase, selected);
        var lastId = checkpoint.LastFileItemId;
        var examined = checkpoint.Examined;
        var replaced = checkpoint.Replaced;
        var createdMissing = checkpoint.CreatedMissing;
        var skipped = checkpoint.Skipped;
        var failed = checkpoint.Failed;
        var processedThisSlice = 0L;
        var yielded = false;
        var batchSize = Math.Clamp(
            options.BatchSize ?? DefaultBatchSize, 1, MaxBatchSize);

        bool LimitReached() => options.Limit is int limit && examined >= limit;

        async Task ReportAsync()
        {
            if (progress is not null)
            {
                await progress(
                    examined,
                    options.Limit,
                    $"gallery derivatives phase={phase} examined={examined} replaced={replaced} created={createdMissing} skipped={skipped} failed={failed}",
                    cancellationToken);
            }
        }

        while (phase != GalleryDerivativePhases.Done && !yielded && !LimitReached())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await FetchPageAsync(phase, lastId, batchSize, cancellationToken);
            if (page.Count == 0)
            {
                phase = NextPhase(phase, selected);
                lastId = null;
                continue;
            }

            foreach (var candidate in page)
            {
                cancellationToken.ThrowIfCancellationRequested();
                examined++;
                processedThisSlice++;
                lastId = candidate.FileItemId;

                if (options.DryRun)
                {
                    if (candidate.HasExisting)
                    {
                        if (options.Force) replaced++;
                        else skipped++;
                    }
                    else
                    {
                        createdMissing++;
                    }
                }
                else
                {
                    var outcome = await _thumbnails.RegenerateGalleryDerivativeAsync(
                        candidate.FileItemId,
                        candidate.OwnerUserId,
                        phase,
                        options.Force,
                        cancellationToken);
                    switch (outcome)
                    {
                        case GalleryDerivativeReplacementOutcome.Replaced:
                            replaced++;
                            break;
                        case GalleryDerivativeReplacementOutcome.CreatedMissing:
                            createdMissing++;
                            break;
                        case GalleryDerivativeReplacementOutcome.SkippedExisting:
                        case GalleryDerivativeReplacementOutcome.NotEligible:
                            skipped++;
                            break;
                        default:
                            failed++;
                            break;
                    }
                }

                await ReportAsync();
                if (LimitReached()) break;
                if (shouldYield is not null && shouldYield(processedThisSlice))
                {
                    yielded = true;
                    break;
                }
            }
        }

        if (yielded)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }

        var moreWork = yielded && phase != GalleryDerivativePhases.Done && !LimitReached();
        var nextCheckpoint = moreWork
            ? new GalleryDerivativesRegenerationCheckpoint(
                phase, lastId, examined, replaced, createdMissing, skipped, failed).Serialize()
            : null;

        await ReportAsync();
        log?.Invoke(
            $"media gallery derivatives regenerate: {(moreWork ? "yielded" : "done")} "
            + $"phase={phase} examined={examined} replaced={replaced} "
            + $"created_missing={createdMissing} skipped={skipped} failed={failed}"
            + (options.DryRun ? " dry_run=true" : ""));

        return new GalleryDerivativesRegenerationResult(
            phase,
            lastId,
            examined,
            replaced,
            createdMissing,
            skipped,
            failed,
            moreWork,
            nextCheckpoint);
    }

    private async Task<List<GalleryDerivativeCandidate>> FetchPageAsync(
        string phase,
        Guid? after,
        int batchSize,
        CancellationToken cancellationToken)
    {
        var kind = phase == ThumbnailSizes.Small ? MediaKind.Photo : MediaKind.Video;
        var mediaCategory = phase == ThumbnailSizes.Small
            ? MediaCategories.Image
            : MediaCategories.Video;

        var query = _db.FileItems.AsNoTracking()
            .Where(f => f.DeletedAt == null
                && (after == null || f.Id.CompareTo(after.Value) > 0)
                && _db.BlobMetadata.Any(m => m.BlobObjectId == f.BlobObjectId
                    && m.MediaCategory == mediaCategory
                    && (kind == MediaKind.Photo
                        ? m.DetectedContentType != null
                        : m.DetectedContentType != null
                            || (m.VideoExtractionStatus == MetadataStatuses.Completed
                                && m.VideoCodec != null))));

        query = Eligible(query, kind);
        return await query
            .OrderBy(f => f.Id)
            .Select(f => new GalleryDerivativeCandidate(
                f.Id,
                f.OwnerUserId,
                _db.FileThumbnails.Any(t =>
                    t.FileItemId == f.Id && t.Size == phase)))
            .Take(batchSize)
            .ToListAsync(cancellationToken);
    }

    private IQueryable<FileItem> Eligible(IQueryable<FileItem> query, MediaKind kind)
    {
        var scoped = MediaLibraryScopePolicy.ApplyScope(query, MediaLibraryScope.Active);
        return _mediaLibrary is null
            ? scoped
            : _mediaLibrary.ApplyMediaLibraryVisibility(scoped, kind);
    }

    private static HashSet<string> NormalizeSizes(IReadOnlyCollection<string>? sizes)
    {
        var requested = sizes is null or { Count: 0 } ? OrderedSizes : sizes;
        var normalized = new HashSet<string>(StringComparer.Ordinal);
        foreach (var size in requested)
        {
            if (!ThumbnailSizes.IsKnown(size))
            {
                throw new ArgumentException($"Unknown derivative size '{size}'.");
            }
            var value = ThumbnailSizes.Normalize(size);
            if (!OrderedSizes.Contains(value, StringComparer.Ordinal))
            {
                throw new ArgumentException(
                    "Only small, poster and video-preview-strip can be regenerated by this job.");
            }
            normalized.Add(value);
        }
        return normalized;
    }

    private static bool IsVideoSize(string size)
        => size is ThumbnailSizes.Poster or ThumbnailSizes.VideoPreviewStrip;

    private static string ResolvePhase(string? requested, IReadOnlySet<string> selected)
        => requested is not null && selected.Contains(requested)
            ? requested
            : OrderedSizes.FirstOrDefault(selected.Contains) ?? GalleryDerivativePhases.Done;

    private static string NextPhase(string current, IReadOnlySet<string> selected)
    {
        var index = Array.IndexOf(OrderedSizes, current);
        for (var next = index + 1; next < OrderedSizes.Length; next++)
        {
            if (selected.Contains(OrderedSizes[next])) return OrderedSizes[next];
        }
        return GalleryDerivativePhases.Done;
    }

    private sealed record GalleryDerivativeCandidate(
        Guid FileItemId,
        Guid OwnerUserId,
        bool HasExisting);
}

public sealed record GalleryDerivativesRegenerationOptions
{
    public IReadOnlyCollection<string>? Sizes { get; init; }
    public bool Force { get; init; }
    public bool DryRun { get; init; }
    public int? Limit { get; init; }
    public int? BatchSize { get; init; }
}

public sealed record GalleryDerivativesRegenerationResult(
    string Phase,
    Guid? LastFileItemId,
    int Examined,
    int Replaced,
    int CreatedMissing,
    int Skipped,
    int Failed,
    bool MoreWorkRemaining,
    string? NextCheckpointJson);

public sealed record GalleryDerivativesRegenerationCheckpoint(
    string Phase,
    Guid? LastFileItemId,
    int Examined,
    int Replaced,
    int CreatedMissing,
    int Skipped,
    int Failed)
{
    public static GalleryDerivativesRegenerationCheckpoint Start(IReadOnlySet<string> selected)
        => new(
            new[]
            {
                ThumbnailSizes.Small,
                ThumbnailSizes.Poster,
                ThumbnailSizes.VideoPreviewStrip,
            }.FirstOrDefault(selected.Contains) ?? GalleryDerivativePhases.Done,
            null, 0, 0, 0, 0, 0);

    public string Serialize() => JsonSerializer.Serialize(this);

    public static GalleryDerivativesRegenerationCheckpoint? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<GalleryDerivativesRegenerationCheckpoint>(json);
        }
        catch
        {
            return null;
        }
    }
}

public static class GalleryDerivativePhases
{
    public const string Done = "done";
}
