using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NubArca.Api.Ai;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Domain.Ai;
using NubArca.Api.Files;
using NubArca.Api.Metadata;
using NubArca.Api.Security;

namespace NubArca.Api.Admin;

// Admin jobs console: turns the static command descriptors into the RESPONSE
// the UI renders — resolving the runtime bits a static catalog cannot know:
//
//   * `choice` options: the AI profiles actually registered for the command's
//     capability, with the CONFIGURED production model preselected
//     (Ai:PhotoSimilarityProfileKey / Ai:FaceProfileKey) so the admin never has
//     to type a model key by hand.
//   * availability: whether the command's feature flag is on at all, so a
//     switched-off capability is shown as disabled instead of silently
//     enqueueing a job that no-ops.
//   * pending counts: how many items each command would actually process right
//     now (computed on demand + briefly cached — several of these are wide
//     scans and must not run on every page load).
//
// Everything returned is safe: profile KEYS (stable, human-meaningful), counts,
// and reason codes. No paths, payloads, secrets or vectors.
public sealed class AdminJobCatalogService
{
    // Pending counts are wide-ish scans; a short shared cache keeps repeated
    // page loads (and the poll) off the database.
    private static readonly TimeSpan PendingCacheTtl = TimeSpan.FromSeconds(60);
    private static readonly SemaphoreSlim PendingLock = new(1, 1);
    private static IReadOnlyDictionary<string, int>? _pendingCache;
    private static DateTimeOffset _pendingCachedAt = DateTimeOffset.MinValue;

    private readonly AppDbContext _db;
    private readonly IAiProfileRegistry _profiles;
    private readonly IOptions<AiOptions> _ai;
    private readonly IOptions<MediaOptions> _media;
    private readonly TimeProvider _clock;

    public AdminJobCatalogService(
        AppDbContext db,
        IAiProfileRegistry profiles,
        IOptions<AiOptions> ai,
        IOptions<MediaOptions> media,
        TimeProvider clock)
    {
        _db = db;
        _profiles = profiles;
        _ai = ai;
        _media = media;
        _clock = clock;
    }

    public async Task<AdminJobCatalogResponse> BuildAsync(CancellationToken cancellationToken)
    {
        var context = AvailabilityContext();
        var profiles = await _profiles.ListProfilesAsync(enabledOnly: true, cancellationToken);

        var commands = new List<AdminJobCommandDto>();
        foreach (var c in AdminJobCommands.All)
        {
            var reason = c.Availability?.Invoke(context);
            var pars = c.Params
                .Select(p => p.Kind == AdminJobParamKind.Choice
                    ? ChoiceParamDto(p, c.Capability, profiles)
                    : PlainParamDto(p))
                .ToList();
            commands.Add(new AdminJobCommandDto(
                c.Key, c.Category, c.JobType, pars,
                Available: reason is null, DisabledReason: reason));
        }
        return new AdminJobCatalogResponse(commands);
    }

    // Validates a submitted `choice` value against the options actually offered
    // for that command. Returns null when fine, or an error message.
    public async Task<string?> ValidateChoicesAsync(
        AdminJobCommand command,
        IReadOnlyDictionary<string, System.Text.Json.JsonElement>? raw,
        CancellationToken cancellationToken)
    {
        var choiceParams = command.Params.Where(p => p.Kind == AdminJobParamKind.Choice).ToList();
        if (choiceParams.Count == 0 || raw is null)
        {
            return null;
        }

        IReadOnlyList<AiProfile>? profiles = null;
        foreach (var p in choiceParams)
        {
            if (!raw.TryGetValue(p.Name, out var el)
                || el.ValueKind != System.Text.Json.JsonValueKind.String)
            {
                continue; // absent/blank → the handler's configured default
            }
            var value = el.GetString();
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }
            profiles ??= await _profiles.ListProfilesAsync(enabledOnly: true, cancellationToken);
            var allowed = profiles
                .Where(x => command.Capability is null || x.Capability == command.Capability)
                .Any(x => x.Key == value.Trim());
            if (!allowed)
            {
                return $"Parameter '{p.Name}' must be one of the offered options.";
            }
        }
        return null;
    }

    // How many items each command would process right now. Only the commands
    // with a cheap-enough, meaningful count are reported; the rest are omitted
    // (the UI simply shows nothing for them).
    public async Task<IReadOnlyDictionary<string, int>> PendingCountsAsync(CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow();
        if (_pendingCache is { } cached && now - _pendingCachedAt < PendingCacheTtl)
        {
            return cached;
        }

        await PendingLock.WaitAsync(cancellationToken);
        try
        {
            if (_pendingCache is { } cached2 && _clock.GetUtcNow() - _pendingCachedAt < PendingCacheTtl)
            {
                return cached2;
            }

            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            var ai = _ai.Value;

            counts["metadata-backfill"] = await _db.BlobMetadata.AsNoTracking().CountAsync(
                m => m.ExtractionStatus == MetadataStatuses.Pending
                    || m.ExtractionStatus == MetadataStatuses.Failed,
                cancellationToken);

            counts["metadata-video-backfill"] = await _db.BlobMetadata.AsNoTracking().CountAsync(
                m => m.MediaCategory == MediaCategories.Video
                    && (m.VideoExtractionStatus == MetadataStatuses.Pending
                        || m.VideoExtractionStatus == MetadataStatuses.Failed),
                cancellationToken);

            // Videos eligible for an HLS ladder that do not have one yet (the
            // same predicate VideoHlsBackfillService walks).
            var trusted = SafeContentType.TrustedVideoTypeList;
            counts["media-video-hls-backfill"] = await _db.BlobMetadata.AsNoTracking()
                .Where(m => m.MediaCategory == MediaCategories.Video
                    && ((m.DetectedContentType != null && trusted.Contains(m.DetectedContentType))
                        || (m.VideoExtractionStatus == MetadataStatuses.Completed
                            && m.VideoCodec != null))
                    && !_db.BlobHlsDerivatives.Any(d => d.BlobObjectId == m.BlobObjectId))
                .CountAsync(cancellationToken);

            // Real derivative work only — and ALIGNED with what the backfill
            // actually processes, so the number never promises work the job
            // then skips:
            //  * images missing the grid thumbnail, NOT blocked by a permanent/
            //    not-eligible/skipped diagnostic (an undecodable file is not
            //    "waiting" — only --retry-failed would touch it);
            //  * videos missing a poster whose content type is SERVER-DETECTED
            //    (the poster gate needs it; undetected videos wait on the video-
            //    metadata probe, not on this command).
            // `_db.FileItems` carries the global Private-Vault filter, so
            // vault-only files are excluded automatically.
            var imagesNeedingThumb = await _db.FileItems.AsNoTracking()
                .Where(f => f.DeletedAt == null
                    && _db.BlobMetadata.Any(m => m.BlobObjectId == f.BlobObjectId
                        && m.MediaCategory == MediaCategories.Image)
                    && !_db.FileThumbnails.Any(t => t.FileItemId == f.Id && t.Size == ThumbnailSizes.Small)
                    && !_db.DerivativeDiagnostics.Any(d => d.FileItemId == f.Id
                        && d.Size == ThumbnailSizes.Small
                        && (d.Status == DerivativeStatuses.FailedPermanent
                            || d.Status == DerivativeStatuses.NotEligible
                            || d.Status == DerivativeStatuses.Skipped)))
                .CountAsync(cancellationToken);
            var videosNeedingPoster = await _db.FileItems.AsNoTracking()
                .Where(f => f.DeletedAt == null
                    && _db.BlobMetadata.Any(m => m.BlobObjectId == f.BlobObjectId
                        && m.MediaCategory == MediaCategories.Video
                        && (m.DetectedContentType != null
                            || (m.VideoExtractionStatus == MetadataStatuses.Completed
                                && m.VideoCodec != null)))
                    && !_db.FileThumbnails.Any(t => t.FileItemId == f.Id && t.Size == ThumbnailSizes.Poster)
                    && !_db.DerivativeDiagnostics.Any(d => d.FileItemId == f.Id
                        && d.Size == ThumbnailSizes.Poster
                        && (d.Status == DerivativeStatuses.FailedPermanent
                            || d.Status == DerivativeStatuses.NotEligible
                            || d.Status == DerivativeStatuses.Skipped)))
                .CountAsync(cancellationToken);
            counts["media-derivatives-backfill"] = imagesNeedingThumb + videosNeedingPoster;

            // AI counts are per the CONFIGURED production profile (what the
            // command runs with when the select is left alone) AND restricted to
            // blobs referenced by an active, non-vault FileItem — exactly the
            // backfill's own eligibility. Counting bare BlobMetadata rows would
            // include vault-only / orphaned blobs the job never touches.
            if (!string.IsNullOrWhiteSpace(ai.PhotoSimilarityProfileKey))
            {
                var photoProfile = await _profiles.GetProfileByKeyAsync(
                    ai.PhotoSimilarityProfileKey!, cancellationToken);
                if (photoProfile is not null)
                {
                    counts["ai-photos-embeddings-backfill"] = await _db.FileItems.AsNoTracking()
                        .Where(f => f.DeletedAt == null
                            && _db.BlobMetadata.Any(m => m.BlobObjectId == f.BlobObjectId
                                && m.MediaCategory == MediaCategories.Image)
                            && !_db.BlobEmbeddings.Any(e =>
                                e.BlobObjectId == f.BlobObjectId && e.ProfileId == photoProfile.Id))
                        .Select(f => f.BlobObjectId).Distinct()
                        .CountAsync(cancellationToken);
                }
            }

            if (!string.IsNullOrWhiteSpace(ai.FaceProfileKey))
            {
                var faceProfile = await _profiles.GetProfileByKeyAsync(
                    ai.FaceProfileKey!, cancellationToken);
                if (faceProfile is not null)
                {
                    counts["ai-faces-detect-backfill"] = await _db.FileItems.AsNoTracking()
                        .Where(f => f.DeletedAt == null
                            && _db.BlobMetadata.Any(m => m.BlobObjectId == f.BlobObjectId
                                && m.MediaCategory == MediaCategories.Image)
                            && !_db.BlobAiArtifactStatuses.Any(s =>
                                s.BlobObjectId == f.BlobObjectId
                                && s.ProfileId == faceProfile.Id
                                && s.Capability == AiCapabilities.FaceDetection
                                && s.Status == AiArtifactStatuses.Completed))
                        .Select(f => f.BlobObjectId).Distinct()
                        .CountAsync(cancellationToken);
                }
            }

            _pendingCache = counts;
            _pendingCachedAt = _clock.GetUtcNow();
            return counts;
        }
        finally
        {
            PendingLock.Release();
        }
    }

    private AdminJobAvailabilityContext AvailabilityContext()
    {
        var ai = _ai.Value;
        var media = _media.Value;
        return new AdminJobAvailabilityContext(
            AiEnabled: ai.Enabled,
            ImageEmbeddingsEnabled: ai.ImageEmbeddingsEnabled,
            DocumentExtractionEnabled: ai.DocumentExtractionEnabled,
            DocumentEmbeddingsEnabled: ai.DocumentEmbeddingsEnabled,
            FaceDetectionEnabled: ai.FaceDetectionEnabled,
            FaceEmbeddingsEnabled: ai.FaceEmbeddingsEnabled,
            FaceClusteringEnabled: ai.FaceClusteringEnabled,
            TagsEnabled: ai.TagsEnabled,
            VideoHlsEnabled: media.VideoHlsEnabled,
            VideoProbeEnabled: media.VideoMetadataProbeEnabled,
            FfmpegPosterEnabled: string.Equals(
                media.VideoPosterProvider, "ffmpeg", StringComparison.OrdinalIgnoreCase));
    }

    private static AdminJobParamDto PlainParamDto(AdminJobParam p)
        => new(p.Name, p.Kind.ToString().ToLowerInvariant(), p.Required,
            p.Min, p.Max, p.DefaultBool, p.DefaultInt, p.Danger);

    // Options = the enabled profiles for the command's capability; the
    // preselected value is the configured production key when it is among them
    // (else the registry default, else nothing → handler decides).
    private AdminJobParamDto ChoiceParamDto(
        AdminJobParam p, string? capability, IReadOnlyList<AiProfile> profiles)
    {
        var ai = _ai.Value;
        var forCapability = profiles
            .Where(x => capability is null || x.Capability == capability)
            .OrderBy(x => x.Key, StringComparer.Ordinal)
            .ToList();

        var configuredKey = capability switch
        {
            AiCapabilities.ImageEmbedding => ai.PhotoSimilarityProfileKey,
            AiCapabilities.FaceEmbedding or AiCapabilities.FaceDetection => ai.FaceProfileKey,
            _ => null,
        };
        var recommended = forCapability.FirstOrDefault(x => x.Key == configuredKey)
            ?? forCapability.FirstOrDefault(x => x.IsDefault);

        var options = forCapability
            .Select(x => new AdminJobChoiceDto(x.Key, x.Key, Recommended: x.Key == recommended?.Key))
            .ToList();

        return new AdminJobParamDto(
            p.Name, p.Kind.ToString().ToLowerInvariant(), p.Required,
            p.Min, p.Max, p.DefaultBool, p.DefaultInt, p.Danger,
            Options: options, DefaultText: recommended?.Key);
    }
}
