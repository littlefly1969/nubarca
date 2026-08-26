namespace NubArca.Api.Rag.Retrieval;

/// One candidate after fusion, carrying where each path put it.
public sealed record RagFusedCandidate(
    RagIndexedChunk Chunk,
    double FusionScore,
    int? LexicalRank,
    double LexicalScore,
    int? VectorRank,
    double VectorScore,
    int Rank);

/// Reciprocal Rank Fusion.
///
///     score(d) = Σ over paths  1 / (k + rank(d))
///
/// RANKS, not scores. BM25F produces an unbounded relevance number whose scale
/// depends on corpus statistics; cosine similarity produces a bounded number
/// whose useful range depends on the checkpoint. Adding or averaging them
/// requires a calibration nobody has measured yet, and the usual shortcut —
/// min-max normalizing each result set — makes the top score 1.0 whether the
/// best hit was excellent or merely the least bad. RRF needs no calibration,
/// is stable when one path returns nothing, and can be explained to a person
/// looking at `rag query` output.
///
/// `k = 60` is the constant from the original TREC work and the value every
/// implementation that has not measured its own uses. It damps the difference
/// between rank 1 and rank 2 enough that agreement between paths matters more
/// than either path's confidence — which is the property we want. It is NOT an
/// operator knob: exposing it would invite tuning against an anecdote.
public static class RrfFusion
{
    public const int K = 60;

    public static IReadOnlyList<RagFusedCandidate> Fuse(
        IReadOnlyList<RagLexicalHit> lexical,
        IReadOnlyList<RagVectorHit> vector,
        int take)
    {
        if (take <= 0) return Array.Empty<RagFusedCandidate>();

        var merged = new Dictionary<string, Accumulator>(StringComparer.Ordinal);

        foreach (var hit in lexical)
        {
            var entry = GetOrAdd(merged, hit.Chunk);
            entry.LexicalRank = hit.Rank;
            entry.LexicalScore = hit.Score;
            entry.Score += 1.0 / (K + hit.Rank);
        }

        foreach (var hit in vector)
        {
            // A vector hit whose chunk is not in the lexical candidate set still
            // needs its text: the vector path carries the chunk it matched, so
            // fusion never has to go back to the store mid-query.
            var entry = GetOrAdd(merged, hit.Chunk);
            entry.VectorRank = hit.Rank;
            entry.VectorScore = hit.Score;
            entry.Score += 1.0 / (K + hit.Rank);
        }

        // Ordinal chunk id as the tie-break. Two candidates with identical
        // fusion scores — which happens constantly, because RRF's values come
        // from a small set of ranks — must order the same way on every machine.
        return merged.Values
            .OrderByDescending(a => a.Score)
            .ThenBy(a => a.Chunk.Id, StringComparer.Ordinal)
            .Take(take)
            .Select((a, i) => new RagFusedCandidate(
                a.Chunk, a.Score, a.LexicalRank, a.LexicalScore, a.VectorRank, a.VectorScore, i + 1))
            .ToList();
    }

    private static Accumulator GetOrAdd(Dictionary<string, Accumulator> merged, RagIndexedChunk chunk)
    {
        if (merged.TryGetValue(chunk.Id, out var existing)) return existing;
        var created = new Accumulator(chunk);
        merged[chunk.Id] = created;
        return created;
    }

    private sealed class Accumulator(RagIndexedChunk chunk)
    {
        public RagIndexedChunk Chunk { get; } = chunk;
        public double Score { get; set; }
        public int? LexicalRank { get; set; }
        public double LexicalScore { get; set; }
        public int? VectorRank { get; set; }
        public double VectorScore { get; set; }
    }
}
