using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NubArca.Api.Ai.Backends;
using NubArca.Api.Ai.Resolution;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Storage;

namespace NubArca.Api.Ai.Onnx.Face;

// Read-only evaluation harness for local ONNX face-recognition models. Powers the
// `ai face …` operator CLI. It NEVER writes FaceDetection/FaceEmbedding/status
// rows, never clusters/names faces, and surfaces only safe data: counts, timings,
// dimensions, rounded detection scores, normalized bounding boxes, and rounded
// similarity scores. No raw vectors, BlobObjectId, SHA, StorageKey, physical
// paths, or model internals are returned.
//
// Availability is resolved through IAiBackendResolver, so AI-disabled / provider
// "none" / a missing model file all report a clean "unavailable" reason rather
// than throwing. Candidate/file queries go through the vault-filtered FileItems
// set, so Private Vault content is never processed or revealed.
public sealed class OnnxFaceEvaluationService
{
    private readonly AppDbContext _db;
    private readonly IBlobService _blobs;
    private readonly IAiBackendResolver _resolver;
    private readonly IAiProfileRegistry _registry;
    private readonly IOptions<AiOptions> _options;

    public OnnxFaceEvaluationService(
        AppDbContext db,
        IBlobService blobs,
        IAiBackendResolver resolver,
        IAiProfileRegistry registry,
        IOptions<AiOptions> options)
    {
        _db = db;
        _blobs = blobs;
        _resolver = resolver;
        _registry = registry;
        _options = options;
    }

    // Configured active face profile key (Ai__FaceProfileKey), or null. Purely a
    // convenience default for the CLI — never auto-enables face processing.
    public string? ConfiguredProfileKey => _options.Value.FaceProfileKey;

    // Lists the face model packages + whether each required file is present on
    // disk. Reports only booleans + safe config — never a model path.
    public IReadOnlyList<FaceModelInfo> ListModels()
    {
        var modelDir = _options.Value.Onnx.ModelDir;
        var dirConfigured = !string.IsNullOrWhiteSpace(modelDir);
        var list = new List<FaceModelInfo>();
        foreach (var (profileKey, catalogKey) in OnnxFaceModels.ProfileToCatalogKey)
        {
            var c = OnnxFaceModels.Catalog[catalogKey];
            var detectorPresent = dirConfigured
                && File.Exists(Path.Combine(modelDir!, c.PackageSubdir, c.DetectorFile));
            var recognitionPresent = dirConfigured
                && File.Exists(Path.Combine(modelDir!, c.PackageSubdir, c.RecognitionFile));
            list.Add(new FaceModelInfo(
                c.Key, profileKey, c.Capability, c.DetectorInputSize, c.RecognitionInputSize,
                c.LandmarkCount, c.Dimension, c.DistanceMetric,
                dirConfigured, detectorPresent, recognitionPresent, c.LicenseNote));
        }

        return list.OrderBy(m => m.ModelKey, StringComparer.Ordinal).ToList();
    }

    public async Task<FaceDetectTestResult> DetectTestAsync(
        Guid fileItemId, string profileKey, CancellationToken cancellationToken = default)
    {
        var (backend, profile, unavailable) = await ResolveAsync(profileKey, cancellationToken);
        if (unavailable is not null)
        {
            return new FaceDetectTestResult(false, unavailable, false, 0, null, null, null, Array.Empty<FaceBox>());
        }

        var blobId = await ResolveOwnerFileBlobAsync(fileItemId, cancellationToken);
        if (blobId is null)
        {
            return new FaceDetectTestResult(true, null, false, 0, null, null, null, Array.Empty<FaceBox>());
        }

        var bytes = await ReadBlobAsync(blobId.Value, cancellationToken);
        var r = await backend!.RunAsync(bytes, profile!, OnnxFaceBackend.EmbedMode.None, 0, cancellationToken);
        var boxes = r.Faces
            .Select(f => new FaceBox(
                f.Score is { } s ? Math.Round(s, 4) : null,
                Math.Round(f.X, 4), Math.Round(f.Y, 4), Math.Round(f.Width, 4), Math.Round(f.Height, 4),
                f.Landmarks is { Count: > 0 }))
            .ToList();
        return new FaceDetectTestResult(
            true, null, true, r.Faces.Count, r.ImageWidth, r.ImageHeight, r.Diagnostic, boxes);
    }

    public async Task<FaceEmbedTestResult> EmbedTestAsync(
        Guid fileItemId, string profileKey, int faceIndex, CancellationToken cancellationToken = default)
    {
        var (backend, profile, unavailable) = await ResolveAsync(profileKey, cancellationToken);
        if (unavailable is not null)
        {
            return new FaceEmbedTestResult(false, unavailable, false, 0, null, null, null, null, null, null, null);
        }

        var blobId = await ResolveOwnerFileBlobAsync(fileItemId, cancellationToken);
        if (blobId is null)
        {
            return new FaceEmbedTestResult(true, null, false, 0, null, null, null, null, null, null, null);
        }

        var bytes = await ReadBlobAsync(blobId.Value, cancellationToken);
        var r = await backend!.RunAsync(bytes, profile!, OnnxFaceBackend.EmbedMode.Specific, faceIndex, cancellationToken);

        if (r.Embedding is null)
        {
            return new FaceEmbedTestResult(
                true, null, true, r.Faces.Count, null, null, null, null, Math.Round(r.DetectMs, 1), null, r.Diagnostic);
        }

        double sumSq = 0;
        var finite = true;
        foreach (var v in r.Embedding)
        {
            if (float.IsNaN(v) || float.IsInfinity(v))
            {
                finite = false;
            }

            sumSq += (double)v * v;
        }

        return new FaceEmbedTestResult(
            true, null, true, r.Faces.Count, r.EmbeddedFaceIndex, r.EmbeddingDimension,
            Math.Sqrt(sumSq), finite, Math.Round(r.DetectMs, 1),
            r.EmbedMs is { } e ? Math.Round(e, 1) : null, r.Diagnostic);
    }

    public async Task<FaceCompareResult> CompareAsync(
        Guid fileA, int faceA, Guid fileB, int faceB, string profileKey,
        CancellationToken cancellationToken = default)
    {
        var (backend, profile, unavailable) = await ResolveAsync(profileKey, cancellationToken);
        if (unavailable is not null)
        {
            return new FaceCompareResult(false, unavailable, false, false, 0, 0, false, null, null);
        }

        var blobA = await ResolveOwnerFileBlobAsync(fileA, cancellationToken);
        var blobB = await ResolveOwnerFileBlobAsync(fileB, cancellationToken);
        if (blobA is null || blobB is null)
        {
            return new FaceCompareResult(true, null, blobA is not null, blobB is not null, 0, 0, false, null, null);
        }

        var ra = await backend!.RunAsync(
            await ReadBlobAsync(blobA.Value, cancellationToken), profile!,
            OnnxFaceBackend.EmbedMode.Specific, faceA, cancellationToken);
        var rb = await backend.RunAsync(
            await ReadBlobAsync(blobB.Value, cancellationToken), profile!,
            OnnxFaceBackend.EmbedMode.Specific, faceB, cancellationToken);

        if (ra.Embedding is null || rb.Embedding is null || ra.Embedding.Length != rb.Embedding.Length)
        {
            return new FaceCompareResult(true, null, true, true, ra.Faces.Count, rb.Faces.Count, false, null, null);
        }

        var cosine = Cosine(ra.Embedding, rb.Embedding);
        return new FaceCompareResult(
            true, null, true, true, ra.Faces.Count, rb.Faces.Count, true,
            Math.Round(cosine, 6), Math.Round(1 - cosine, 6));
    }

    public async Task<FaceBenchmarkResult> BenchmarkAsync(
        string profileKey, int limit, CancellationToken cancellationToken = default)
    {
        var (backend, profile, unavailable) = await ResolveAsync(profileKey, cancellationToken);
        if (unavailable is not null)
        {
            return FaceBenchmarkResult.Unavailable(unavailable, profileKey);
        }

        var take = Math.Clamp(limit, 1, 2000);
        var blobIds = await EligibleImageBlobs().Take(take).ToListAsync(cancellationToken);

        var detectTimes = new List<double>(blobIds.Count);
        var embedTimes = new List<double>();
        var failureReasons = new Dictionary<string, int>(StringComparer.Ordinal);
        int attempted = 0, succeeded = 0, failed = 0, facesDetected = 0, zeroFaceImages = 0, embedded = 0;

        foreach (var blobId in blobIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            attempted++;
            try
            {
                var bytes = await ReadBlobAsync(blobId, cancellationToken);
                var r = await backend!.RunAsync(bytes, profile!, OnnxFaceBackend.EmbedMode.First, 0, cancellationToken);
                succeeded++;
                detectTimes.Add(r.DetectMs);
                facesDetected += r.Faces.Count;
                if (r.Faces.Count == 0)
                {
                    zeroFaceImages++;
                }
                else if (r.Embedding is not null && r.EmbedMs is { } e)
                {
                    embedded++;
                    embedTimes.Add(e);
                }
                else
                {
                    Bump(failureReasons, r.Diagnostic ?? "embed-skipped");
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                failed++;
                Bump(failureReasons, "processing-error");
            }
        }

        detectTimes.Sort();
        embedTimes.Sort();
        return new FaceBenchmarkResult(
            true, null, profileKey, profile!.Dimension,
            attempted, succeeded, failed, facesDetected, zeroFaceImages, embedded,
            succeeded == 0 ? null : Math.Round(facesDetected / (double)succeeded, 3),
            Avg(detectTimes), Percentile(detectTimes, 50), Percentile(detectTimes, 95),
            Avg(embedTimes), Percentile(embedTimes, 50), Percentile(embedTimes, 95),
            failureReasons);
    }

    // Optional manual-evaluation aid: a safe list of owner file references + the
    // face count each image yields, so an operator can hand-pick same-person /
    // different-person pairs to feed `ai face compare`. Names + counts only.
    public async Task<FaceSamplePairsResult> SamplePairsAsync(
        string profileKey, int limit, CancellationToken cancellationToken = default)
    {
        var (backend, profile, unavailable) = await ResolveAsync(profileKey, cancellationToken);
        if (unavailable is not null)
        {
            return new FaceSamplePairsResult(false, unavailable, Array.Empty<FaceSampleItem>());
        }

        var take = Math.Clamp(limit, 1, 200);
        var candidates = await (
            from f in _db.FileItems.AsNoTracking()
            where f.DeletedAt == null
                && _db.BlobMetadata.Any(m => m.BlobObjectId == f.BlobObjectId && m.MediaCategory == MediaCategories.Image)
            orderby f.Id
            select new { f.Id, f.Name, f.BlobObjectId })
            .Take(take)
            .ToListAsync(cancellationToken);

        var items = new List<FaceSampleItem>(candidates.Count);
        foreach (var c in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var bytes = await ReadBlobAsync(c.BlobObjectId, cancellationToken);
                var r = await backend!.RunAsync(bytes, profile!, OnnxFaceBackend.EmbedMode.None, 0, cancellationToken);
                if (r.Faces.Count > 0)
                {
                    items.Add(new FaceSampleItem(c.Id, c.Name, r.Faces.Count));
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // skip unreadable/undecodable candidate
            }
        }

        return new FaceSamplePairsResult(true, null, items);
    }

    private async Task<(OnnxFaceBackend? Backend, Domain.Ai.AiProfile? Profile, string? Unavailable)> ResolveAsync(
        string profileKey, CancellationToken cancellationToken)
    {
        var res = await _resolver.ResolveForProfileKeyAsync<IFaceDetector>(profileKey, cancellationToken);
        if (!res.IsAvailable || res.Backend is null)
        {
            return (null, null, res.Resolution.UnavailableReason ?? AiUnavailableReasons.BackendNotReady);
        }

        if (res.Backend is not OnnxFaceBackend onnx)
        {
            // e.g. a deterministic face profile — not a real ONNX face model.
            return (null, null, "backend-not-onnx-face");
        }

        var profile = await _registry.GetProfileByKeyAsync(profileKey, cancellationToken);
        if (profile is null)
        {
            return (null, null, AiUnavailableReasons.ProfileNotFound);
        }

        return (onnx, profile, null);
    }

    // Owner-visible, non-vault file → its blob id. The default FileItems query
    // carries the global Private Vault filter, so a vaulted file resolves to null
    // ("not found") and is never processed.
    private async Task<Guid?> ResolveOwnerFileBlobAsync(Guid fileItemId, CancellationToken cancellationToken)
        => await _db.FileItems.AsNoTracking()
            .Where(f => f.Id == fileItemId && f.DeletedAt == null)
            .Select(f => (Guid?)f.BlobObjectId)
            .FirstOrDefaultAsync(cancellationToken);

    private IQueryable<Guid> EligibleImageBlobs() =>
        from b in _db.BlobObjects.AsNoTracking()
        where _db.BlobMetadata.Any(m => m.BlobObjectId == b.Id && m.MediaCategory == MediaCategories.Image)
            && _db.FileItems.Any(f => f.BlobObjectId == b.Id && f.DeletedAt == null)
        orderby b.Id
        select b.Id;

    private async Task<byte[]> ReadBlobAsync(Guid blobId, CancellationToken cancellationToken)
    {
        await using var stream = await _blobs.OpenContentAsync(blobId, cancellationToken);
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, cancellationToken);
        return ms.ToArray();
    }

    private static void Bump(Dictionary<string, int> counts, string key)
        => counts[key] = counts.TryGetValue(key, out var n) ? n + 1 : 1;

    private static double Cosine(float[] a, float[] b)
    {
        // Both vectors are L2-normalized, so the dot product is the cosine.
        double dot = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += (double)a[i] * b[i];
        }

        return dot;
    }

    private static double? Avg(IReadOnlyList<double> xs) => xs.Count == 0 ? null : Math.Round(xs.Average(), 1);

    private static double? Percentile(IReadOnlyList<double> sorted, double p)
    {
        if (sorted.Count == 0)
        {
            return null;
        }

        var rank = (int)Math.Ceiling(p / 100.0 * sorted.Count) - 1;
        return Math.Round(sorted[Math.Clamp(rank, 0, sorted.Count - 1)], 1);
    }
}

// ---- safe, aggregate/owner-private result DTOs (no vectors / storage ids) ----

public sealed record FaceModelInfo(
    string ModelKey, string ProfileKey, string Capability, int DetectorInputSize, int RecognitionInputSize,
    int LandmarkCount, int Dimension, string DistanceMetric,
    bool ModelDirConfigured, bool DetectorPresent, bool RecognitionPresent, string LicenseNote);

public sealed record FaceBox(
    double? Score, double X, double Y, double Width, double Height, bool HasLandmarks);

public sealed record FaceDetectTestResult(
    bool Available, string? UnavailableReason, bool Found, int FaceCount,
    int? ImageWidth, int? ImageHeight, string? Diagnostic, IReadOnlyList<FaceBox> Faces);

public sealed record FaceEmbedTestResult(
    bool Available, string? UnavailableReason, bool Found, int FaceCount,
    int? FaceIndex, int? Dimension, double? L2Norm, bool? Finite,
    double? DetectMs, double? EmbedMs, string? Diagnostic);

public sealed record FaceCompareResult(
    bool Available, string? UnavailableReason, bool FoundA, bool FoundB,
    int FaceCountA, int FaceCountB, bool HasScore, double? Cosine, double? Distance);

public sealed record FaceBenchmarkResult(
    bool Available, string? UnavailableReason, string ProfileKey, int? Dimension,
    int ImagesAttempted, int ImagesSucceeded, int ImagesFailed, int FacesDetected,
    int ZeroFaceImages, int FacesEmbedded, double? AvgFacesPerImage,
    double? DetectAvgMs, double? DetectP50Ms, double? DetectP95Ms,
    double? EmbedAvgMs, double? EmbedP50Ms, double? EmbedP95Ms,
    IReadOnlyDictionary<string, int> FailureReasons)
{
    public static FaceBenchmarkResult Unavailable(string reason, string profileKey) =>
        new(false, reason, profileKey, null, 0, 0, 0, 0, 0, 0, null,
            null, null, null, null, null, null, new Dictionary<string, int>());
}

public sealed record FaceSampleItem(Guid FileItemId, string Name, int FaceCount);

public sealed record FaceSamplePairsResult(
    bool Available, string? UnavailableReason, IReadOnlyList<FaceSampleItem> Items);
