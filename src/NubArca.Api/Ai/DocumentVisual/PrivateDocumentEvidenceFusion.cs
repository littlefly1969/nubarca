using NubArca.Api.Rag;
using NubArca.Api.Rag.Retrieval;

namespace NubArca.Api.Ai.DocumentVisual;

/// FUSING TWO RETRIEVALS OF THE SAME PERSON'S DOCUMENTS.
///
/// The private path now asks two questions. The first is the one it always
/// asked: rank this owner's whole corpus, lexically and semantically. The second
/// is new: take the files whose PAGES look like the question, and rank the text
/// inside just those. Both come back as ordered evidence, and both went through
/// the same eligibility join and the same evidence gate on the way.
///
/// RANKS, NEVER SCORES — the reason RrfFusion already states, doubled here. The
/// two lists carry fusion scores that are themselves RRF sums over different
/// candidate pools, so their magnitudes are not comparable even though they are
/// the same kind of number. Adding them would let a scoped pass over three
/// documents outweigh a global pass over three hundred, purely because a small
/// pool produces higher ranks. So position is all that survives.
///
/// AND THIS FUSES EVIDENCE, NOT CANDIDATES — deliberately, because it means the
/// gate ran first. `RagRetriever` applies `HasStrongEvidence` to each pass
/// before building its evidence, so a visually-found file whose text does not
/// actually answer the question contributes a `None` result and nothing to fuse.
/// A visual hit can therefore introduce a document; it cannot lower the bar for
/// what that document has to say.
public static class PrivateDocumentEvidenceFusion
{
    /// The two lists, interleaved by reciprocal rank and bounded by the caller's
    /// own evidence and character budgets.
    ///
    /// `maxCharacters` is re-applied because each list was trimmed against the
    /// full budget independently; a naive concatenation of the two would send
    /// twice the intended context to the model.
    public static IReadOnlyList<RagEvidence> Fuse(
        IReadOnlyList<RagEvidence> globalText,
        IReadOnlyList<RagEvidence> visualExpandedText,
        int maxEvidence,
        int maxCharacters)
    {
        if (maxEvidence <= 0 || maxCharacters <= 0) return Array.Empty<RagEvidence>();
        if (visualExpandedText.Count == 0) return Bounded(globalText, maxEvidence, maxCharacters);
        if (globalText.Count == 0) return Bounded(visualExpandedText, maxEvidence, maxCharacters);

        var merged = new Dictionary<string, Accumulator>(StringComparer.Ordinal);

        Accumulate(merged, globalText);
        Accumulate(merged, visualExpandedText);

        var fused = merged.Values
            .OrderByDescending(a => a.Score)
            // Ordinal chunk id as the tie-break. RRF values come from a small
            // set of ranks, so ties are constant, and two runs of one question
            // must order them identically.
            .ThenBy(a => a.Evidence.Id, StringComparer.Ordinal)
            .Select(a => a.Evidence)
            .ToList();

        return Bounded(fused, maxEvidence, maxCharacters);
    }

    private static void Accumulate(
        Dictionary<string, Accumulator> merged, IReadOnlyList<RagEvidence> list)
    {
        for (var i = 0; i < list.Count; i++)
        {
            var evidence = list[i];
            var rank = i + 1;
            if (merged.TryGetValue(evidence.Id, out var existing))
            {
                existing.Score += 1.0 / (RrfFusion.K + rank);
                continue;
            }

            merged[evidence.Id] = new Accumulator(evidence, 1.0 / (RrfFusion.K + rank));
        }
    }

    /// The caller's budgets, applied to the fused order.
    ///
    /// A chunk that does not FIT stops the list rather than being cut in half.
    /// The retrieval that produced it already centred its text on the match, and
    /// truncating again here would hand the model a sentence ending mid-clause
    /// and a citation implying the document said it that way.
    private static IReadOnlyList<RagEvidence> Bounded(
        IReadOnlyList<RagEvidence> evidence, int maxEvidence, int maxCharacters)
    {
        var kept = new List<RagEvidence>(Math.Min(evidence.Count, maxEvidence));
        var budget = maxCharacters;

        foreach (var item in evidence)
        {
            if (kept.Count >= maxEvidence) break;
            if (item.Text.Length > budget) break;
            budget -= item.Text.Length;
            kept.Add(item);
        }

        return kept;
    }

    private sealed class Accumulator(RagEvidence evidence, double score)
    {
        public RagEvidence Evidence { get; } = evidence;
        public double Score { get; set; } = score;
    }
}
