using NubArca.Api.Ai.DocumentVisual;
using NubArca.Api.Rag;
using NubArca.Api.Rag.Retrieval;

namespace NubArca.Api.Ai.Documents;

/// What a search found, and why it found nothing when it did.
public static class OwnerDocumentSearchStatuses
{
    /// At least one document cleared the evidence gate.
    public const string Ok = "ok";

    /// The library is searchable and nothing in it matched well enough.
    public const string NoEvidence = "no_evidence";

    /// Nothing of this owner's is indexed yet.
    public const string NotIndexed = "not_indexed";

    /// The corpus exists but cannot be searched (over the chunk ceiling, …).
    /// `Reason` carries the sanitized token.
    public const string Unavailable = "unavailable";
}

/// One search, as the SERVER states it. `SourceKinds` and `AllowedSourceIds`
/// are server-side narrowings: no HTTP body carries them, and both can only
/// remove candidates from the owner's eligible set — a forged id of another
/// owner, a deleted or a vaulted file is not in that set to begin with.
public sealed record OwnerDocumentSearchRequest(
    string Query,
    int? Limit = null,
    IReadOnlyCollection<string>? SourceKinds = null,
    IReadOnlyCollection<Guid>? AllowedSourceIds = null);

/// One passage of one document. Text is a bounded excerpt centred on the match.
public sealed record OwnerDocumentSearchPassage(
    int Rank,
    string Snippet,
    string? Section,
    int? Page,
    string? LocatorKind,
    int? LocatorIndex,
    string? LocatorLabel,
    int? Ordinal,
    int? LexicalRank,
    int? VectorRank);

/// One document, as good as its best passage. `SourceKind` is one of
/// DocumentSourceKinds; `SourceId` is the CALLER'S OWN id for the source — for
/// `file`, their FileItemId.
public sealed record OwnerDocumentSearchHit(
    string SourceKind,
    Guid SourceId,
    string Name,
    int Rank,
    IReadOnlyList<OwnerDocumentSearchPassage> Passages);

public sealed record OwnerDocumentSearchResult(
    string Status,
    string? Reason,
    string TextMode,
    string VisualMode,
    bool VisualUsed,
    IReadOnlyList<OwnerDocumentSearchHit> Documents)
{
    public static OwnerDocumentSearchResult Empty(string status, string? reason, string textMode, string visualMode)
        => new(status, reason, textMode, visualMode, false, Array.Empty<OwnerDocumentSearchHit>());
}

/// "FIND IT IN MY DOCUMENTS" — retrieval with provenance, and no model.
///
/// The same OwnerDocumentRetrievalPipeline the Assistant answers from — global
/// text (lexical, plus dense when semantic is on) and, when available, the
/// visual pass that scopes a second text pass — fused by rank. What differs is
/// the output: instead of a prompt, the evidence is grouped by source and
/// handed back with WHERE it was found.
///
/// THE SAME GATE. A search result is evidence that cleared the identical
/// evidence gate the Assistant requires before it may call a model; there is no
/// "show me the near misses" mode, because the near misses are what a cosine
/// returns for every question in a small library.
///
/// No generative model, no tool, no second retrieval round. The owner is the
/// caller's; the domain is the constant `user-documents` inside the pipeline.
public sealed class OwnerDocumentSearchService
{
    /// Fixed bounds. Evidence beyond what one results page can show costs a
    /// scan and buys nothing; the retriever's own candidate clamps still apply.
    public const int MaxDocuments = 25;
    public const int DefaultDocuments = 10;
    public const int MaxPassagesPerDocument = 3;
    public const int MaxEvidence = 40;
    public const int MaxEvidenceCharacters = 160_000;
    public const int MaxSnippetCharacters = 320;

    private readonly OwnerDocumentRetrievalPipeline _pipeline;
    private readonly ILogger<OwnerDocumentSearchService> _log;

    public OwnerDocumentSearchService(
        OwnerDocumentRetrievalPipeline pipeline, ILogger<OwnerDocumentSearchService> log)
    {
        _pipeline = pipeline;
        _log = log;
    }

    public async Task<OwnerDocumentSearchResult> SearchAsync(
        Guid ownerUserId, OwnerDocumentSearchRequest request, CancellationToken cancellationToken = default)
    {
        var text = (request.Query ?? string.Empty).Trim();
        if (ownerUserId == Guid.Empty)
        {
            return OwnerDocumentSearchResult.Empty(
                OwnerDocumentSearchStatuses.Unavailable, RagFailureReasons.OwnerRequired,
                RagRetrievalModes.Lexical, DocumentVisualModes.Unavailable);
        }

        // Narrowings that leave nothing are answered as "no evidence", never
        // widened. A kind that is not registered simply matches no branch.
        if (text.Length == 0
            || request.SourceKinds is { Count: 0 }
            || request.AllowedSourceIds is { Count: 0 })
        {
            return OwnerDocumentSearchResult.Empty(
                OwnerDocumentSearchStatuses.NoEvidence, null,
                RagRetrievalModes.Lexical, DocumentVisualModes.Unavailable);
        }

        var documents = Math.Clamp(request.Limit ?? DefaultDocuments, 1, MaxDocuments);

        var retrieval = await _pipeline.RetrieveAsync(
            ownerUserId, text, MaxEvidence, MaxEvidenceCharacters,
            cancellationToken: cancellationToken,
            allowedSourceIds: request.AllowedSourceIds,
            allowedSourceKinds: request.SourceKinds);

        // Outcome, modes and counts. Never the query, never a name, never an id.
        _log.LogInformation(
            "user-documents: search outcome={Outcome} mode={Mode} evidence={Count} visual={VisualMode} "
            + "visual_evidence={VisualCount}",
            retrieval.Outcome, retrieval.TextMode, retrieval.Evidence.Count,
            retrieval.VisualMode, retrieval.VisualExpandedEvidence);

        if (retrieval.Outcome == RagRetrievalOutcome.Unavailable)
        {
            var notIndexed = retrieval.TextReason is null or RagFailureReasons.IndexUnavailable;
            return OwnerDocumentSearchResult.Empty(
                notIndexed ? OwnerDocumentSearchStatuses.NotIndexed : OwnerDocumentSearchStatuses.Unavailable,
                notIndexed ? null : retrieval.TextReason,
                retrieval.TextMode, retrieval.VisualMode);
        }
        if (retrieval.Outcome != RagRetrievalOutcome.Strong || retrieval.Evidence.Count == 0)
        {
            return OwnerDocumentSearchResult.Empty(
                OwnerDocumentSearchStatuses.NoEvidence, null, retrieval.TextMode, retrieval.VisualMode);
        }

        var terms = RagQueryShape.For(text, expandAliases: false).AllTerms;
        var hits = Group(ownerUserId, retrieval.Evidence, terms, documents);

        return new OwnerDocumentSearchResult(
            hits.Count > 0 ? OwnerDocumentSearchStatuses.Ok : OwnerDocumentSearchStatuses.NoEvidence,
            null, retrieval.TextMode, retrieval.VisualMode,
            VisualUsed: retrieval.VisualExpandedEvidence > 0,
            hits);
    }

    /// Evidence, in fused order, folded into documents in order of their best
    /// passage. Evidence for any OTHER owner, or with no source, is dropped — it
    /// cannot happen through the eligibility join, and this is the last place
    /// it could be caught before it is on the wire.
    internal static List<OwnerDocumentSearchHit> Group(
        Guid ownerUserId,
        IReadOnlyList<RagEvidence> evidence,
        IReadOnlyList<string> terms,
        int maxDocuments)
    {
        var order = new List<(string Kind, Guid Id)>();
        var bySource = new Dictionary<(string Kind, Guid Id), (string Name, List<OwnerDocumentSearchPassage> Passages)>();

        var rank = 0;
        foreach (var item in evidence)
        {
            rank++;
            if (item.OwnerUserId != ownerUserId) continue;
            if (item.DocumentSourceId is not { } sourceId || sourceId == Guid.Empty) continue;
            if (string.IsNullOrEmpty(item.DocumentSourceKind)) continue;

            var key = (item.DocumentSourceKind, sourceId);
            if (!bySource.TryGetValue(key, out var entry))
            {
                if (order.Count >= maxDocuments) continue;
                entry = (item.Title, new List<OwnerDocumentSearchPassage>());
                bySource[key] = entry;
                order.Add(key);
            }
            if (entry.Passages.Count >= MaxPassagesPerDocument) continue;

            entry.Passages.Add(new OwnerDocumentSearchPassage(
                Rank: rank,
                Snippet: RagLexicalIndex.CenterOnMatch(item.Text, terms, MaxSnippetCharacters),
                Section: string.IsNullOrWhiteSpace(item.Section) ? null : item.Section,
                Page: item.Page,
                LocatorKind: item.LocatorKind,
                LocatorIndex: item.LocatorIndex,
                LocatorLabel: string.IsNullOrWhiteSpace(item.LocatorLabel) ? null : item.LocatorLabel,
                Ordinal: item.Ordinal,
                LexicalRank: item.LexicalRank,
                VectorRank: item.VectorRank));
        }

        // The REAL kind of each source, as the eligibility join stamped it.
        return order
            .Select((key, i) => new OwnerDocumentSearchHit(
                key.Kind, key.Id, bySource[key].Name, i + 1, bySource[key].Passages))
            .ToList();
    }
}
