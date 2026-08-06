using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Storage;

namespace NubArca.Api.Files;

// Slice 100: a read-only, in-memory benchmark comparing the image derivative
// backends on real library images. It samples up to N image source blobs,
// renders small + medium with each AVAILABLE backend (discarding the output —
// nothing is stored, no FileThumbnail rows, no refcount changes), and reports
// aggregate timings + the vips/ImageSharp speedup. Counts and milliseconds only
// — never a file name, path, storage key, id, or metadata.
public sealed class DerivativeBenchmarkService
{
    private readonly AppDbContext _db;
    private readonly IBlobService _blobService;
    private readonly ImageSharpDerivativeBackend _imageSharp;
    private readonly VipsDerivativeBackend _vips;
    private readonly MediaDerivativesOptions _options;

    public DerivativeBenchmarkService(
        AppDbContext db,
        IBlobService blobService,
        ImageSharpDerivativeBackend imageSharp,
        VipsDerivativeBackend vips,
        Microsoft.Extensions.Options.IOptions<MediaDerivativesOptions> options)
    {
        _db = db;
        _blobService = blobService;
        _imageSharp = imageSharp;
        _vips = vips;
        _options = options.Value;
    }

    public async Task<DerivativeBenchmarkResult> RunAsync(
        int limit, Action<string>? log = null, CancellationToken cancellationToken = default)
    {
        var requests = new[]
        {
            new DerivativeRequest(ThumbnailSizes.Small, _options.EdgeFor(ThumbnailSizes.Small), _options.QualityFor(ThumbnailSizes.Small)),
            new DerivativeRequest(ThumbnailSizes.Medium, _options.EdgeFor(ThumbnailSizes.Medium), _options.QualityFor(ThumbnailSizes.Medium)),
        };

        // Sample distinct image source blobs (oldest first for determinism).
        var blobIds = await _db.FileItems.AsNoTracking()
            .Where(f => f.DeletedAt == null
                && _db.BlobMetadata.Any(m => m.BlobObjectId == f.BlobObjectId
                    && m.MediaCategory == MediaCategories.Image
                    && m.DetectedContentType != null))
            .OrderBy(f => f.CreatedAt).ThenBy(f => f.Id)
            .Select(f => f.BlobObjectId)
            .Distinct()
            .Take(limit)
            .ToListAsync(cancellationToken);

        log?.Invoke($"media derivatives benchmark: sampling {blobIds.Count} image(s); sizes=small+medium.");

        // Read all source bytes up front so disk I/O is not attributed to a backend.
        var sources = new List<byte[]>(blobIds.Count);
        foreach (var id in blobIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var stream = await _blobService.OpenContentAsync(id, cancellationToken);
                using var ms = new MemoryStream();
                await stream.CopyToAsync(ms, cancellationToken);
                sources.Add(ms.ToArray());
            }
            catch
            {
                // Skip a blob whose bytes are unreadable — not part of the comparison.
            }
        }

        var imageSharp = await MeasureAsync(_imageSharp, sources, requests, cancellationToken);
        BackendBenchmark? vips = null;
        string? vipsUnavailable = null;
        if (_vips.IsAvailable)
        {
            vips = await MeasureAsync(_vips, sources, requests, cancellationToken);
        }
        else
        {
            vipsUnavailable = "libvips native library not available";
        }

        return new DerivativeBenchmarkResult(sources.Count, imageSharp, vips, vipsUnavailable);
    }

    private static async Task<BackendBenchmark> MeasureAsync(
        IImageDerivativeBackend backend,
        IReadOnlyList<byte[]> sources,
        IReadOnlyList<DerivativeRequest> requests,
        CancellationToken cancellationToken)
    {
        // Warm up (native load + JIT) on the first decodable image; not timed.
        foreach (var src in sources)
        {
            try { _ = await backend.RenderAsync(src, requests, cancellationToken); break; }
            catch (ImageBackendException) { /* try next */ }
        }

        var ok = 0;
        var failed = 0;
        long totalMillis = 0;
        long totalBytes = 0;
        foreach (var src in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var started = Stopwatch.GetTimestamp();
            try
            {
                var results = await backend.RenderAsync(src, requests, cancellationToken);
                totalMillis += (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                ok++;
                foreach (var r in results)
                {
                    if (r is not null) totalBytes += r.Jpeg.Length;
                }
            }
            catch (ImageBackendException)
            {
                failed++;
            }
        }
        return new BackendBenchmark(backend.Name, ok, failed, totalMillis, totalBytes);
    }
}

public sealed record BackendBenchmark(
    string Name, int Images, int Failed, long TotalMillis, long TotalOutputBytes)
{
    public double AvgMillis => Images > 0 ? (double)TotalMillis / Images : 0;
}

public sealed record DerivativeBenchmarkResult(
    int SampledImages,
    BackendBenchmark ImageSharp,
    BackendBenchmark? Vips,
    string? VipsUnavailableReason)
{
    // vips speedup vs ImageSharp on the per-image average (>1 = vips faster).
    public double? Speedup =>
        Vips is { AvgMillis: > 0 } v && ImageSharp.AvgMillis > 0
            ? ImageSharp.AvgMillis / v.AvgMillis
            : null;
}
