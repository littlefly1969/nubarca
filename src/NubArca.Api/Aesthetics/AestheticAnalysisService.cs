using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NubArca.Api.Aesthetics.Sidecar;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Files;
using NubArca.Api.Jobs;
using NubArca.Api.Storage;
using SixLabors.ImageSharp;

namespace NubArca.Api.Aesthetics;

public sealed class AestheticAnalysisService : IAestheticAnalysisService
{
    private readonly AppDbContext _db;
    private readonly IJobQueue _jobs;
    private readonly IBlobService _blobs;
    private readonly IAestheticModelClient _client;
    private readonly TimeProvider _clock;
    private readonly ImageProcessingOptions _imageOptions;
    private readonly AestheticsOptions _options;
    private readonly ILogger<AestheticAnalysisService> _logger;

    public AestheticAnalysisService(
        AppDbContext db,
        IJobQueue jobs,
        IBlobService blobs,
        IAestheticModelClient client,
        TimeProvider clock,
        ILogger<AestheticAnalysisService> logger,
        IOptions<ImageProcessingOptions>? imageOptions = null,
        IOptions<AestheticsOptions>? options = null)
    {
        _db = db;
        _jobs = jobs;
        _blobs = blobs;
        _client = client;
        _clock = clock;
        _logger = logger;
        _imageOptions = imageOptions?.Value ?? new ImageProcessingOptions();
        _options = options?.Value ?? new AestheticsOptions();
    }

    public async Task<AestheticAnalysisBatchResultDto> RequestAnalysisAsync(
        Guid ownerUserId, IReadOnlyList<Guid> itemIds, IReadOnlyList<string>? capabilities,
        CancellationToken cancellationToken = default)
    {
        var enqueued = new List<AestheticAnalysisEnqueuedDto>();
        var skipped = new List<AestheticAnalysisSkippedDto>();

        // De-dup + cap the requested id list defensively.
        var ids = itemIds.Distinct().ToList();
        if (ids.Count > _options.MaximumBatchItems)
        {
            foreach (var over in ids.Skip(_options.MaximumBatchItems))
            {
                skipped.Add(new AestheticAnalysisSkippedDto(over, "batch_limit_exceeded"));
            }
            ids = ids.Take(_options.MaximumBatchItems).ToList();
        }

        // Feature master switch: controlled unavailable, create NOTHING.
        if (!_options.Enabled)
        {
            foreach (var id in ids)
            {
                skipped.Add(new AestheticAnalysisSkippedDto(id, AestheticErrorCodes.FeatureDisabled));
            }
            return new AestheticAnalysisBatchResultDto(enqueued, skipped);
        }

        // Resolve + gate-filter capabilities ONCE for the whole batch.
        var requested = (capabilities is { Count: > 0 })
            ? capabilities
            : AestheticLabService.SplitCsv(_options.DefaultCapabilities);
        var allowed = _options.FilterAllowed(requested);
        if (allowed.Count == 0)
        {
            foreach (var id in ids)
            {
                skipped.Add(new AestheticAnalysisSkippedDto(id, AestheticErrorCodes.CapabilityDisabled));
            }
            return new AestheticAnalysisBatchResultDto(enqueued, skipped);
        }
        var capabilityCsv = NormalizeCsv(allowed);

        foreach (var itemId in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var itemExists = await _db.AestheticLabItems.AsNoTracking()
                .AnyAsync(i => i.Id == itemId && i.OwnerUserId == ownerUserId, cancellationToken);
            if (!itemExists)
            {
                skipped.Add(new AestheticAnalysisSkippedDto(itemId, AestheticErrorCodes.ItemNotFound));
                continue;
            }

            var result = await EnqueueOneAsync(ownerUserId, itemId, allowed, capabilityCsv, cancellationToken);
            if (result.Enqueued is not null)
            {
                enqueued.Add(result.Enqueued);
            }
            else if (result.SkipReason is not null)
            {
                skipped.Add(new AestheticAnalysisSkippedDto(itemId, result.SkipReason));
            }
        }

        return new AestheticAnalysisBatchResultDto(enqueued, skipped);
    }

    private sealed record EnqueueOutcome(AestheticAnalysisEnqueuedDto? Enqueued, string? SkipReason);

    private async Task<EnqueueOutcome> EnqueueOneAsync(
        Guid ownerUserId, Guid itemId, IReadOnlyList<string> capabilities, string capabilityCsv,
        CancellationToken cancellationToken)
    {
        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken);

            // Collapse a duplicate LIVE run for the same (item, profile,
            // capabilities). A completed run is NOT reused — a re-request makes a
            // new historical run.
            var live = await _db.AestheticAnalysisRuns
                .Where(r => r.AestheticLabItemId == itemId && r.OwnerUserId == ownerUserId
                    && r.ProfileKey == _options.ProfileKey
                    && r.RequestedCapabilities == capabilityCsv
                    && (r.Status == AestheticRunStatuses.Queued || r.Status == AestheticRunStatuses.Running))
                .OrderByDescending(r => r.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken);

            AestheticAnalysisRun run;
            if (live is not null)
            {
                run = live;
            }
            else
            {
                var now = _clock.GetUtcNow().UtcDateTime;
                run = new AestheticAnalysisRun
                {
                    Id = Guid.NewGuid(),
                    OwnerUserId = ownerUserId,
                    AestheticLabItemId = itemId,
                    ProfileKey = _options.ProfileKey,
                    PreprocessingProfileKey = _options.PreprocessingProfileKey,
                    RequestedCapabilities = capabilityCsv,
                    CompletedCapabilities = string.Empty,
                    Status = AestheticRunStatuses.Queued,
                    CreatedAt = now,
                };
                _db.AestheticAnalysisRuns.Add(run);
                await _db.SaveChangesAsync(cancellationToken);
            }

            // ALWAYS (re)enqueue — the per-run idempotency key dedups a still-live
            // background job; it re-drives a queued run whose prior background job
            // was lost. Inside the transaction so run + job commit atomically.
            var job = await _jobs.EnqueueAsync(
                JobTypes.AestheticsAnalyze,
                new AestheticAnalysisJobPayload(run.Id),
                idempotencyKey: $"aesthetics:analyze:{run.Id:N}",
                cancellationToken: cancellationToken);

            if (run.BackgroundJobId != job.Id)
            {
                run.BackgroundJobId = job.Id;
                await _db.SaveChangesAsync(cancellationToken);
            }

            await tx.CommitAsync(cancellationToken);
            return new EnqueueOutcome(new AestheticAnalysisEnqueuedDto(itemId, run.Id, run.Status), null);
        });
    }

    public async Task<AestheticRunDto?> GetRunAsync(
        Guid ownerUserId, Guid runId, CancellationToken cancellationToken = default)
    {
        var run = await _db.AestheticAnalysisRuns.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == runId && r.OwnerUserId == ownerUserId, cancellationToken);
        return run is null ? null : await LoadRunDtoAsync(run, cancellationToken);
    }

    public async Task<bool> CancelRunAsync(
        Guid ownerUserId, Guid runId, CancellationToken cancellationToken = default)
    {
        var run = await _db.AestheticAnalysisRuns
            .FirstOrDefaultAsync(r => r.Id == runId && r.OwnerUserId == ownerUserId, cancellationToken);
        if (run is null || AestheticRunStatuses.IsTerminal(run.Status))
        {
            return false;
        }

        // Best-effort cancel the background job; the handler observes the flag and
        // stops, then marks the run cancelled. Also flip a QUEUED run immediately.
        if (run.BackgroundJobId is Guid jobId)
        {
            await _jobs.RequestCancellationAsync(jobId, cancellationToken);
        }
        if (run.Status == AestheticRunStatuses.Queued)
        {
            var now = _clock.GetUtcNow().UtcDateTime;
            run.Status = AestheticRunStatuses.Cancelled;
            run.CompletedAt = now;
            await _db.SaveChangesAsync(cancellationToken);
        }
        return true;
    }

    public async Task<AestheticRunDto?> RetryRunAsync(
        Guid ownerUserId, Guid runId, CancellationToken cancellationToken = default)
    {
        var source = await _db.AestheticAnalysisRuns.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == runId && r.OwnerUserId == ownerUserId, cancellationToken);
        if (source is null)
        {
            return null;
        }
        // Only failed/cancelled runs are retryable; a live/succeeded run is not.
        if (source.Status is not (AestheticRunStatuses.Failed or AestheticRunStatuses.Cancelled))
        {
            return null;
        }
        if (!_options.Enabled)
        {
            return null;
        }

        // Re-filter the source capabilities through the current gates.
        var allowed = _options.FilterAllowed(AestheticLabService.SplitCsv(source.RequestedCapabilities));
        if (allowed.Count == 0)
        {
            return null;
        }
        var capabilityCsv = NormalizeCsv(allowed);

        var outcome = await EnqueueOneAsync(ownerUserId, source.AestheticLabItemId, allowed, capabilityCsv, cancellationToken);
        if (outcome.Enqueued is null)
        {
            return null;
        }
        return await GetRunAsync(ownerUserId, outcome.Enqueued.RunId, cancellationToken);
    }

    // ---- worker execution ---------------------------------------------------

    public async Task AnalyzeAsync(Guid runId, JobContext context, CancellationToken cancellationToken = default)
    {
        var run = await _db.AestheticAnalysisRuns.FirstOrDefaultAsync(r => r.Id == runId, cancellationToken);
        if (run is null)
        {
            // The run (and possibly its item) was removed — safe no-op.
            return;
        }
        if (AestheticRunStatuses.IsTerminal(run.Status))
        {
            return; // already processed
        }

        var item = await _db.AestheticLabItems
            .FirstOrDefaultAsync(i => i.Id == run.AestheticLabItemId && i.OwnerUserId == run.OwnerUserId, cancellationToken);
        var now = _clock.GetUtcNow().UtcDateTime;

        if (item is null)
        {
            await FailAsync(run, AestheticErrorCodes.ItemNotFound);
            return;
        }

        if (context.IsCancellationRequested)
        {
            await MarkCancelledAsync(run);
            throw new OperationCanceledException();
        }

        run.Status = AestheticRunStatuses.Running;
        run.StartedAt = now;
        await _db.SaveChangesAsync(cancellationToken);

        try
        {
            if (!_options.Enabled)
            {
                await FailAsync(run, AestheticErrorCodes.FeatureDisabled);
                return;
            }
            var capabilities = _options.FilterAllowed(AestheticLabService.SplitCsv(run.RequestedCapabilities));
            if (capabilities.Count == 0)
            {
                await FailAsync(run, AestheticErrorCodes.CapabilityDisabled);
                return;
            }
            if (!_client.IsConfigured)
            {
                // Environment/config state — NOT a permanent content skip/failure.
                await FailAsync(run, AestheticErrorCodes.ModelUnavailable);
                return;
            }

            // Bounded, controlled image input: validate BEFORE inference so a
            // decompression bomb / unsupported format fails without hitting the
            // model. We send the IMMUTABLE original bytes; the sidecar owns the
            // model-specific preprocessing (official-v1 = the checkpoint's own).
            byte[] bytes;
            await using (var stream = await _blobs.OpenContentAsync(item.BlobObjectId, cancellationToken))
            {
                using var buffer = new MemoryStream();
                await stream.CopyToAsync(buffer, cancellationToken);
                bytes = buffer.ToArray();
            }
            ImageInfo? info;
            try
            {
                info = Image.Identify(bytes);
            }
            catch (Exception)
            {
                await FailAsync(run, AestheticErrorCodes.UnsupportedImage);
                return;
            }
            if (info is null
                || info.Width <= 0 || info.Height <= 0
                || info.Width > _imageOptions.MaxWidth
                || info.Height > _imageOptions.MaxHeight
                || (long)info.Width * info.Height > _imageOptions.MaxPixels)
            {
                await FailAsync(run, AestheticErrorCodes.UnsupportedImage);
                return;
            }

            var request = new AestheticSidecarRequest(
                AestheticSidecarContract.Version,
                _options.ProfileKey,
                capabilities,
                "it",
                run.PreprocessingProfileKey);

            var response = await _client.AnalyzeAsync(request, bytes, item.ContentType, cancellationToken);

            var validation = AestheticSidecarResponseValidator.Validate(response, request);
            if (!validation.Ok)
            {
                await FailAsync(run, validation.ErrorCode ?? AestheticErrorCodes.InvalidModelOutput);
                return;
            }

            await PersistSuccessAsync(run, response, validation, cancellationToken);
        }
        catch (OperationCanceledException) when (context.IsCancellationRequested || cancellationToken.IsCancellationRequested)
        {
            await MarkCancelledAsync(run);
            throw; // processor marks the background job cancelled (not failed)
        }
        catch (AestheticSidecarException ex)
        {
            _logger.LogWarning(ex, "Aesthetic analysis sidecar failure for run {RunId} ({Code}).", run.Id, ex.Code);
            await FailAsync(run, ex.Code);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Aesthetic analysis failed for run {RunId}.", run.Id);
            await FailAsync(run, AestheticErrorCodes.AnalysisFailed);
        }
    }

    private async Task PersistSuccessAsync(
        AestheticAnalysisRun run, AestheticSidecarResponse response,
        AestheticSidecarResponseValidator.ValidationResult validation,
        CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var strategy = _db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken);

            var tracked = await _db.AestheticAnalysisRuns.FirstAsync(r => r.Id == run.Id, cancellationToken);

            foreach (var m in validation.Metrics)
            {
                _db.AestheticMetrics.Add(new AestheticMetric
                {
                    Id = Guid.NewGuid(),
                    RunId = tracked.Id,
                    MetricKey = m.Key,
                    MetricGroup = m.Group,
                    NumericValue = m.Value,
                    ScaleMin = m.ScaleMin,
                    ScaleMax = m.ScaleMax,
                    Confidence = m.Confidence,
                    MetricVersion = m.Version,
                    CreatedAt = now,
                });
            }
            foreach (var t in validation.Texts)
            {
                _db.AestheticTextResults.Add(new AestheticTextResult
                {
                    Id = Guid.NewGuid(),
                    RunId = tracked.Id,
                    TextKind = t.Kind,
                    Language = t.Language,
                    Text = t.Text,
                    PromptTemplateVersion = t.PromptTemplateVersion,
                    CreatedAt = now,
                });
            }

            tracked.Status = AestheticRunStatuses.Succeeded;
            tracked.CompletedAt = now;
            tracked.DurationMs = response.DurationMs > 0
                ? response.DurationMs
                : (tracked.StartedAt is DateTime s ? (long)(now - s).TotalMilliseconds : null);
            tracked.CompletedCapabilities = NormalizeCsv(validation.CompletedCapabilities);
            tracked.ModelName = Truncate(response.ModelName, 128);
            tracked.ModelRevision = Truncate(response.ModelRevision, 128);
            tracked.RuntimeName = Truncate(response.RuntimeName, 64);
            tracked.RuntimeVersion = Truncate(response.RuntimeVersion, 32);
            tracked.WarningsJson = validation.Warnings.Count > 0
                ? JsonSerializer.Serialize(validation.Warnings)
                : null;
            // Bounded, validated raw response kept as INTERNAL provenance only.
            tracked.RawOutputJson = SerializeRawProvenance(response);

            await _db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
        });
    }

    private async Task FailAsync(AestheticAnalysisRun run, string errorCode)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var tracked = await _db.AestheticAnalysisRuns.FirstOrDefaultAsync(r => r.Id == run.Id);
        if (tracked is null || AestheticRunStatuses.IsTerminal(tracked.Status))
        {
            return;
        }
        tracked.Status = AestheticRunStatuses.Failed;
        tracked.ErrorCode = errorCode;
        tracked.CompletedAt = now;
        if (tracked.StartedAt is DateTime s)
        {
            tracked.DurationMs = (long)(now - s).TotalMilliseconds;
        }
        await _db.SaveChangesAsync(CancellationToken.None);
    }

    private async Task MarkCancelledAsync(AestheticAnalysisRun run)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var tracked = await _db.AestheticAnalysisRuns.FirstOrDefaultAsync(r => r.Id == run.Id);
        if (tracked is null || AestheticRunStatuses.IsTerminal(tracked.Status))
        {
            return;
        }
        tracked.Status = AestheticRunStatuses.Cancelled;
        tracked.CompletedAt = now;
        await _db.SaveChangesAsync(CancellationToken.None);
    }

    private async Task<AestheticRunDto> LoadRunDtoAsync(AestheticAnalysisRun run, CancellationToken cancellationToken)
    {
        var metrics = await _db.AestheticMetrics.AsNoTracking()
            .Where(m => m.RunId == run.Id)
            .OrderBy(m => m.MetricKey)
            .Select(m => new AestheticMetricDto(m.MetricKey, m.MetricGroup, m.NumericValue, m.ScaleMin, m.ScaleMax, m.Confidence, m.MetricVersion))
            .ToListAsync(cancellationToken);
        var texts = await _db.AestheticTextResults.AsNoTracking()
            .Where(t => t.RunId == run.Id)
            .OrderBy(t => t.TextKind)
            .Select(t => new AestheticTextDto(t.TextKind, t.Language, t.Text, t.PromptTemplateVersion))
            .ToListAsync(cancellationToken);
        return new AestheticRunDto(
            run.Id, run.Status, run.ProfileKey, run.ModelName, run.ModelRevision,
            run.RuntimeName, run.RuntimeVersion, run.PreprocessingProfileKey,
            AestheticLabService.SplitCsv(run.RequestedCapabilities),
            AestheticLabService.SplitCsv(run.CompletedCapabilities),
            run.CreatedAt, run.StartedAt, run.CompletedAt, run.DurationMs, run.ErrorCode,
            DeserializeWarnings(run.WarningsJson), metrics, texts);
    }

    private static string NormalizeCsv(IEnumerable<string> values) =>
        string.Join(',', values.Distinct().OrderBy(v => v, StringComparer.Ordinal));

    private static string? Truncate(string? s, int max) =>
        string.IsNullOrEmpty(s) ? s : (s.Length > max ? s[..max] : s);

    // The bounded, validated provenance blob. We re-serialize the response's SAFE
    // structural fields (never a stray large field) so RawOutputJson stays small.
    private static string SerializeRawProvenance(AestheticSidecarResponse r)
    {
        var provenance = new
        {
            contractVersion = r.ContractVersion,
            profileKey = r.ProfileKey,
            modelName = r.ModelName,
            modelRevision = r.ModelRevision,
            runtimeName = r.RuntimeName,
            runtimeVersion = r.RuntimeVersion,
            preprocessingProfileKey = r.PreprocessingProfileKey,
            completedCapabilities = r.CompletedCapabilities,
            durationMs = r.DurationMs,
            metrics = r.Metrics.Select(m => new { m.Key, m.Value, m.ScaleMin, m.ScaleMax, m.Confidence, m.Version }),
        };
        var json = JsonSerializer.Serialize(provenance);
        return json.Length > AestheticSidecarContract.MaxRawResponseBytes
            ? json[..AestheticSidecarContract.MaxRawResponseBytes]
            : json;
    }

    private static IReadOnlyList<string> DeserializeWarnings(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<string>();
        }
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }
}
