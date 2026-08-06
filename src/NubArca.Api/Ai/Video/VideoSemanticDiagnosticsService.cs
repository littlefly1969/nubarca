using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NubArca.Api.Ai.Resolution;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Domain.Ai;
using NubArca.Api.Media.Semantic;
using NubArca.Api.Metadata;

namespace NubArca.Api.Ai.Video;

// VSEM-04: read-only, aggregate-only status for the video-semantic substrate.
//
// Composes the SAME data the VSEM-01/02 backfill services already query
// (VideoSemanticIndex / VideoSemanticEmbeddingStatus / VideoSemanticSampleEmbedding
// + the profile registry + IAiBackendResolver), the same way AiStatusService /
// AiDiagnosticsAggregator compose the generic AI substrate. No new diagnostics
// table, no new framework — just a dedicated aggregation seam for this
// capability so segmentation, embedding and pgvector readiness are reported as
// three SEPARATE axes instead of one blended percentage.
//
// Every count is a blob/manifest/sample AGGREGATE. Nothing here ever selects a
// FileItemId, filename, storage key, owner id or raw vector.
public sealed class VideoSemanticDiagnosticsService
{
    private readonly AppDbContext _db;
    private readonly IOptions<VideoSemanticSegmentationOptions> _segmentationOptions;
    private readonly IOptions<VideoVisualEmbeddingOptions> _embeddingOptions;
    private readonly IOptions<AiOptions> _aiOptions;
    private readonly IAiProfileRegistry _registry;
    private readonly IAiBackendResolver _resolver;
    private readonly VideoSemanticSampleVectorIndexService _vectors;

    public VideoSemanticDiagnosticsService(
        AppDbContext db,
        IOptions<VideoSemanticSegmentationOptions> segmentationOptions,
        IOptions<VideoVisualEmbeddingOptions> embeddingOptions,
        IOptions<AiOptions> aiOptions,
        IAiProfileRegistry registry,
        IAiBackendResolver resolver,
        VideoSemanticSampleVectorIndexService vectors)
    {
        _db = db;
        _segmentationOptions = segmentationOptions;
        _embeddingOptions = embeddingOptions;
        _aiOptions = aiOptions;
        _registry = registry;
        _resolver = resolver;
        _vectors = vectors;
    }

    public async Task<VideoSemanticStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var segOptions = _segmentationOptions.Value;
        var activeVersion = segOptions.SegmentationVersion;

        var eligibleVideoBlobs = await EligibleVideoBlobIds().LongCountAsync(cancellationToken);

        var segRows = await _db.VideoSemanticIndexes.AsNoTracking()
            .Where(i => i.SegmentationVersion == activeVersion)
            .GroupBy(i => new { i.Status, i.ErrorCode })
            .Select(g => new { g.Key.Status, g.Key.ErrorCode, Count = g.Count() })
            .ToListAsync(cancellationToken);

        var segCompleted = segRows.Where(r => r.Status == AiArtifactStatuses.Completed).Sum(r => (long)r.Count);
        var segFailed = segRows.Where(r => r.Status == AiArtifactStatuses.Failed).Sum(r => (long)r.Count);
        var segCapacityExceeded = segRows.Where(r =>
            r.Status == AiArtifactStatuses.Skipped
            && r.ErrorCode == VideoSemanticErrorCodes.SegmentationCapacityExceeded).Sum(r => (long)r.Count);
        var segSkippedOther = segRows.Where(r =>
            r.Status == AiArtifactStatuses.Skipped
            && r.ErrorCode != VideoSemanticErrorCodes.SegmentationCapacityExceeded).Sum(r => (long)r.Count);
        var segProcessed = segCompleted + segFailed + segCapacityExceeded + segSkippedOther;
        var segNotProcessed = Math.Max(0, eligibleVideoBlobs - segProcessed);

        var historicalRaw = await _db.VideoSemanticIndexes.AsNoTracking()
            .Where(i => i.SegmentationVersion != activeVersion)
            .GroupBy(i => new { i.SegmentationVersion, i.Status })
            .Select(g => new { g.Key.SegmentationVersion, g.Key.Status, Count = g.Count() })
            .ToListAsync(cancellationToken);
        var historical = historicalRaw
            .GroupBy(r => r.SegmentationVersion)
            .Select(g => new VideoSemanticHistoricalVersion(
                g.Key,
                g.Where(r => r.Status == AiArtifactStatuses.Completed).Sum(r => (long)r.Count),
                g.Where(r => r.Status == AiArtifactStatuses.Failed).Sum(r => (long)r.Count),
                g.Where(r => r.Status == AiArtifactStatuses.Skipped).Sum(r => (long)r.Count)))
            .OrderBy(h => h.SegmentationVersion)
            .ToList();

        // Active profile resolution mirrors AiVideosEmbeddingsBackfillJobHandler:
        // configured Ai:PhotoSimilarityProfileKey wins, else the capability's
        // default profile. A configured-but-unusable key is reported by key
        // (never silently swapped for the default), matching operator intent.
        var configuredKey = _aiOptions.Value.PhotoSimilarityProfileKey;
        string? activeProfileKey;
        bool profileAvailable;
        string? profileUnavailableReason;
        AiProfile? profile;
        if (!string.IsNullOrWhiteSpace(configuredKey))
        {
            profile = await _registry.GetProfileByKeyAsync(configuredKey, cancellationToken);
            activeProfileKey = configuredKey;
            profileAvailable = profile is { Enabled: true };
            profileUnavailableReason = profile is null
                ? AiUnavailableReasons.NoDefaultProfile
                : (profile.Enabled ? null : "profile-disabled");
        }
        else
        {
            var availability = await _resolver.GetCapabilityAvailabilityAsync(
                AiCapabilities.ImageEmbedding, cancellationToken);
            activeProfileKey = availability.ProfileKey;
            profileAvailable = availability.IsAvailable;
            profileUnavailableReason = availability.UnavailableReason;
            profile = activeProfileKey is null
                ? null
                : await _registry.GetProfileByKeyAsync(activeProfileKey, cancellationToken);
        }

        long embCompleted = 0, embPartial = 0, embFailed = 0, embSkipped = 0;
        long samplesExpected = 0, samplesEmbedded = 0;
        long canonicalEmbeddings = 0, pgvectorSynced = 0;
        var vectorBackendAvailable = false;

        if (profile is not null)
        {
            var embRows = await _db.VideoSemanticEmbeddingStatuses.AsNoTracking()
                .Where(s => s.ProfileId == profile.Id
                    && _db.VideoSemanticIndexes.Any(i =>
                        i.Id == s.VideoSemanticIndexId && i.SegmentationVersion == activeVersion))
                .GroupBy(s => s.Status)
                .Select(g => new
                {
                    Status = g.Key,
                    Count = g.Count(),
                    Expected = g.Sum(x => x.ExpectedSampleCount),
                    Completed = g.Sum(x => x.CompletedSampleCount),
                })
                .ToListAsync(cancellationToken);

            embCompleted = embRows.Where(r => r.Status == VideoSemanticEmbeddingStatuses.Completed).Sum(r => (long)r.Count);
            embPartial = embRows.Where(r => r.Status == VideoSemanticEmbeddingStatuses.Partial).Sum(r => (long)r.Count);
            embFailed = embRows.Where(r => r.Status == VideoSemanticEmbeddingStatuses.Failed).Sum(r => (long)r.Count);
            embSkipped = embRows.Where(r => r.Status == VideoSemanticEmbeddingStatuses.Skipped).Sum(r => (long)r.Count);
            samplesExpected = embRows.Sum(r => (long)r.Expected);
            samplesEmbedded = embRows.Sum(r => (long)r.Completed);

            canonicalEmbeddings = await _db.VideoSemanticSampleEmbeddings.AsNoTracking()
                .LongCountAsync(e => e.ProfileId == profile.Id && e.Status == AiArtifactStatuses.Completed, cancellationToken);
            vectorBackendAvailable = await _vectors.IsBackendAvailableAsync(profile.Dimension, cancellationToken);
            // CountValidIndexedAsync (not CountIndexedAsync): a stale mirror —
            // its canonical row no longer `completed` but not yet swept by
            // DeleteStaleAsync — must never inflate synchronized coverage.
            pgvectorSynced = vectorBackendAvailable
                ? await _vectors.CountValidIndexedAsync(profile.Id, cancellationToken)
                : 0;
        }

        var embPending = Math.Max(0, segCompleted - (embCompleted + embPartial + embFailed + embSkipped));
        var samplesFailedOrMissing = Math.Max(0, samplesExpected - samplesEmbedded);
        var pgvectorStaleOrMissing = vectorBackendAvailable ? Math.Max(0, canonicalEmbeddings - pgvectorSynced) : 0;

        return new VideoSemanticStatus(
            EligibleVideoBlobs: eligibleVideoBlobs,
            ActiveSegmentationVersion: activeVersion,
            SegmentationEnabled: segOptions.Enabled,
            SegmentationNotProcessed: segNotProcessed,
            SegmentationCompleted: segCompleted,
            SegmentationFailed: segFailed,
            SegmentationSkipped: segSkippedOther,
            SegmentationCapacityExceeded: segCapacityExceeded,
            HistoricalVersions: historical,
            EmbeddingsEnabled: _embeddingOptions.Value.Enabled,
            ActiveEmbeddingProfileKey: activeProfileKey,
            ActiveEmbeddingProfileAvailable: profileAvailable,
            ActiveEmbeddingProfileUnavailableReason: profileUnavailableReason,
            EmbeddingManifestsPending: embPending,
            EmbeddingManifestsCompleted: embCompleted,
            EmbeddingManifestsPartial: embPartial,
            EmbeddingManifestsFailed: embFailed,
            EmbeddingManifestsSkipped: embSkipped,
            SamplesExpected: samplesExpected,
            SamplesCanonicallyEmbedded: samplesEmbedded,
            SamplesFailedOrMissing: samplesFailedOrMissing,
            CanonicalEmbeddingsProfileWide: canonicalEmbeddings,
            PgvectorBackendAvailable: vectorBackendAvailable,
            PgvectorSynchronizedProfileWide: pgvectorSynced,
            PgvectorStaleOrMissingProfileWide: pgvectorStaleOrMissing,
            MaxRankedPhotoCandidates: MediaSemanticSearchService.PerModalityTopK,
            MaxRankedVideoCandidates: MediaSemanticSearchService.PerModalityTopK,
            RankingContractVersion: SemanticMediaCursor.RankingVersion);
    }

    // Eligible video blobs: authoritative video metadata + at least one active,
    // non-Vault reference — the identical eligibility rule the VSEM-01 backfill
    // candidate query uses (its `_db.FileItems` global filter already excludes
    // the Private Vault).
    private IQueryable<Guid> EligibleVideoBlobIds()
        => _db.BlobObjects.AsNoTracking()
            .Where(b =>
                _db.BlobMetadata.Any(m =>
                    m.BlobObjectId == b.Id
                    && m.MediaCategory == MediaCategories.Video
                    && m.VideoExtractionStatus == MetadataStatuses.Completed)
                && _db.FileItems.Any(f =>
                    f.BlobObjectId == b.Id
                    && f.DeletedAt == null
                    && f.MediaLibraryState == MediaLibraryState.Active))
            .Select(b => b.Id);
}

// Aggregate-only counts for one non-active segmentation version. Never mixed
// into the active-version numbers above.
public sealed record VideoSemanticHistoricalVersion(
    int SegmentationVersion,
    long Completed,
    long Failed,
    long Skipped);

public sealed record VideoSemanticStatus(
    long EligibleVideoBlobs,
    int ActiveSegmentationVersion,
    bool SegmentationEnabled,
    long SegmentationNotProcessed,
    long SegmentationCompleted,
    long SegmentationFailed,
    long SegmentationSkipped,
    long SegmentationCapacityExceeded,
    IReadOnlyList<VideoSemanticHistoricalVersion> HistoricalVersions,
    bool EmbeddingsEnabled,
    string? ActiveEmbeddingProfileKey,
    bool ActiveEmbeddingProfileAvailable,
    string? ActiveEmbeddingProfileUnavailableReason,
    long EmbeddingManifestsPending,
    long EmbeddingManifestsCompleted,
    long EmbeddingManifestsPartial,
    long EmbeddingManifestsFailed,
    long EmbeddingManifestsSkipped,
    long SamplesExpected,
    long SamplesCanonicallyEmbedded,
    long SamplesFailedOrMissing,
    long CanonicalEmbeddingsProfileWide,
    bool PgvectorBackendAvailable,
    long PgvectorSynchronizedProfileWide,
    long PgvectorStaleOrMissingProfileWide,
    int MaxRankedPhotoCandidates,
    int MaxRankedVideoCandidates,
    string RankingContractVersion);
