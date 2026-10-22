using NubArca.Api.Ai.DocumentVisual;
using NubArca.Api.Ai.Documents.Sources;
using NubArca.Api.Ai.TextEmbeddings;
using NubArca.Api.Data;
using NubArca.Api.Domain.Ai;

namespace NubArca.Api.Ai.Documents.Indexing;

/// What a run has resolved once, up front, for every source it touches.
///
/// `Embedding` is null when the embed phase does not run in this job (the
/// capability flag is off); an unavailable resolution is carried as such, so
/// the phase records an environment state rather than a failure. `Visual` is
/// null when the visual capability is off or not ready.
public sealed record DocumentIndexRunSettings(
    TextEmbeddingResolution? Embedding,
    DocumentVisualProfileResolution? Visual,
    bool RetryFailed,
    int MaxAttempts,
    Func<bool> ShouldYield);

/// How one source's pass ended.
public sealed record DocumentSourceRunResult(bool Yielded, bool DidWork, string? Outcome)
{
    public static readonly DocumentSourceRunResult Nothing = new(false, false, null);
}

/// Runs the phases of ONE source — extract → chunk → embed → visual — and
/// records each one's state.
///
/// The phases are the job's unit of resumability: a phase that completed is
/// recorded before the next one starts, and a slice that has to yield between
/// phases (or between chunks inside the embed phase) is resumed by the next
/// slice from exactly there, because completed work is either recorded or
/// short-circuits on its own.
///
/// The classification rules, which are the reason this class exists:
///  - a permanent CONTENT verdict is `skipped`, and is not retried until the
///    source's revision changes;
///  - a retryable failure is `failed` and counts an attempt; past the attempt
///    budget, automatic runs leave it alone until the content changes or an
///    operator asks with `--retry-failed`;
///  - a model, engine or renderer that is not available is `pending` with a
///    reason — an environment state, never charged to the source;
///  - cancellation puts a running phase back to `pending` and records nothing
///    permanent.
public sealed class DocumentSourceIndexProcessor
{
    private readonly AppDbContext _db;
    private readonly OwnerDocumentIndexer _indexer;
    private readonly OwnerDocumentVisualIndexer _visual;
    private readonly DocumentSourceStateStore _states;
    private readonly DocumentIndexingGate _gate;
    private readonly ILogger<DocumentSourceIndexProcessor> _log;

    public DocumentSourceIndexProcessor(
        AppDbContext db,
        OwnerDocumentIndexer indexer,
        OwnerDocumentVisualIndexer visual,
        DocumentSourceStateStore states,
        DocumentIndexingGate gate,
        ILogger<DocumentSourceIndexProcessor> log)
    {
        _db = db;
        _indexer = indexer;
        _visual = visual;
        _states = states;
        _gate = gate;
        _log = log;
    }

    public async Task<DocumentSourceRunResult> ProcessAsync(
        IOwnerDocumentSource source, Guid ownerUserId, DocumentSourceCandidate candidate,
        DocumentIndexRunSettings settings, CancellationToken cancellationToken)
    {
        var kind = source.Kind;
        var id = candidate.SourceId;
        var revision = candidate.RevisionHash;
        var states = await _states.GetAsync(kind, id, cancellationToken);

        // A PERMANENT VERDICT ABOUT THESE BYTES IS NOT RE-READ. The same bytes
        // earn the same refusal; a content change is a new revision and
        // re-opens the question on its own.
        if (IsSkippedAt(states, DocumentSourcePhases.Extract, revision)
            || IsSkippedAt(states, DocumentSourcePhases.Chunk, revision))
        {
            return DocumentSourceRunResult.Nothing;
        }

        if (!settings.RetryFailed && IsExhausted(states, DocumentSourcePhases.Extract, revision, settings.MaxAttempts))
        {
            return new DocumentSourceRunResult(false, false, "attempts-exhausted");
        }

        // ---- extract + chunk ------------------------------------------------
        var extractCurrent = IsCompletedAt(states, DocumentSourcePhases.Extract, revision);
        if (!extractCurrent)
        {
            await _states.RecordAsync(kind, id, ownerUserId, DocumentSourcePhases.Extract, revision,
                DocumentSourceStatuses.Running, cancellationToken: cancellationToken);
        }

        OwnerDocumentSourceOutcome outcome;
        try
        {
            outcome = await _indexer.IndexSourceAsync(source, ownerUserId, candidate, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            await AbandonAsync(kind, id, DocumentSourceReasons.Cancelled);
            throw;
        }
        catch (Exception ex)
        {
            return await FailUnexpectedAsync(kind, id, ownerUserId, DocumentSourcePhases.Extract, revision, ex);
        }

        switch (outcome.Kind)
        {
            case OwnerDocumentSourceOutcomeKind.NotEligible:
                // Gone, or changed under us. The live state wins; a later pass
                // (or the change signal) handles the new revision.
                await _states.ResetRunningAsync(kind, id, reason: null);
                return DocumentSourceRunResult.Nothing;

            case OwnerDocumentSourceOutcomeKind.Skipped:
                if (outcome.Phase == DocumentSourcePhases.Chunk)
                {
                    await RecordAsync(kind, id, ownerUserId, DocumentSourcePhases.Extract, revision,
                        DocumentSourceStatuses.Completed, null, outcome.ExtractionProfileId, null, cancellationToken);
                }
                await RecordAsync(kind, id, ownerUserId, outcome.Phase, revision,
                    DocumentSourceStatuses.Skipped, outcome.Reason, outcome.ExtractionProfileId,
                    OwnerDocumentChunkFormat.Current, cancellationToken);
                return new DocumentSourceRunResult(false, true, "skipped");

            case OwnerDocumentSourceOutcomeKind.Failed:
                await RecordAsync(kind, id, ownerUserId, DocumentSourcePhases.Extract, revision,
                    DocumentSourceStatuses.Failed, outcome.Reason, null, null, cancellationToken);
                return new DocumentSourceRunResult(false, true, "failed");

            case OwnerDocumentSourceOutcomeKind.Unavailable:
                await RecordAsync(kind, id, ownerUserId, DocumentSourcePhases.Extract, revision,
                    DocumentSourceStatuses.Pending, outcome.Reason, null, null, cancellationToken);
                return new DocumentSourceRunResult(false, true, "unavailable");
        }

        await RecordAsync(kind, id, ownerUserId, DocumentSourcePhases.Extract, revision,
            DocumentSourceStatuses.Completed, null, outcome.ExtractionProfileId, null, cancellationToken);
        await RecordAsync(kind, id, ownerUserId, DocumentSourcePhases.Chunk, revision,
            DocumentSourceStatuses.Completed, null, outcome.ExtractionProfileId,
            OwnerDocumentChunkFormat.Current, cancellationToken);

        var didWork = outcome.Kind == OwnerDocumentSourceOutcomeKind.Indexed;
        if (didWork && settings.ShouldYield())
        {
            return new DocumentSourceRunResult(true, true, "yielded");
        }

        // ---- embed ----------------------------------------------------------
        if (settings.Embedding is { } embedding)
        {
            var embedResult = await EmbedAsync(
                kind, id, ownerUserId, revision, outcome.DocumentTextId!.Value, embedding, settings, states,
                cancellationToken);
            if (embedResult is not null) return embedResult;
        }

        // ---- visual (the `file` kind only) ----------------------------------
        if (settings.Visual is { IsAvailable: true } visual && kind == DocumentSourceKinds.File)
        {
            var visualResult = await VisualAsync(
                id, ownerUserId, revision, visual, settings, states, cancellationToken);
            if (visualResult is not null) return visualResult;
        }

        return new DocumentSourceRunResult(false, didWork, didWork ? "indexed" : "unchanged");
    }

    private async Task<DocumentSourceRunResult?> EmbedAsync(
        string kind, Guid id, Guid ownerUserId, string revision, Guid documentTextId,
        TextEmbeddingResolution embedding, DocumentIndexRunSettings settings,
        IReadOnlyDictionary<string, DocumentSourceState> states, CancellationToken cancellationToken)
    {
        if (!embedding.IsAvailable)
        {
            // ENVIRONMENT, NOT CONTENT. The profile is disabled, not installed
            // or its model files are missing — pending with the reason.
            await RecordAsync(kind, id, ownerUserId, DocumentSourcePhases.Embed, revision,
                DocumentSourceStatuses.Pending, embedding.Reason, null, null, cancellationToken);
            return null;
        }

        var profileId = embedding.Profile!.Id;
        if (!settings.RetryFailed
            && IsExhausted(states, DocumentSourcePhases.Embed, revision, settings.MaxAttempts))
        {
            return null;
        }

        OwnerDocumentEmbedOutcome result;
        try
        {
            using (await _gate.EnterModelAsync(cancellationToken))
            {
                result = await _indexer.EmbedDocumentAsync(
                    ownerUserId, documentTextId, embedding, settings.ShouldYield, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            await AbandonAsync(kind, id, DocumentSourceReasons.Cancelled);
            throw;
        }
        catch (Exception ex)
        {
            return await FailUnexpectedAsync(kind, id, ownerUserId, DocumentSourcePhases.Embed, revision, ex);
        }

        if (result.Stopped)
        {
            await RecordAsync(kind, id, ownerUserId, DocumentSourcePhases.Embed, revision,
                DocumentSourceStatuses.Pending, DocumentSourceReasons.Yielded, profileId, null, cancellationToken);
            return new DocumentSourceRunResult(true, true, "yielded");
        }

        if (!result.Completed)
        {
            await RecordAsync(kind, id, ownerUserId, DocumentSourcePhases.Embed, revision,
                DocumentSourceStatuses.Pending, result.Reason, profileId, null, cancellationToken);
            return null;
        }

        await RecordAsync(kind, id, ownerUserId, DocumentSourcePhases.Embed, revision,
            DocumentSourceStatuses.Completed, null, profileId, null, cancellationToken);

        if (result.EmbeddingsCreated > 0 && settings.ShouldYield())
        {
            return new DocumentSourceRunResult(true, true, "yielded");
        }
        return null;
    }

    private async Task<DocumentSourceRunResult?> VisualAsync(
        Guid id, Guid ownerUserId, string revision, DocumentVisualProfileResolution visual,
        DocumentIndexRunSettings settings, IReadOnlyDictionary<string, DocumentSourceState> states,
        CancellationToken cancellationToken)
    {
        const string kind = DocumentSourceKinds.File;
        var profileId = visual.Profile!.Id;

        if (IsSkippedAt(states, DocumentSourcePhases.Visual, revision)
            || (!settings.RetryFailed
                && IsExhausted(states, DocumentSourcePhases.Visual, revision, settings.MaxAttempts)))
        {
            return null;
        }

        (string Status, string? Reason) result;
        try
        {
            using (await _gate.EnterModelAsync(cancellationToken))
            {
                result = await _visual.IndexFileAsync(ownerUserId, id, visual, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            await AbandonAsync(kind, id, DocumentSourceReasons.Cancelled);
            throw;
        }
        catch (Exception ex)
        {
            return await FailUnexpectedAsync(kind, id, ownerUserId, DocumentSourcePhases.Visual, revision, ex);
        }

        await RecordAsync(kind, id, ownerUserId, DocumentSourcePhases.Visual, revision,
            result.Status, result.Reason, profileId, null, cancellationToken);
        return null;
    }

    private Task RecordAsync(
        string kind, Guid id, Guid ownerUserId, string phase, string revision, string status,
        string? reason, Guid? profileId, int? formatVersion, CancellationToken cancellationToken)
        => _states.RecordAsync(kind, id, ownerUserId, phase, revision, status, reason, profileId,
            formatVersion, cancellationToken);

    /// Cancellation: whatever the interrupted phase left tracked is discarded
    /// (never flushed by a later save), and its state goes back to pending.
    private async Task AbandonAsync(string kind, Guid id, string reason)
    {
        _db.ChangeTracker.Clear();
        await _states.ResetRunningAsync(kind, id, reason);
    }

    /// An exception nobody classified. Retryable, recorded as a token — the
    /// exception message can carry a path and is never stored or logged; the
    /// type name is enough to find the bug.
    private async Task<DocumentSourceRunResult> FailUnexpectedAsync(
        string kind, Guid id, Guid ownerUserId, string phase, string revision, Exception ex)
    {
        _db.ChangeTracker.Clear();
        _log.LogWarning(
            "documents index: phase={Phase} failed with {ExceptionType}", phase, ex.GetType().Name);
        await _states.RecordAsync(kind, id, ownerUserId, phase, revision,
            DocumentSourceStatuses.Failed, DocumentSourceReasons.UnexpectedError,
            cancellationToken: CancellationToken.None);
        return new DocumentSourceRunResult(false, true, "failed");
    }

    private static bool IsCompletedAt(
        IReadOnlyDictionary<string, DocumentSourceState> states, string phase, string revision)
        => states.TryGetValue(phase, out var s)
           && s.SourceRevision == revision
           && s.Status == DocumentSourceStatuses.Completed;

    private static bool IsSkippedAt(
        IReadOnlyDictionary<string, DocumentSourceState> states, string phase, string revision)
        => states.TryGetValue(phase, out var s)
           && s.SourceRevision == revision
           && s.Status == DocumentSourceStatuses.Skipped;

    private static bool IsExhausted(
        IReadOnlyDictionary<string, DocumentSourceState> states, string phase, string revision, int maxAttempts)
        => states.TryGetValue(phase, out var s)
           && s.SourceRevision == revision
           && s.Status == DocumentSourceStatuses.Failed
           && s.Attempts >= maxAttempts;
}
