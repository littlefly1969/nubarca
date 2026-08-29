using NubArca.Api.Rag;
using NubArca.Api.Rag.Domains;

namespace NubArca.Api.Ai.DocumentVisual;

/// One question, answered from one person's documents, by both signals.
///
/// Bounded TEXT evidence and two mode tokens. There is deliberately no field
/// for a visual unit, a page, a score or a file id: what leaves this pipeline is
/// what may reach a prompt, and nothing else needs to.
public sealed record OwnerDocumentRetrievalResult(
    IReadOnlyList<RagEvidence> Evidence,
    RagRetrievalOutcome Outcome,
    string TextMode,
    string VisualMode,
    int GlobalEvidence,
    int VisualExpandedEvidence,
    IReadOnlyList<Guid> VisualCandidateFileIds)
{
    public static OwnerDocumentRetrievalResult From(RagRetrievalResult text, string visualMode)
        => new(text.Evidence, text.Outcome, text.Mode, visualMode,
               text.Evidence.Count, 0, Array.Empty<Guid>());
}

/// THE RETRIEVAL HALF OF "ANSWER FROM MY DOCUMENTS", as one object.
///
///     global private text retrieval
///   + dense visual retrieval  ->  candidate files
///                             ->  the SAME text retrieval, scoped to them
///   = two ranked lists, fused by rank
///
/// Extracted from the Assistant service so that three callers measure the
/// identical thing: the Assistant, which turns the result into a prompt; the
/// evaluation harness, which reports what each signal contributed; and the
/// operator CLI. A benchmark that re-implements the pipeline it benchmarks
/// measures the re-implementation.
///
/// What it does NOT do is generate, decide trust, or know which model will be
/// called. It holds no model runtime and no owner beyond the one it is passed.
public sealed class OwnerDocumentRetrievalPipeline
{
    private static readonly RagDomainDefinition Domain = RagDomainRegistry.UserDocuments;

    private readonly IRagRetriever _knowledge;
    private readonly IOwnerDocumentVisualRetriever? _visual;

    public OwnerDocumentRetrievalPipeline(
        IRagRetriever knowledge, IOwnerDocumentVisualRetriever? visual)
    {
        _knowledge = knowledge;
        _visual = visual;
    }

    public async Task<OwnerDocumentRetrievalResult> RetrieveAsync(
        Guid ownerUserId,
        string question,
        int maxEvidence,
        int maxCharacters,
        bool useVisual = true,
        CancellationToken cancellationToken = default)
    {
        var global = await _knowledge.RetrieveAsync(
            new RagQuery(Domain.DomainKey, question, ownerUserId, maxEvidence, maxCharacters),
            cancellationToken);

        if (!useVisual || _visual is null)
        {
            return OwnerDocumentRetrievalResult.From(global, DocumentVisualModes.Unavailable);
        }

        // WHICH OF MY DOCUMENTS LOOKS LIKE THIS QUESTION.
        var visual = await _visual.RetrieveAsync(
            new DocumentVisualQuery(ownerUserId, question, MaxUnits: 60, MaxFiles: 8),
            cancellationToken);

        if (!visual.IsAvailable || visual.CandidateFileIds.Count == 0)
        {
            return OwnerDocumentRetrievalResult.From(global, visual.Reason ?? visual.Mode);
        }

        // AND WHAT DOES THAT DOCUMENT ACTUALLY SAY. The same retriever, the same
        // domain, the same owner — narrowed to files this owner's own eligible
        // visual index named moments ago, so the narrowing can only ever be a
        // subset of what the global pass was already allowed to see.
        var scoped = await _knowledge.RetrieveAsync(
            new RagQuery(Domain.DomainKey, question, ownerUserId, maxEvidence, maxCharacters)
            {
                AllowedFileItemIds = visual.CandidateFileIds,
            },
            cancellationToken);

        // A VISUALLY PERFECT PAGE WITH NOTHING USEFUL WRITTEN ON IT STOPS HERE.
        // The gate inside the retriever refused it, and there is deliberately no
        // branch saying "but the picture matched".
        var expanded = scoped.Outcome == RagRetrievalOutcome.Strong
            ? scoped.Evidence
            : Array.Empty<RagEvidence>();

        var fused = PrivateDocumentEvidenceFusion.Fuse(
            global.Evidence, expanded, maxEvidence, maxCharacters);

        var strong =
            (global.Outcome == RagRetrievalOutcome.Strong && global.Evidence.Count > 0)
            || expanded.Count > 0;

        return new OwnerDocumentRetrievalResult(
            fused,
            strong && fused.Count > 0
                ? RagRetrievalOutcome.Strong
                : global.Outcome == RagRetrievalOutcome.Unavailable
                    ? RagRetrievalOutcome.Unavailable
                    : RagRetrievalOutcome.None,
            global.Mode,
            visual.Mode,
            global.Evidence.Count,
            expanded.Count,
            visual.CandidateFileIds);
    }
}
