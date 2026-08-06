using Microsoft.EntityFrameworkCore;
using NubArca.Api.Data;
using NubArca.Api.Domain.Ai;
using NubArca.Api.Files;

namespace NubArca.Api.Media.Semantic;

// VSEM-03: owner-visible candidate scopes for the unified semantic search.
//
// This is the PRIVACY-CRITICAL half of the pipeline: every scope is built from
// the owner's visible FileItem records FIRST — authentication/owner scope,
// deleted/Vault/media-library filters and the requested physical filters all
// apply here, BEFORE any vector service sees a single identifier. The vector
// layers then rank strictly INSIDE these bounded scopes; there is no path that
// ranks globally and filters afterwards.
public sealed class SemanticMediaCandidateService
{
    private readonly AppDbContext _db;
    private readonly IFileItemService _files;

    public SemanticMediaCandidateService(AppDbContext db, IFileItemService files)
    {
        _db = db;
        _files = files;
    }

    // Owner-visible photo candidates (same set the photo semantic search uses,
    // including the small-image quality gate).
    public Task<IReadOnlyList<GalleryCandidateRef>> GetPhotoCandidatesAsync(
        Guid ownerUserId, ImageFilters filters, int cap, CancellationToken cancellationToken)
        => _files.ListPhysicalGalleryCandidatesAsync(ownerUserId, filters, cap, cancellationToken);

    // Owner-visible video candidates through the same shared gallery engine.
    public Task<IReadOnlyList<GalleryCandidateRef>> GetVideoCandidatesAsync(
        Guid ownerUserId, ImageFilters filters, int cap, CancellationToken cancellationToken)
        => _files.ListPhysicalVideoCandidatesAsync(ownerUserId, filters, cap, cancellationToken);

    // The bounded temporal sample scope of the candidate video blobs: every
    // sample of each blob's COMPLETED manifest at the given segmentation
    // version, with its parent segment interval. Deterministic order before
    // the cap so truncation is stable. Blob-level only — the caller already
    // owns the blob→FileItem mapping from the candidate scope.
    public async Task<IReadOnlyList<VideoSampleScopeRef>> GetVideoSampleScopeAsync(
        IReadOnlyCollection<Guid> candidateBlobIds,
        int segmentationVersion,
        int cap,
        CancellationToken cancellationToken)
    {
        if (candidateBlobIds.Count == 0)
        {
            return Array.Empty<VideoSampleScopeRef>();
        }

        var blobIds = candidateBlobIds.ToList();
        var rows = await (
            from index in _db.VideoSemanticIndexes.AsNoTracking()
            where blobIds.Contains(index.BlobObjectId)
                && index.SegmentationVersion == segmentationVersion
                && index.Status == AiArtifactStatuses.Completed
            join segment in _db.VideoSemanticSegments.AsNoTracking()
                on index.Id equals segment.VideoSemanticIndexId
            join sample in _db.VideoSemanticSamples.AsNoTracking()
                on segment.Id equals sample.VideoSemanticSegmentId
            orderby index.BlobObjectId, segment.SegmentIndex, sample.SampleIndex
            select new VideoSampleScopeRef(
                index.BlobObjectId,
                segment.Id,
                segment.StartMilliseconds,
                segment.EndMilliseconds,
                sample.Id,
                sample.TimestampMilliseconds))
            .Take(Math.Max(1, cap))
            .ToListAsync(cancellationToken);

        return rows;
    }

    // How many of the candidate video blobs have ANY visual-embedding progress
    // for this profile (a completed or partial aggregate at a completed
    // manifest). Drives the generic "indexing" status only — never exposed as
    // per-item state.
    public async Task<int> CountVideoBlobsWithEmbeddingsAsync(
        IReadOnlyCollection<Guid> candidateBlobIds,
        Guid profileId,
        int segmentationVersion,
        CancellationToken cancellationToken)
    {
        if (candidateBlobIds.Count == 0)
        {
            return 0;
        }

        var blobIds = candidateBlobIds.ToList();
        return await _db.VideoSemanticIndexes.AsNoTracking()
            .Where(i => blobIds.Contains(i.BlobObjectId)
                && i.SegmentationVersion == segmentationVersion
                && i.Status == AiArtifactStatuses.Completed
                && _db.VideoSemanticEmbeddingStatuses.Any(s =>
                    s.VideoSemanticIndexId == i.Id
                    && s.ProfileId == profileId
                    && (s.Status == VideoSemanticEmbeddingStatuses.Completed
                        || s.Status == VideoSemanticEmbeddingStatuses.Partial)))
            .Select(i => i.BlobObjectId)
            .Distinct()
            .CountAsync(cancellationToken);
    }
}

// One sample of the temporal scope with its parent segment interval. Internal
// to the search pipeline; never serialized to a DTO.
public sealed record VideoSampleScopeRef(
    Guid BlobObjectId,
    Guid SegmentId,
    long SegmentStartMilliseconds,
    long SegmentEndMilliseconds,
    Guid SampleId,
    long SampleTimestampMilliseconds);
