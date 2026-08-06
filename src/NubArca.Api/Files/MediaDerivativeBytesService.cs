using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using NubArca.Api.Data;
using NubArca.Api.Storage;

namespace NubArca.Api.Files;

// Slice 96: physical-placement verification and repair for derived artifacts.
//
// The backfill (MediaDerivativesBackfillService) works ROW-first: it finds
// files with no FileThumbnail row of a size and generates them. This service
// covers the orthogonal failure the union-based integrity scan cannot see:
// the FileThumbnail row EXISTS and its BlobObject row is consistent, but the
// physical bytes are not in the derived root the serving endpoints read from
// (typically: Storage:DerivedRootPath was introduced or changed after the
// artifacts were generated, so they still sit in the original root). In that
// state the gallery silently regenerates every artifact on first view —
// CPU-heavy ImageSharp work for bytes that already exist on disk.
//
// verify-bytes classifies every FileThumbnail row's bytes as present in the
// derived root / only in the original root / missing from both. repair-bytes
// fixes the placement by STREAMING the bytes across roots (no decode, no DB
// mutation); rows missing from both roots are left alone unless
// --regenerate-missing explicitly asks for the standard regeneration path.
//
// Output is counts and millisecond aggregates only — never storage keys,
// SHAs, ids, names, paths, or metadata.
public sealed class MediaDerivativeBytesService
{
    private readonly AppDbContext _db;
    private readonly IBlobStorage _storage;
    private readonly IBlobStorage _derivedStorage;
    private readonly IFileThumbnailService _thumbnails;
    private readonly IBlobService _blobs;

    public MediaDerivativeBytesService(
        AppDbContext db,
        IBlobStorage storage,
        IFileThumbnailService thumbnails,
        IBlobService blobs,
        IDerivedBlobStorage? derivedStorage = null)
    {
        _db = db;
        _storage = storage;
        _derivedStorage = derivedStorage ?? storage;
        _thumbnails = thumbnails;
        _blobs = blobs;
    }

    public async Task<DerivativeBytesVerifyResult> VerifyAsync(
        MediaDerivativeBytesOptions options,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var sw = Stopwatch.StartNew();
        var rows = await LoadRowsAsync(options, cancellationToken);

        var acc = new Accumulator();
        long bytesCopyable = 0;
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var placement = await ClassifyAsync(row.StorageKey, cancellationToken);
            acc.Count(row.Size, placement);
            if (placement == Placement.OnlyInOriginalRoot)
            {
                bytesCopyable += row.SizeBytes;
            }

            if (acc.Checked % 500 == 0)
            {
                log?.Invoke(
                    $"media derivatives verify-bytes: {acc.Checked}/{rows.Count} checked "
                    + $"(present {acc.Present}, only-original {acc.OnlyOriginal}, missing {acc.MissingBoth}).");
            }
        }

        sw.Stop();
        return new DerivativeBytesVerifyResult(
            acc.Checked,
            acc.Present,
            acc.OnlyOriginal,
            acc.MissingBoth,
            bytesCopyable,
            sw.ElapsedMilliseconds,
            acc.For(ThumbnailSizes.Small),
            acc.For(ThumbnailSizes.Medium),
            acc.For(ThumbnailSizes.Poster));
    }

    public async Task<DerivativeBytesRepairResult> RepairAsync(
        MediaDerivativeBytesOptions options,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var sw = Stopwatch.StartNew();
        var rows = await LoadRowsAsync(options, cancellationToken);

        // The per-size trio reuses the verify semantics: "present" = skipped,
        // "only-original" = copied (or would be, in a dry run), "missing" =
        // left alone / handed to regeneration.
        var acc = new Accumulator();
        var regenerated = 0;
        var failed = 0;
        long bytesCopied = 0;

        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var placement = await ClassifyAsync(row.StorageKey, cancellationToken);
            switch (placement)
            {
                case Placement.PresentInDerivedRoot:
                    acc.Count(row.Size, placement);
                    break;

                case Placement.OnlyInOriginalRoot:
                    if (options.DryRun)
                    {
                        acc.Count(row.Size, placement);
                        bytesCopied += row.SizeBytes;
                        break;
                    }
                    if (await CopyToDerivedRootAsync(row.StorageKey, cancellationToken))
                    {
                        acc.Count(row.Size, placement);
                        bytesCopied += row.SizeBytes;
                    }
                    else
                    {
                        // Source bytes no longer hash to their key (corrupt
                        // original-root copy) — never installed under the
                        // expected key. Counted, not detailed.
                        acc.Count(row.Size, Placement.PresentInDerivedRoot, countOnly: true);
                        failed++;
                    }
                    break;

                case Placement.MissingFromBoth:
                    if (options.RegenerateMissing && !options.DryRun)
                    {
                        acc.Count(row.Size, placement, countOnly: true);
                        if (await RegenerateAsync(row, cancellationToken))
                        {
                            regenerated++;
                        }
                        else
                        {
                            failed++;
                        }
                    }
                    else
                    {
                        acc.Count(row.Size, placement);
                    }
                    break;
            }

            if (acc.Checked % 500 == 0)
            {
                log?.Invoke(
                    $"media derivatives repair-bytes: {acc.Checked}/{rows.Count} checked "
                    + $"(skipped {acc.Present}, copied {acc.OnlyOriginal}, missing {acc.MissingBoth}, "
                    + $"regenerated {regenerated}, failed {failed}).");
            }
        }

        sw.Stop();
        return new DerivativeBytesRepairResult(
            acc.Checked,
            acc.Present,
            acc.OnlyOriginal,
            acc.MissingBoth,
            regenerated,
            failed,
            bytesCopied,
            sw.ElapsedMilliseconds,
            options.DryRun,
            acc.For(ThumbnailSizes.Small),
            acc.For(ThumbnailSizes.Medium),
            acc.For(ThumbnailSizes.Poster));
    }

    private enum Placement
    {
        PresentInDerivedRoot,
        OnlyInOriginalRoot,
        MissingFromBoth,
    }

    private async Task<Placement> ClassifyAsync(string storageKey, CancellationToken cancellationToken)
    {
        if (await _derivedStorage.ExistsAsync(storageKey, cancellationToken))
        {
            return Placement.PresentInDerivedRoot;
        }
        if (await _storage.ExistsAsync(storageKey, cancellationToken))
        {
            return Placement.OnlyInOriginalRoot;
        }
        return Placement.MissingFromBoth;
    }

    // Streams the bytes across roots: temp file + atomic rename, re-hashed on
    // the way (a corrupt source cannot land under the expected key), byte-
    // idempotent when a concurrent request already restored the same key.
    private async Task<bool> CopyToDerivedRootAsync(string storageKey, CancellationToken cancellationToken)
    {
        await using var source = await _storage.OpenReadAsync(storageKey, cancellationToken);
        var write = await _derivedStorage.WriteAsync(source, cancellationToken);
        return string.Equals(write.StorageKey, storageKey, StringComparison.Ordinal);
    }

    // Explicit --regenerate-missing only: same pattern as the poster
    // regeneration CLI — drop the orphan row, release its derived blob
    // reference, then go through the standard generation-only path.
    private async Task<bool> RegenerateAsync(ThumbnailRow row, CancellationToken cancellationToken)
    {
        try
        {
            var deleted = await _db.FileThumbnails
                .Where(t => t.Id == row.ThumbnailId)
                .ExecuteDeleteAsync(cancellationToken);
            if (deleted == 1)
            {
                try { await _blobs.ReleaseAsync(row.BlobObjectId, CancellationToken.None); }
                catch { /* best effort; reconcile reports leftovers */ }
            }

            if (row.Size == ThumbnailSizes.Poster)
            {
                var outcome = await _thumbnails.EnsurePosterGeneratedAsync(
                    row.FileItemId, row.OwnerUserId, cancellationToken);
                return outcome is DerivativeOutcome.Generated or DerivativeOutcome.SkippedExisting;
            }
            if (row.Size == ThumbnailSizes.VideoPreviewStrip)
            {
                var outcome = await _thumbnails.EnsureVideoPreviewStripGeneratedAsync(
                    row.FileItemId, row.OwnerUserId, cancellationToken);
                return outcome is DerivativeOutcome.Generated or DerivativeOutcome.SkippedExisting;
            }

            var result = await _thumbnails.EnsureImageDerivativesAsync(
                row.FileItemId, row.OwnerUserId, new[] { row.Size }, cancellationToken);
            var sizeOutcome = result.Outcomes.FirstOrDefault(o => o.Size == row.Size);
            return sizeOutcome?.Outcome is DerivativeOutcome.Generated or DerivativeOutcome.SkippedExisting;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // No exception detail — it could echo paths or metadata.
            return false;
        }
    }

    private async Task<List<ThumbnailRow>> LoadRowsAsync(
        MediaDerivativeBytesOptions options, CancellationToken cancellationToken)
    {
        var thumbnails = _db.FileThumbnails.AsNoTracking();
        if (options.Size is { } size)
        {
            var normalized = ThumbnailSizes.Normalize(size);
            thumbnails = thumbnails.Where(t => t.Size == normalized);
        }

        var query =
            from t in thumbnails
            join b in _db.BlobObjects.AsNoTracking() on t.BlobObjectId equals b.Id
            join f in _db.FileItems.AsNoTracking() on t.FileItemId equals f.Id
            orderby t.CreatedAt, t.Id
            select new ThumbnailRow(t.Id, t.FileItemId, f.OwnerUserId, t.Size, t.BlobObjectId, b.StorageKey, b.SizeBytes);

        if (options.Limit is int limit)
        {
            query = query.Take(limit);
        }

        return await query.ToListAsync(cancellationToken);
    }

    private sealed record ThumbnailRow(
        Guid ThumbnailId,
        Guid FileItemId,
        Guid OwnerUserId,
        string Size,
        Guid BlobObjectId,
        string StorageKey,
        long SizeBytes);

    // Totals + per-size buckets in one place. `countOnly` bumps Checked
    // without classifying (used when the row's outcome is reported through a
    // dedicated counter such as failed/regenerated).
    private sealed class Accumulator
    {
        private readonly Dictionary<string, int[]> _bySize = new(StringComparer.Ordinal);

        public int Checked { get; private set; }
        public int Present { get; private set; }
        public int OnlyOriginal { get; private set; }
        public int MissingBoth { get; private set; }

        public void Count(string size, Placement placement, bool countOnly = false)
        {
            Checked++;
            var buckets = Bucket(size);
            buckets[0]++;
            if (countOnly)
            {
                return;
            }
            switch (placement)
            {
                case Placement.PresentInDerivedRoot:
                    Present++;
                    buckets[1]++;
                    break;
                case Placement.OnlyInOriginalRoot:
                    OnlyOriginal++;
                    buckets[2]++;
                    break;
                case Placement.MissingFromBoth:
                    MissingBoth++;
                    buckets[3]++;
                    break;
            }
        }

        public DerivativeBytesSizeCounts For(string size)
        {
            var b = Bucket(size);
            return new DerivativeBytesSizeCounts(b[0], b[1], b[2], b[3]);
        }

        private int[] Bucket(string size)
        {
            if (!_bySize.TryGetValue(size, out var buckets))
            {
                buckets = new int[4];
                _bySize[size] = buckets;
            }
            return buckets;
        }
    }
}

public sealed record MediaDerivativeBytesOptions
{
    // small | medium | poster | video-preview-strip; null = all sizes.
    public string? Size { get; init; }
    public int? Limit { get; init; }
    public bool DryRun { get; init; }
    // repair-bytes only: rows whose bytes are missing from BOTH roots go
    // through the standard (CPU-heavy) regeneration path. Default off — the
    // copy-only repair never decodes and never mutates the DB.
    public bool RegenerateMissing { get; init; }
}

public sealed record DerivativeBytesSizeCounts(
    int Checked,
    int PresentInDerivedRoot,
    int OnlyInOriginalRoot,
    int MissingFromBoth);

public sealed record DerivativeBytesVerifyResult(
    int Checked,
    int PresentInDerivedRoot,
    int OnlyInOriginalRoot,
    int MissingFromBoth,
    // Sum of BlobObject.SizeBytes over only-in-original rows: how much a
    // copy-only repair would move, without touching the filesystem twice.
    long BytesCopyable,
    long ElapsedMillis,
    DerivativeBytesSizeCounts Small,
    DerivativeBytesSizeCounts Medium,
    DerivativeBytesSizeCounts Poster);

public sealed record DerivativeBytesRepairResult(
    int Checked,
    int SkippedPresentInDerivedRoot,
    int CopiedFromOriginalRoot,
    int MissingFromBoth,
    int Regenerated,
    int Failed,
    // In a dry run: the bytes that WOULD be copied.
    long BytesCopied,
    long ElapsedMillis,
    bool DryRun,
    DerivativeBytesSizeCounts Small,
    DerivativeBytesSizeCounts Medium,
    DerivativeBytesSizeCounts Poster);
