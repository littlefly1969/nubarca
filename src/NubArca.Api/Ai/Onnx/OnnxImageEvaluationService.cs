using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NubArca.Api.Ai.Backends;
using NubArca.Api.Ai.Resolution;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Storage;

namespace NubArca.Api.Ai.Onnx;

// Phase 2A: read-only evaluation harness for local ONNX image embedders. Powers
// the `ai onnx image …` operator CLI. It NEVER writes BlobEmbedding/status rows
// (it is a dry-run benchmark/quality tool — production reindex is Phase 2B), and
// it surfaces only safe data: counts, timings, dimensions, and owner-private
// file NAMES + rounded scores. No raw vectors, BlobObjectId, SHA, StorageKey, or
// physical paths are returned.
//
// The embedder is resolved through IAiBackendResolver, so an unconfigured/absent
// ONNX model (or AI disabled) cleanly reports unavailable rather than throwing —
// and writes nothing.
public sealed class OnnxImageEvaluationService
{
    private readonly AppDbContext _db;
    private readonly IBlobService _blobs;
    private readonly IAiBackendResolver _resolver;
    private readonly IAiProfileRegistry _registry;
    private readonly IOptions<AiOptions> _options;

    public OnnxImageEvaluationService(
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

    // Lists the ONNX image model candidates + whether each is present on disk.
    // Reports only the configured/present booleans — never the model path.
    public IReadOnlyList<OnnxModelInfo> ListModels()
    {
        var modelDir = _options.Value.Onnx.ModelDir;
        var dirConfigured = !string.IsNullOrWhiteSpace(modelDir);
        var list = new List<OnnxModelInfo>();
        foreach (var (profileKey, catalogKey) in OnnxImageModels.ProfileToCatalogKey)
        {
            var c = OnnxImageModels.Catalog[catalogKey];
            var present = dirConfigured && File.Exists(Path.Combine(modelDir!, c.ModelSubdir, c.ModelFile));
            var textPresent = dirConfigured && c.TextModelFile is not null
                && File.Exists(Path.Combine(modelDir!, c.ModelSubdir, c.TextModelFile));
            var tokenizerPresent = dirConfigured && c.TokenizerFile is not null
                && File.Exists(Path.Combine(modelDir!, c.ModelSubdir, c.TokenizerFile));
            list.Add(new OnnxModelInfo(
                c.Key, profileKey, c.InputSize, c.ResizeMode, c.Dimension,
                dirConfigured, present, textPresent, tokenizerPresent));
        }
        return list.OrderBy(m => m.ModelKey, StringComparer.Ordinal).ToList();
    }

    public async Task<OnnxBenchmarkResult> BenchmarkAsync(
        string profileKey, int limit, CancellationToken cancellationToken = default)
    {
        var (embedder, profile, unavailable) = await ResolveAsync(profileKey, cancellationToken);
        if (unavailable is not null)
        {
            return new OnnxBenchmarkResult(false, unavailable, profileKey, null, 0, 0, 0, null, null, null);
        }

        var take = Math.Clamp(limit, 1, 1000);
        var blobIds = await EligibleImageBlobs().Take(take).ToListAsync(cancellationToken);

        var times = new List<double>(blobIds.Count);
        int attempted = 0, succeeded = 0, failed = 0;
        int? dimension = profile!.Dimension;
        foreach (var blobId in blobIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            attempted++;
            try
            {
                var bytes = await ReadBlobAsync(blobId, cancellationToken);
                var sw = Stopwatch.StartNew();
                var result = await embedder!.EmbedImageAsync(bytes, profile, cancellationToken);
                sw.Stop();
                times.Add(sw.Elapsed.TotalMilliseconds);
                dimension = result.Dimension;
                succeeded++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                failed++; // per-image failure (decode/inference) — bounded, never fatal
            }
        }

        times.Sort();
        return new OnnxBenchmarkResult(
            true, null, profileKey, dimension, attempted, succeeded, failed,
            Avg(times), Percentile(times, 50), Percentile(times, 95));
    }

    public async Task<OnnxEmbedTestResult> EmbedTestAsync(
        Guid fileItemId, string profileKey, CancellationToken cancellationToken = default)
    {
        var (embedder, profile, unavailable) = await ResolveAsync(profileKey, cancellationToken);
        if (unavailable is not null)
        {
            return new OnnxEmbedTestResult(false, unavailable, false, null, null, null, null);
        }

        var blobId = await _db.FileItems.AsNoTracking()
            .Where(f => f.Id == fileItemId && f.DeletedAt == null)
            .Select(f => (Guid?)f.BlobObjectId)
            .FirstOrDefaultAsync(cancellationToken);
        if (blobId is null)
        {
            return new OnnxEmbedTestResult(true, null, false, null, null, null, null);
        }

        var bytes = await ReadBlobAsync(blobId.Value, cancellationToken);
        var sw = Stopwatch.StartNew();
        var result = await embedder!.EmbedImageAsync(bytes, profile!, cancellationToken);
        sw.Stop();

        double sumSq = 0;
        var finite = true;
        foreach (var v in result.Vector)
        {
            if (float.IsNaN(v) || float.IsInfinity(v)) finite = false;
            sumSq += (double)v * v;
        }

        return new OnnxEmbedTestResult(
            true, null, true, result.Dimension, Math.Sqrt(sumSq), finite, sw.Elapsed.TotalMilliseconds);
    }

    public async Task<OnnxCompareResult> CompareAsync(
        Guid fileItemId, string profileKey, int limit, int candidateLimit, CancellationToken cancellationToken = default)
    {
        var (embedder, profile, unavailable) = await ResolveAsync(profileKey, cancellationToken);
        if (unavailable is not null)
        {
            return new OnnxCompareResult(false, unavailable, false, false, Array.Empty<OnnxSimilarItem>());
        }

        var query = await _db.FileItems.AsNoTracking()
            .Where(f => f.Id == fileItemId && f.DeletedAt == null)
            .Select(f => new { f.OwnerUserId, f.BlobObjectId })
            .FirstOrDefaultAsync(cancellationToken);
        if (query is null)
        {
            return new OnnxCompareResult(true, null, false, false, Array.Empty<OnnxSimilarItem>());
        }

        var queryBytes = await ReadBlobAsync(query.BlobObjectId, cancellationToken);
        var queryVec = (await embedder!.EmbedImageAsync(queryBytes, profile!, cancellationToken)).Vector;

        // Owner-scoped candidate sample: the query owner's OTHER image files.
        // Cross-owner candidates are impossible. Bounded by candidateLimit and
        // streamed one image at a time.
        var take = Math.Clamp(candidateLimit, 1, 2000);
        var candidates = await (
            from f in _db.FileItems.AsNoTracking()
            where f.OwnerUserId == query.OwnerUserId
                && f.DeletedAt == null
                && f.Id != fileItemId
                && _db.BlobMetadata.Any(m => m.BlobObjectId == f.BlobObjectId && m.MediaCategory == MediaCategories.Image)
            orderby f.Id
            select new { f.Name, f.BlobObjectId })
            .Take(take)
            .ToListAsync(cancellationToken);

        var scored = new List<OnnxSimilarItem>(candidates.Count);
        foreach (var c in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var bytes = await ReadBlobAsync(c.BlobObjectId, cancellationToken);
                var vec = (await embedder.EmbedImageAsync(bytes, profile!, cancellationToken)).Vector;
                if (vec.Length == queryVec.Length)
                {
                    scored.Add(new OnnxSimilarItem(c.Name, Math.Round(Cosine(queryVec, vec), 6)));
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // skip unreadable/incompatible candidate
            }
        }

        var top = scored
            .OrderByDescending(s => s.Score)
            .ThenBy(s => s.Name, StringComparer.Ordinal)
            .Take(Math.Clamp(limit, 1, 100))
            .ToList();
        return new OnnxCompareResult(true, null, true, true, top);
    }

    private async Task<(IImageEmbedder? Embedder, Domain.Ai.AiProfile? Profile, string? Unavailable)> ResolveAsync(
        string profileKey, CancellationToken cancellationToken)
    {
        var res = await _resolver.ResolveForProfileKeyAsync<IImageEmbedder>(profileKey, cancellationToken);
        if (!res.IsAvailable || res.Backend is null)
        {
            return (null, null, res.Resolution.UnavailableReason ?? AiUnavailableReasons.BackendNotReady);
        }

        var profile = await _registry.GetProfileByKeyAsync(profileKey, cancellationToken);
        if (profile is null)
        {
            return (null, null, AiUnavailableReasons.ProfileNotFound);
        }

        return (res.Backend, profile, null);
    }

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

    private static double Cosine(float[] a, float[] b)
    {
        // Embedder output is already L2-normalized, so the dot product is cosine.
        double dot = 0;
        for (var i = 0; i < a.Length; i++) dot += (double)a[i] * b[i];
        return dot;
    }

    private static double? Avg(IReadOnlyList<double> xs) => xs.Count == 0 ? null : xs.Average();

    private static double? Percentile(IReadOnlyList<double> sorted, double p)
    {
        if (sorted.Count == 0) return null;
        var rank = (int)Math.Ceiling(p / 100.0 * sorted.Count) - 1;
        return sorted[Math.Clamp(rank, 0, sorted.Count - 1)];
    }
}

// ---- safe, aggregate/owner-private result DTOs (no vectors / storage ids) ----

public sealed record OnnxModelInfo(
    string ModelKey, string ProfileKey, int InputSize, string ResizeMode, int Dimension,
    bool ModelDirConfigured, bool ModelPresent, bool TextModelPresent, bool TokenizerPresent);

public sealed record OnnxBenchmarkResult(
    bool Available, string? UnavailableReason, string ProfileKey, int? Dimension,
    int Attempted, int Succeeded, int Failed,
    double? AvgMs, double? P50Ms, double? P95Ms);

public sealed record OnnxEmbedTestResult(
    bool Available, string? UnavailableReason, bool Found, int? Dimension,
    double? L2Norm, bool? Finite, double? Ms);

public sealed record OnnxCompareResult(
    bool Available, string? UnavailableReason, bool Found, bool QueryEmbedded,
    IReadOnlyList<OnnxSimilarItem> Items);

public sealed record OnnxSimilarItem(string Name, double Score);
