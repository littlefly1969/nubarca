using Microsoft.EntityFrameworkCore;
using NubArca.Api.Data;
using NubArca.Api.Domain;

namespace NubArca.Api.Files;

// Slice 99: durable diagnostics for media-derivative generation. It owns the
// lifecycle of derivative_diagnostics rows (one per FileItem × size) plus the
// retry/backoff policy and the safe aggregates surfaced to Admin stats and the
// CLI.
//
// Invariants:
//   * file_thumbnails is still the ONLY record of a SUCCESSFUL artifact; this
//     service never writes there and never fabricates a thumbnail row.
//   * a diagnostic row means "this derivative is currently missing because …";
//     a successful (re)generation CLEARS it (ApplyImageOutcomeAsync), and a
//     thumbnail created by ANY path (e.g. the lazy endpoint) supersedes it
//     (PruneResolvedAsync / the live filter on every aggregate query).
//   * rows carry no StorageKey, SHA, BlobId, owner id, raw metadata, GPS,
//     secrets, or stack traces — only stable codes, counts, a sanitized bounded
//     message, and the non-sensitive sniffed content type / format.
public sealed class DerivativeDiagnosticsService
{
    private readonly AppDbContext _db;
    private readonly TimeProvider _clock;

    public DerivativeDiagnosticsService(AppDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    // Transient backoff: 15m, 30m, 1h, 2h, … capped at 6h. Permanent /
    // not-eligible rows get no NextRetryAt (retried only by a forced run).
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromHours(6);

    private const int MessageMaxLength = 200;
    private const int ContentTypeMaxLength = 255;
    private const int FormatMaxLength = 64;

    // Map an image generation outcome to durable diagnostic state: clear on
    // success, record the precise reason otherwise. A no-code outcome (the file
    // vanished mid-run) records nothing — there is nothing meaningful to explain
    // and the row would dangle off a deleted FileItem.
    public async Task ApplyImageOutcomeAsync(
        Guid fileItemId,
        ImageDerivativeOutcome outcome,
        string? detectedContentType,
        string? detectedFormat,
        CancellationToken cancellationToken = default)
    {
        if (outcome.Outcome is DerivativeOutcome.Generated or DerivativeOutcome.SkippedExisting)
        {
            await ClearAsync(fileItemId, outcome.Size, cancellationToken);
            return;
        }
        if (outcome.ErrorCode is null)
        {
            return;
        }
        // Slice 100: record the backend that ultimately produced the failure and
        // whether a fallback was attempted (a safe, bounded token — no paths/ids).
        var backend = outcome.Backend ?? DerivativeBackends.ImageSharp;
        var message = outcome.FellBack ? "fell_back_to_imagesharp" : null;
        await RecordAsync(
            fileItemId,
            outcome.Size,
            MapStatus(outcome.ErrorCode, outcome.Permanent),
            outcome.ErrorCode,
            detectedContentType,
            detectedFormat,
            backend,
            DerivativeGenerators.ImageVersion,
            message,
            cancellationToken);
    }

    // Poster outcomes are coarse (the synthetic provider effectively never
    // fails; an FFmpeg provider might). A failure is recorded transient so a
    // re-run or a provider change can retry it.
    public async Task ApplyPosterOutcomeAsync(
        Guid fileItemId,
        DerivativeOutcome outcome,
        string? detectedContentType,
        string? detectedFormat,
        CancellationToken cancellationToken = default)
    {
        if (outcome is DerivativeOutcome.Generated or DerivativeOutcome.SkippedExisting)
        {
            await ClearAsync(fileItemId, ThumbnailSizes.Poster, cancellationToken);
            return;
        }
        var code = outcome == DerivativeOutcome.NotEligible
            ? DerivativeErrorCodes.NotEligible
            : DerivativeErrorCodes.Unknown;
        await RecordAsync(
            fileItemId,
            ThumbnailSizes.Poster,
            MapStatus(code, permanent: false),
            code,
            detectedContentType,
            detectedFormat,
            backend: null,
            DerivativeGenerators.PosterVersion,
            cancellationToken: cancellationToken);
    }

    // Filmstrip failures never enter an automatic retry cycle: this derivative
    // can be requested by every hover/focus event, so retrying transiently here
    // would recreate the same load storm fixed for image previews. A forced
    // backfill remains the single explicit retry path.
    public async Task ApplyVideoPreviewStripOutcomeAsync(
        Guid fileItemId,
        DerivativeOutcome outcome,
        string? detectedContentType,
        string? detectedFormat,
        CancellationToken cancellationToken = default)
    {
        if (outcome is DerivativeOutcome.Generated or DerivativeOutcome.SkippedExisting)
        {
            await ClearAsync(fileItemId, ThumbnailSizes.VideoPreviewStrip, cancellationToken);
            return;
        }
        var code = outcome == DerivativeOutcome.NotEligible
            ? DerivativeErrorCodes.NotEligible
            : DerivativeErrorCodes.Unknown;
        await RecordAsync(
            fileItemId,
            ThumbnailSizes.VideoPreviewStrip,
            outcome == DerivativeOutcome.NotEligible
                ? DerivativeStatuses.NotEligible
                : DerivativeStatuses.FailedPermanent,
            code,
            detectedContentType,
            detectedFormat,
            DerivativeBackends.Ffmpeg,
            DerivativeGenerators.VideoPreviewStripVersion,
            cancellationToken: cancellationToken);
    }

    // (FileItemId, Size) upsert: a fresh row records FirstAttemptedAt; a repeat
    // increments AttemptCount and recomputes the transient backoff. Set-based
    // update bypasses change tracking so it is safe to call on the shared scoped
    // context the backfill reuses across items.
    public async Task RecordAsync(
        Guid fileItemId,
        string size,
        string status,
        string? errorCode,
        string? detectedContentType,
        string? detectedFormat,
        string? backend,
        int generatorVersion,
        string? message = null,
        CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var existing = await _db.DerivativeDiagnostics.AsNoTracking()
            .Where(d => d.FileItemId == fileItemId && d.Size == size)
            .Select(d => new { d.Id, d.AttemptCount })
            .FirstOrDefaultAsync(cancellationToken);

        var attempt = (existing?.AttemptCount ?? 0) + 1;
        DateTime? nextRetry = status == DerivativeStatuses.FailedTransient
            ? now + Backoff(attempt)
            : null;
        var trimmedMessage = Truncate(message, MessageMaxLength);
        var trimmedContentType = Truncate(detectedContentType, ContentTypeMaxLength);
        var trimmedFormat = Truncate(detectedFormat, FormatMaxLength);

        if (existing is null)
        {
            _db.DerivativeDiagnostics.Add(new DerivativeDiagnostic
            {
                Id = Guid.NewGuid(),
                FileItemId = fileItemId,
                Size = size,
                Status = status,
                ErrorCode = errorCode,
                Message = trimmedMessage,
                DetectedContentType = trimmedContentType,
                DetectedFormat = trimmedFormat,
                AttemptCount = attempt,
                FirstAttemptedAt = now,
                LastAttemptedAt = now,
                NextRetryAt = nextRetry,
                Backend = backend,
                GeneratorVersion = generatorVersion,
            });
            await _db.SaveChangesAsync(cancellationToken);
        }
        else
        {
            await _db.DerivativeDiagnostics
                .Where(d => d.Id == existing.Id)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(d => d.Status, status)
                    .SetProperty(d => d.ErrorCode, errorCode)
                    .SetProperty(d => d.Message, trimmedMessage)
                    .SetProperty(d => d.DetectedContentType, trimmedContentType)
                    .SetProperty(d => d.DetectedFormat, trimmedFormat)
                    .SetProperty(d => d.AttemptCount, attempt)
                    .SetProperty(d => d.LastAttemptedAt, now)
                    .SetProperty(d => d.NextRetryAt, nextRetry)
                    .SetProperty(d => d.Backend, backend)
                    .SetProperty(d => d.GeneratorVersion, generatorVersion),
                    cancellationToken);
        }
    }

    // Drop the diagnostic for a now-resolved target. No-op when none exists.
    public Task ClearAsync(Guid fileItemId, string size, CancellationToken cancellationToken = default)
        => _db.DerivativeDiagnostics
            .Where(d => d.FileItemId == fileItemId && d.Size == size)
            .ExecuteDeleteAsync(cancellationToken);

    // Supersede any diagnostic whose derivative now exists (e.g. produced by the
    // lazy endpoint, which deliberately does not touch diagnostics). Keeps the
    // table — and therefore the aggregates — honest. Returns rows removed.
    public Task<int> PruneResolvedAsync(CancellationToken cancellationToken = default)
        => _db.DerivativeDiagnostics
            .Where(d => _db.FileThumbnails.Any(t => t.FileItemId == d.FileItemId && t.Size == d.Size))
            .ExecuteDeleteAsync(cancellationToken);

    // Map a code + permanence flag to a durable status. NotEligible /
    // MediaLibraryExcluded are skips, not failures.
    public static string MapStatus(string? errorCode, bool permanent) => errorCode switch
    {
        DerivativeErrorCodes.NotEligible
            or DerivativeErrorCodes.MediaLibraryExcluded => DerivativeStatuses.NotEligible,
        _ => permanent ? DerivativeStatuses.FailedPermanent : DerivativeStatuses.FailedTransient,
    };

    // ---- aggregation (Admin stats + CLI) ----------------------------------

    // Live diagnostics only: a row whose thumbnail now exists is ignored (it is
    // resolved, even if PruneResolvedAsync has not run yet).
    private IQueryable<DerivativeDiagnostic> Live() => _db.DerivativeDiagnostics
        .AsNoTracking()
        .Where(d => !_db.FileThumbnails.Any(t => t.FileItemId == d.FileItemId && t.Size == d.Size));

    public async Task<DerivativeDiagnosticSummary> SummariseAsync(CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;

        var byStatus = await Live()
            .GroupBy(d => new { d.Size, d.Status })
            .Select(g => new { g.Key.Size, g.Key.Status, Count = g.Count(), Last = (DateTime?)g.Max(d => d.LastAttemptedAt) })
            .ToListAsync(cancellationToken);

        var retryableNow = await Live()
            .Where(d => d.Status == DerivativeStatuses.FailedTransient
                && (d.NextRetryAt == null || d.NextRetryAt <= now))
            .GroupBy(d => d.Size)
            .Select(g => new { Size = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        var byCode = await Live()
            .Where(d => d.ErrorCode != null)
            .GroupBy(d => new { d.Size, d.ErrorCode })
            .Select(g => new { g.Key.Size, g.Key.ErrorCode, Count = g.Count() })
            .ToListAsync(cancellationToken);

        var byFormat = await Live()
            .Where(d => d.DetectedContentType != null
                && (d.Status == DerivativeStatuses.FailedPermanent
                    || d.Status == DerivativeStatuses.FailedTransient
                    || d.Status == DerivativeStatuses.NotEligible))
            .GroupBy(d => new { d.Size, d.DetectedContentType })
            .Select(g => new { g.Key.Size, g.Key.DetectedContentType, Count = g.Count() })
            .ToListAsync(cancellationToken);

        var sizes = byStatus.Select(x => x.Size)
            .Concat(byCode.Select(x => x.Size))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        int CountOf(string size, string status) =>
            byStatus.Where(x => x.Size == size && x.Status == status).Sum(x => x.Count);

        var perSize = sizes.Select(size =>
        {
            var failureLast = byStatus
                .Where(x => x.Size == size
                    && (x.Status == DerivativeStatuses.FailedPermanent
                        || x.Status == DerivativeStatuses.FailedTransient))
                .Select(x => x.Last)
                .Where(t => t != null)
                .DefaultIfEmpty(null)
                .Max();

            return new DerivativeDiagnosticSizeSummary(
                Size: size,
                Total: byStatus.Where(x => x.Size == size).Sum(x => x.Count),
                Pending: CountOf(size, DerivativeStatuses.Pending),
                Skipped: CountOf(size, DerivativeStatuses.Skipped),
                NotEligible: CountOf(size, DerivativeStatuses.NotEligible),
                FailedTransient: CountOf(size, DerivativeStatuses.FailedTransient),
                FailedPermanent: CountOf(size, DerivativeStatuses.FailedPermanent),
                RetryableNow: retryableNow.Where(x => x.Size == size).Sum(x => x.Count),
                LastFailureAt: failureLast,
                ByErrorCode: byCode
                    .Where(x => x.Size == size)
                    .OrderByDescending(x => x.Count)
                    .Select(x => new DerivativeCodeCount(x.ErrorCode!, x.Count))
                    .ToList(),
                TopFormats: byFormat
                    .Where(x => x.Size == size)
                    .OrderByDescending(x => x.Count)
                    .Take(5)
                    .Select(x => new DerivativeFormatCount(x.DetectedContentType!, x.Count))
                    .ToList());
        }).ToList();

        var lastFailureAt = perSize
            .Select(s => s.LastFailureAt)
            .Where(t => t != null)
            .DefaultIfEmpty(null)
            .Max();

        return new DerivativeDiagnosticSummary(perSize, lastFailureAt);
    }

    private static TimeSpan Backoff(int attempt)
    {
        var exponent = Math.Min(Math.Max(attempt - 1, 0), 10);
        var span = TimeSpan.FromMinutes(15.0 * Math.Pow(2, exponent));
        return span > MaxBackoff ? MaxBackoff : span;
    }

    private static string? Truncate(string? value, int max)
    {
        if (value is null)
        {
            return null;
        }
        return value.Length <= max ? value : value[..max];
    }
}

// Neutral aggregate shapes (no ids, paths, keys, or raw metadata). Consumed by
// StorageStatsService (mapped into the admin DTO) and the `media derivatives
// failures` CLI.
public sealed record DerivativeDiagnosticSummary(
    IReadOnlyList<DerivativeDiagnosticSizeSummary> Sizes,
    DateTime? LastFailureAt);

public sealed record DerivativeDiagnosticSizeSummary(
    string Size,
    int Total,
    int Pending,
    int Skipped,
    int NotEligible,
    int FailedTransient,
    int FailedPermanent,
    // Transient rows currently due for an automatic retry (NextRetryAt elapsed).
    int RetryableNow,
    DateTime? LastFailureAt,
    IReadOnlyList<DerivativeCodeCount> ByErrorCode,
    IReadOnlyList<DerivativeFormatCount> TopFormats);

public sealed record DerivativeCodeCount(string ErrorCode, int Count);

public sealed record DerivativeFormatCount(string DetectedContentType, int Count);
