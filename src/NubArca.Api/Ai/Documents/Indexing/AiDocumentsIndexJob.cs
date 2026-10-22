using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NubArca.Api.Access;
using NubArca.Api.Ai.Diagnostics;
using NubArca.Api.Ai.Resolution;
using NubArca.Api.Ai.DocumentVisual;
using NubArca.Api.Ai.Documents.Sources;
using NubArca.Api.Ai.TextEmbeddings;
using NubArca.Api.Data;
using NubArca.Api.Domain.Ai;
using NubArca.Api.Jobs;
using NubArca.Api.Rag;

namespace NubArca.Api.Ai.Documents.Indexing;

/// Payload of `ai.documents.index`. Exactly one scope:
///  - one source: `OwnerUserId` + `SourceKind` + `SourceId` (the change signal);
///  - one owner: `OwnerUserId` alone (a backfill, a folder restore, an import);
///  - every owner: `AllOwners`, still processed one owner at a time.
///
/// Ids only, and only ever written by the server. Never a name, a path or a
/// storage key; PayloadJson is never surfaced.
public sealed record DocumentIndexJobPayload(
    Guid? OwnerUserId = null,
    string? SourceKind = null,
    Guid? SourceId = null,
    bool AllOwners = false,
    bool RetryFailed = false,
    int? Limit = null);

/// Versioned, internal resume state. Counts and cursor ids only. An unknown or
/// older version starts fresh, which is safe: completed work short-circuits.
public sealed record DocumentIndexCheckpoint(
    int V,
    Guid? Owner,
    int KindIndex,
    Guid? SourceCursor,
    int Processed)
{
    public const int CurrentVersion = 1;

    public string Serialize() => JsonSerializer.Serialize(this);

    public static DocumentIndexCheckpoint? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var parsed = JsonSerializer.Deserialize<DocumentIndexCheckpoint>(json);
            return parsed is { V: CurrentVersion } ? parsed : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// `ai.documents.index` — automatic, resumable owner-document indexing.
///
/// Compute band, sliced and checkpointed like every other backfill: candidates
/// are KEYSET-paged per source kind (never loaded whole), the yield check runs
/// between sources and between phases (and between chunks while embedding), and
/// a continuation resumes from the checkpoint. A source in flight when the slice
/// yields is revisited, which is cheap: completed phases short-circuit.
///
/// Gating mirrors the other AI backfills — AI disabled, the extraction
/// capability off, or cancellation all no-op without touching a single row.
/// Embeddings additionally need `Ai:DocumentEmbeddingsEnabled` and the
/// `user-documents` domain's semantic profile; the visual phase needs
/// `Ai:DocumentVisual:Enabled` and its model. A model that is not available is
/// an environment state: at most one aggregate diagnostic, per-source `pending`
/// with a reason, never `failed` or `skipped`.
public sealed class AiDocumentsIndexJobHandler : IJobHandler
{
    private const int PageSize = 32;

    private readonly IOptions<AiOptions> _options;
    private readonly AppDbContext _db;
    private readonly OwnerDocumentSources _sources;
    private readonly DocumentSourceIndexProcessor _processor;
    private readonly TextEmbeddingResolver _embeddings;
    private readonly DocumentVisualProfileResolver _visual;
    private readonly IAiDiagnosticsWriter _diagnostics;
    private readonly DocumentIndexingGate _gate;
    private readonly IUserPermissionService _permissions;

    public AiDocumentsIndexJobHandler(
        IOptions<AiOptions> options,
        AppDbContext db,
        OwnerDocumentSources sources,
        DocumentSourceIndexProcessor processor,
        TextEmbeddingResolver embeddings,
        DocumentVisualProfileResolver visual,
        IAiDiagnosticsWriter diagnostics,
        DocumentIndexingGate gate,
        IUserPermissionService permissions)
    {
        _options = options;
        _db = db;
        _sources = sources;
        _processor = processor;
        _embeddings = embeddings;
        _visual = visual;
        _diagnostics = diagnostics;
        _gate = gate;
        _permissions = permissions;
    }

    public string JobType => JobTypes.AiDocumentsIndex;

    public async Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Deserialize<DocumentIndexJobPayload>(context.PayloadJson)
            ?? new DocumentIndexJobPayload();
        var options = _options.Value;

        if (context.IsCancellationRequested)
        {
            await NoOpAsync(context, "cancelled");
            return;
        }
        if (!options.Enabled)
        {
            await NoOpAsync(context, "ai-disabled");
            return;
        }
        if (!options.DocumentExtractionEnabled)
        {
            await NoOpAsync(context, "capability-disabled");
            return;
        }
        if (!payload.AllOwners && (payload.OwnerUserId is null || payload.OwnerUserId == Guid.Empty))
        {
            await NoOpAsync(context, "no-scope");
            return;
        }

        // ONE DOCUMENT JOB AT A TIME (by default) in this process. A job that
        // cannot get a slot hands its worker slot back rather than holding it.
        if (!await _gate.Jobs.WaitAsync(
                TimeSpan.FromSeconds(options.DocumentIndexing.EffectiveGateWaitSeconds), cancellationToken))
        {
            context.Log("documents index: another document job is running; re-queued.");
            context.RequestContinuation(JobYieldReasons.SliceBudget, context.Checkpoint);
            return;
        }

        try
        {
            await RunAsync(context, payload, options, cancellationToken);
        }
        finally
        {
            _gate.Jobs.Release();
        }
    }

    private async Task RunAsync(
        JobContext context, DocumentIndexJobPayload payload, AiOptions options, CancellationToken cancellationToken)
    {
        var checkpoint = DocumentIndexCheckpoint.TryParse(context.Checkpoint)
            ?? new DocumentIndexCheckpoint(
                DocumentIndexCheckpoint.CurrentVersion,
                payload.AllOwners ? null : payload.OwnerUserId, 0, null, 0);
        var processedThisSlice = 0;

        var settings = new DocumentIndexRunSettings(
            await ResolveEmbeddingAsync(options, cancellationToken),
            await ResolveVisualAsync(cancellationToken),
            payload.RetryFailed,
            options.DocumentIndexing.EffectiveMaxAttemptsPerSource,
            () => context.ShouldYield(processedThisSlice));

        // ---- one source -----------------------------------------------------
        if (payload.SourceId is { } sourceId && payload.OwnerUserId is { } sourceOwner)
        {
            if (!await PermittedAsync(sourceOwner, cancellationToken))
            {
                await NoOpAsync(context, "not-permitted");
                return;
            }

            var source = _sources.For(payload.SourceKind ?? DocumentSourceKinds.File);
            var candidate = source is null
                ? null
                : await source.GetCandidateAsync(sourceOwner, sourceId, cancellationToken);
            if (source is null || candidate is null)
            {
                await NoOpAsync(context, "not-a-candidate");
                return;
            }

            var result = await _processor.ProcessAsync(source, sourceOwner, candidate, settings, cancellationToken);
            await context.ReportProgressAsync(1, 1, result.Outcome ?? "unchanged", cancellationToken);
            if (result.Yielded)
            {
                context.RequestContinuation(YieldReason(context), checkpoint.Serialize());
            }
            return;
        }

        // ---- one owner, or every owner one at a time --------------------------
        var kinds = _sources.All.OrderBy(k => k.Kind, StringComparer.Ordinal).ToList();

        while (true)
        {
            if (checkpoint.Owner is null)
            {
                // Every owner: keyset over users. Owners with no documents cost
                // one empty candidate page each.
                var first = await NextOwnerAsync(null, cancellationToken);
                if (first is null) break;
                checkpoint = checkpoint with { Owner = first, KindIndex = 0, SourceCursor = null };
            }

            var owner = checkpoint.Owner.Value;

            // Only owners who hold document-search.access are indexed. It is
            // re-read per owner, so a revoked owner is skipped from the next
            // slice on; what was already indexed stays until they are allowed
            // again or their documents change.
            var permitted = await PermittedAsync(owner, cancellationToken);
            while (permitted && checkpoint.KindIndex < kinds.Count)
            {
                var source = kinds[checkpoint.KindIndex];
                var page = await source.ListCandidatesAsync(owner, checkpoint.SourceCursor, PageSize, cancellationToken);
                if (page.Count == 0)
                {
                    checkpoint = checkpoint with { KindIndex = checkpoint.KindIndex + 1, SourceCursor = null };
                    continue;
                }

                foreach (var candidate in page)
                {
                    if (payload.Limit is int limit && checkpoint.Processed >= limit)
                    {
                        await FinishAsync(context, checkpoint);
                        return;
                    }

                    if (context.ShouldYield(processedThisSlice))
                    {
                        context.RequestContinuation(YieldReason(context), checkpoint.Serialize());
                        await context.ReportProgressAsync(checkpoint.Processed, null, "documents", cancellationToken);
                        return;
                    }

                    var result = await _processor.ProcessAsync(source, owner, candidate, settings, cancellationToken);
                    if (result.Yielded)
                    {
                        // The cursor stays BEFORE this source: the next slice
                        // revisits it and resumes from its first unfinished phase.
                        context.RequestContinuation(YieldReason(context), checkpoint.Serialize());
                        await context.ReportProgressAsync(checkpoint.Processed, null, "documents", cancellationToken);
                        return;
                    }

                    processedThisSlice++;
                    checkpoint = checkpoint with
                    {
                        SourceCursor = candidate.SourceId,
                        Processed = checkpoint.Processed + 1,
                    };
                    await context.ReportProgressAsync(checkpoint.Processed, null, "documents", cancellationToken);
                }
            }

            if (!payload.AllOwners) break;

            var next = await NextOwnerAsync(owner, cancellationToken);
            if (next is null) break;
            checkpoint = checkpoint with { Owner = next, KindIndex = 0, SourceCursor = null };
        }

        await FinishAsync(context, checkpoint);
    }

    private async Task<bool> PermittedAsync(Guid owner, CancellationToken cancellationToken) =>
        (await _permissions.GetCurrentAsync(owner, cancellationToken)).Has(Permissions.DocumentSearchAccess);

    private async Task<Guid?> NextOwnerAsync(Guid? after, CancellationToken cancellationToken)
    {
        var cursor = after ?? Guid.Empty;
        return await _db.Users.AsNoTracking()
            .Where(u => u.Id > cursor)
            .OrderBy(u => u.Id)
            .Select(u => (Guid?)u.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// The embed phase's resolution, or null when this job does not embed.
    private async Task<TextEmbeddingResolution?> ResolveEmbeddingAsync(
        AiOptions options, CancellationToken cancellationToken)
    {
        if (!options.DocumentEmbeddingsEnabled) return null;

        var resolution = await _embeddings.ResolveAsync(RagDomainKey.UserDocuments, cancellationToken);
        if (!resolution.IsAvailable)
        {
            // Environment/config state — at most ONE aggregate diagnostic per
            // slice, never a per-document failure.
            await _diagnostics.RecordProviderUnavailableAsync(
                AiCapabilities.DocumentEmbedding, profileId: null,
                resolution.Reason ?? AiUnavailableReasons.ProviderUnavailable, cancellationToken);
        }
        return resolution;
    }

    /// The visual phase's resolution, or null when the capability is off or not
    /// ready (nothing is recorded for it then).
    private async Task<DocumentVisualProfileResolution?> ResolveVisualAsync(CancellationToken cancellationToken)
    {
        var resolution = await _visual.ResolveAsync(cancellationToken);
        return resolution.IsAvailable ? resolution : null;
    }

    private static async Task FinishAsync(JobContext context, DocumentIndexCheckpoint checkpoint)
    {
        context.Log($"documents index: done, sources={checkpoint.Processed}.");
        await context.ReportProgressAsync(checkpoint.Processed, checkpoint.Processed, "done", context.CancellationToken);
    }

    private static string YieldReason(JobContext context)
        => context.HigherPriorityWaiting ? JobYieldReasons.HigherPriority : JobYieldReasons.SliceBudget;

    private static async Task NoOpAsync(JobContext context, string reason)
    {
        context.Log($"documents index: {reason} — nothing to do.");
        await context.ReportProgressAsync(0, 0, reason, context.CancellationToken);
    }
}
