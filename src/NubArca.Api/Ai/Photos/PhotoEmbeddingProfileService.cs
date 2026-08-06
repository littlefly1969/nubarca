using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NubArca.Api.Ai.Resolution;
using NubArca.Api.Ai.Onnx;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Domain.Ai;

namespace NubArca.Api.Ai.Photos;

// Photo-embedding profile LIFECYCLE helper (read-only). Two jobs, both safe:
//
//  1. ResolveActiveProfileAsync — the SINGLE place that decides which photo
//     similarity profile is "active". Precedence is fully explicit:
//        override (operator CLI --profile) > Ai__PhotoSimilarityProfileKey >
//        documented fallback to the capability default profile.
//     There is NO "latest installed model" heuristic and NO mixing across
//     profiles. A resolved profile is validated as usable for image-embedding
//     similarity (enabled, capability image-embedding, positive dimension, an
//     enabled model). Validation does NOT require backend readiness (e.g. an
//     ONNX model file on disk): reading already-stored owner-private embeddings
//     never needs the live model — only WRITING them (the backfill, via the
//     backend resolver) does. The reasons returned are sanitized tokens.
//
//  2. GetCoverageAsync — aggregate-only coverage for a profile, eligibility
//     IDENTICAL to the photo-embeddings backfill (image blob referenced by an
//     active FileItem), so "missing" equals the backfill's pending count.
//     Returns counts/dimension/metric only — never vectors, BlobObjectId, SHA,
//     StorageKey or paths.
public sealed class PhotoEmbeddingProfileService
{
    private readonly AppDbContext _db;
    private readonly IAiProfileRegistry _registry;
    private readonly IOptions<AiOptions> _options;
    private readonly PhotoVectorIndexService _vectors;
    private readonly TimeProvider _clock;

    public PhotoEmbeddingProfileService(
        AppDbContext db, IAiProfileRegistry registry, IOptions<AiOptions> options,
        PhotoVectorIndexService vectors, TimeProvider clock)
    {
        _db = db;
        _registry = registry;
        _options = options;
        _vectors = vectors;
        _clock = clock;
    }

    public async Task<PhotoProfileResolution> ResolveActiveProfileAsync(
        string? overrideKey, CancellationToken cancellationToken = default)
    {
        var configuredKey = _options.Value.PhotoSimilarityProfileKey;

        string? requestedKey;
        PhotoProfileSource source;
        if (!string.IsNullOrWhiteSpace(overrideKey))
        {
            requestedKey = overrideKey.Trim();
            source = PhotoProfileSource.Override;
        }
        else if (!string.IsNullOrWhiteSpace(configuredKey))
        {
            requestedKey = configuredKey.Trim();
            source = PhotoProfileSource.Configured;
        }
        else
        {
            requestedKey = null;
            source = PhotoProfileSource.DefaultFallback;
        }

        AiProfile? profile;
        if (requestedKey is not null)
        {
            profile = await _registry.GetProfileByKeyAsync(requestedKey, cancellationToken);
            if (profile is null)
            {
                return new PhotoProfileResolution(
                    source, requestedKey, Profile: null, Usable: false, AiUnavailableReasons.ProfileNotFound);
            }
        }
        else
        {
            // Documented explicit fallback: the capability's default profile.
            profile = await _registry.GetDefaultProfileAsync(AiCapabilities.ImageEmbedding, cancellationToken);
            if (profile is null)
            {
                return new PhotoProfileResolution(
                    source, RequestedKey: null, Profile: null, Usable: false, AiUnavailableReasons.NoDefaultProfile);
            }
        }

        var reason = await ValidateUsableAsync(profile, cancellationToken);
        return new PhotoProfileResolution(
            source, requestedKey ?? profile.Key, profile, Usable: reason is null, reason);
    }

    // Whether the profile can host comparable image-embedding vectors. Returns a
    // sanitized reason token when not, else null. Deliberately does NOT check
    // backend/model-file readiness (reading stored vectors needs no live model).
    private async Task<string?> ValidateUsableAsync(AiProfile profile, CancellationToken cancellationToken)
    {
        if (!profile.Enabled)
        {
            return AiUnavailableReasons.ProfileDisabled;
        }

        if (!string.Equals(profile.Capability, AiCapabilities.ImageEmbedding, StringComparison.Ordinal))
        {
            return AiUnavailableReasons.CapabilityMismatch;
        }

        if (profile.Dimension is not > 0)
        {
            return AiUnavailableReasons.ProfileDimensionInvalid;
        }

        var model = await _registry.GetModelAsync(profile.AiModelId, cancellationToken);
        if (model is null || !model.Enabled)
        {
            return AiUnavailableReasons.ModelUnavailable;
        }

        // Real ONNX photo reads are single-generation: the retired 768 profile
        // cannot remain active accidentally after deploy. Deterministic dev/test
        // profiles keep their small dimensions for substrate tests only.
        if (string.Equals(model.Provider, AiProviders.Onnx, StringComparison.Ordinal)
            && profile.Dimension != PhotoVectorIndexService.SupportedDimension)
        {
            return AiUnavailableReasons.ProfileDimensionInvalid;
        }

        return null;
    }

    // Aggregate coverage for the given profile key. Null when the profile does
    // not exist (operator error). Counts only — no internal identifiers.
    public async Task<PhotoEmbeddingCoverage?> GetCoverageAsync(
        string profileKey, CancellationToken cancellationToken = default)
    {
        var profile = await _registry.GetProfileByKeyAsync(profileKey, cancellationToken);
        if (profile is null)
        {
            return null;
        }

        var eligible = await EligibleImageBlobs().LongCountAsync(cancellationToken);
        var embedded = await EligibleImageBlobs()
            .Where(id => _db.BlobEmbeddings.Any(e => e.BlobObjectId == id && e.ProfileId == profile.Id))
            .LongCountAsync(cancellationToken);
        var missing = Math.Max(0, eligible - embedded);
        var percent = eligible == 0 ? 0d : Math.Round(embedded * 100.0 / eligible, 2);

        // Vector (pgvector) coverage, when a vector table exists for this
        // profile's dimension. Vectors mirror canonical embeddings, so the
        // denominator is `embedded`. Unsupported dimension / no pgvector =>
        // not vector-indexed; the read path uses exact-scan there.
        var vectorSupported = await _vectors.IsBackendAvailableAsync(profile.Dimension, cancellationToken);
        var vectorIndexed = vectorSupported
            ? await _vectors.CountIndexedAsync(profile.Id, cancellationToken)
            : 0;
        var missingVectors = vectorSupported ? Math.Max(0, embedded - vectorIndexed) : 0;
        var vectorPercent = vectorSupported && embedded > 0
            ? Math.Round(vectorIndexed * 100.0 / embedded, 2)
            : 0d;

        return new PhotoEmbeddingCoverage(
            profile.Key, eligible, embedded, missing, percent, profile.Dimension, profile.DistanceMetric,
            vectorSupported, vectorIndexed, missingVectors, vectorPercent);
    }

    // Explicit destructive cleanup after the 1152 profile is fully populated.
    // Nothing happens unless the configured active profile is the approved v2
    // profile AND both canonical/vector coverage are complete. `execute=false`
    // is the mandatory dry-run path exposed by the CLI.
    public async Task<LegacyPhotoProfileRetirement> RetireLegacy768Async(
        bool execute, CancellationToken cancellationToken = default)
    {
        var active = await ResolveActiveProfileAsync(null, cancellationToken);
        if (!active.Usable || active.Profile is null
            || active.Profile.Key != OnnxImageModels.SiglipSo400mProfileKey
            || active.Profile.Dimension != PhotoVectorIndexService.SupportedDimension)
        {
            return LegacyPhotoProfileRetirement.NotReady("active-profile-not-1152");
        }

        var coverage = await GetCoverageAsync(active.Profile.Key, cancellationToken);
        if (coverage is null
            || coverage.Embedded != coverage.EligibleImages
            || !coverage.VectorSupported
            || coverage.VectorIndexed != coverage.Embedded)
        {
            return LegacyPhotoProfileRetirement.NotReady("1152-coverage-incomplete");
        }

        var legacyProfiles = await _db.AiProfiles
            .Where(p => p.Capability == AiCapabilities.ImageEmbedding && p.Dimension == 768)
            .ToListAsync(cancellationToken);
        var legacyIds = legacyProfiles.Select(p => p.Id).ToList();
        var embeddings = await _db.BlobEmbeddings
            .LongCountAsync(e => legacyIds.Contains(e.ProfileId), cancellationToken);

        if (!execute)
        {
            return new LegacyPhotoProfileRetirement(true, false, null, legacyProfiles.Count, embeddings);
        }

        await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken);
        foreach (var profile in legacyProfiles)
        {
            profile.Enabled = false;
            profile.IsDefault = false;
            profile.UpdatedAt = _clock.GetUtcNow().UtcDateTime;
        }
        var legacyModels = await _db.AiModels
            .Where(m => m.Capability == AiCapabilities.ImageEmbedding && m.Dimension == 768)
            .ToListAsync(cancellationToken);
        foreach (var model in legacyModels)
        {
            model.Enabled = false;
            model.UpdatedAt = _clock.GetUtcNow().UtcDateTime;
        }
        await _db.SaveChangesAsync(cancellationToken);
        await _db.BlobEmbeddings
            .Where(e => legacyIds.Contains(e.ProfileId))
            .ExecuteDeleteAsync(cancellationToken);
        if (_db.Database.IsNpgsql())
        {
            await _db.Database.ExecuteSqlRawAsync(
                "DROP TABLE IF EXISTS blob_embedding_vectors_768;", cancellationToken);
        }
        await tx.CommitAsync(cancellationToken);
        return new LegacyPhotoProfileRetirement(true, true, null, legacyProfiles.Count, embeddings);
    }

    // Eligible image blobs: an image-category blob referenced by at least one
    // active (non-deleted) FileItem. IDENTICAL to PhotoEmbeddingBackfillService's
    // candidate eligibility (minus the per-profile not-yet-indexed filter).
    private IQueryable<Guid> EligibleImageBlobs() =>
        from b in _db.BlobObjects.AsNoTracking()
        where _db.BlobMetadata.Any(m => m.BlobObjectId == b.Id && m.MediaCategory == MediaCategories.Image)
            && _db.FileItems.Any(f => f.BlobObjectId == b.Id && f.DeletedAt == null)
        select b.Id;
}

// Where the active profile decision came from (sanitized; safe to surface).
public enum PhotoProfileSource
{
    Override,         // operator CLI --profile <key>
    Configured,       // Ai__PhotoSimilarityProfileKey
    DefaultFallback,  // capability default profile (documented fallback)
}

// Outcome of resolving the active photo-similarity profile. Carries only the
// profile's STABLE KEY and sanitized facts; never a GUID, vector, or path.
public sealed record PhotoProfileResolution(
    PhotoProfileSource Source,
    string? RequestedKey,
    AiProfile? Profile,
    bool Usable,
    string? UnavailableReason);

// Aggregate-only coverage snapshot. No internal identifiers. Vector* fields
// describe pgvector ANN coverage; VectorSupported is false when no vector table
// exists for the profile's dimension (or pgvector is unavailable) — the read
// path then uses exact-scan.
public sealed record PhotoEmbeddingCoverage(
    string ProfileKey,
    long EligibleImages,
    long Embedded,
    long Missing,
    double CoveragePercent,
    int? Dimension,
    string? DistanceMetric,
    bool VectorSupported,
    long VectorIndexed,
    long MissingVectors,
    double VectorCoveragePercent);

public sealed record LegacyPhotoProfileRetirement(
    bool Ready,
    bool Executed,
    string? Reason,
    int LegacyProfiles,
    long LegacyEmbeddings)
{
    public static LegacyPhotoProfileRetirement NotReady(string reason) =>
        new(false, false, reason, 0, 0);
}
