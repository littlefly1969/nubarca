using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NubArca.Api.Ai.Documents.Sources;
using NubArca.Api.Data;
using NubArca.Api.Jobs;

namespace NubArca.Api.Ai.Documents.Indexing;

/// The change signal, recorded as an `ai.documents.index` job.
///
/// Nothing is enqueued while the feature is off — AI disabled (the default) or
/// the document-extraction capability off — and nothing for a source that is
/// not a document candidate (a photo upload costs one indexed lookup). The
/// upload never waits for extraction; it waits for one small insert at most.
public sealed class JobQueueDocumentSourceChangeSink : IDocumentSourceChangeSink
{
    private readonly IOptions<AiOptions> _options;
    private readonly OwnerDocumentSources _sources;
    private readonly IJobQueue _jobs;
    private readonly AppDbContext _db;
    private readonly TimeProvider _clock;
    private readonly ILogger<JobQueueDocumentSourceChangeSink> _log;

    public JobQueueDocumentSourceChangeSink(
        IOptions<AiOptions> options,
        OwnerDocumentSources sources,
        IJobQueue jobs,
        AppDbContext db,
        TimeProvider clock,
        ILogger<JobQueueDocumentSourceChangeSink> log)
    {
        _options = options;
        _sources = sources;
        _jobs = jobs;
        _db = db;
        _clock = clock;
        _log = log;
    }

    private bool FeatureEnabled => _options.Value is { Enabled: true, DocumentExtractionEnabled: true };

    public async Task SourceChangedAsync(
        string kind, Guid ownerUserId, Guid sourceId, CancellationToken cancellationToken = default)
    {
        if (!FeatureEnabled || ownerUserId == Guid.Empty) return;

        try
        {
            var source = _sources.For(kind);
            if (source is null) return;

            var candidate = await source.GetCandidateAsync(ownerUserId, sourceId, cancellationToken);
            if (candidate is null) return;

            // Keyed by REVISION as well as source: a job already running for an
            // older revision must not swallow the signal for a newer one.
            await _jobs.EnqueueAsync(
                JobTypes.AiDocumentsIndex,
                new DocumentIndexJobPayload(OwnerUserId: ownerUserId, SourceKind: kind, SourceId: sourceId),
                idempotencyKey: $"documents:index:{kind}:{sourceId:N}:{candidate.RevisionHash}",
                cancellationToken: cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Best effort: a later backfill catches the source up. Token only.
            _log.LogWarning("documents index: change signal not recorded ({ExceptionType})", ex.GetType().Name);
        }
    }

    public async Task OwnerSourcesChangedAsync(Guid ownerUserId, CancellationToken cancellationToken = default)
    {
        if (!FeatureEnabled || ownerUserId == Guid.Empty) return;

        try
        {
            // ONE QUEUED OWNER PASS IS ENOUGH; a RUNNING one is not, because its
            // cursor may already be past the sources that just changed. So the
            // check is for a queued job, and the key carries a stamp so a new
            // job can be queued behind a running one.
            var prefix = $"documents:index:owner:{ownerUserId:N}:";
            var queued = await _db.BackgroundJobs.AsNoTracking()
                .AnyAsync(j => j.Type == JobTypes.AiDocumentsIndex
                               && j.Status == JobStatuses.Queued
                               && j.IdempotencyKey != null
                               && j.IdempotencyKey.StartsWith(prefix),
                    cancellationToken);
            if (queued) return;

            await _jobs.EnqueueAsync(
                JobTypes.AiDocumentsIndex,
                new DocumentIndexJobPayload(OwnerUserId: ownerUserId),
                idempotencyKey: prefix + _clock.GetUtcNow().UtcTicks,
                cancellationToken: cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning("documents index: owner change signal not recorded ({ExceptionType})", ex.GetType().Name);
        }
    }
}
