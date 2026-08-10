namespace NubArca.Api.Ai.Faces;

// Pure, deterministic selection of a Person's reference faces from the faces the
// owner has ALREADY confirmed for them. No inference, no photo reads, no model
// knowledge: the only signals are the existing embeddings and the existing
// per-face quality score.
//
// Why more than one reference: a person photographed over decades occupies a
// whole REGION of the embedding space, and a single arbitrary face only covers
// part of it. Instead of classifying age, film vs. digital, or colour vs. B/W —
// which would need a model we do not have and would be wrong about people
// anyway — the reference set is grown by embedding DIVERSITY, so whatever
// actually varies is discovered from the vectors themselves.
public static class PersonReferenceSelector
{
    // Hard cap on references per person/profile: also the number of ANN searches
    // one similar-face request can cost.
    public const int MaxPersonReferenceFaces = 6;

    // A confirmed face considered for the reference set.
    public readonly record struct ReferenceCandidate(Guid FaceDetectionId, float[] Vector, double Quality);

    // Grow a reference set to at most `max` faces.
    //
    // `seed` is kept as-is (existing persisted references) so an incremental
    // maintenance pass never rebuilds from history. The first reference of an
    // empty set is the highest-quality candidate; every later one maximises
    //
    //     selectionScore = novelty * (0.5 + 0.5 * quality)
    //
    // where novelty = 1 - (best cosine similarity to an already-selected
    // reference). A candidate whose coverage already reaches `coverageThreshold`
    // is considered represented and is never selected — which is what stops an
    // ordinary person at 1-3 references while a wide appearance span grows to 6.
    //
    // Ties break on FaceDetectionId ascending, so the same inputs always give the
    // same set.
    public static IReadOnlyList<Guid> Select(
        IReadOnlyList<ReferenceCandidate> candidates,
        double coverageThreshold,
        IReadOnlyList<ReferenceCandidate>? seed = null,
        int max = MaxPersonReferenceFaces)
    {
        var cap = Math.Clamp(max, 1, MaxPersonReferenceFaces);
        var selected = new List<ReferenceCandidate>(cap);
        var chosen = new HashSet<Guid>();

        if (seed is not null)
        {
            foreach (var s in seed)
            {
                if (selected.Count >= cap)
                {
                    break;
                }
                if (s.Vector.Length > 0 && chosen.Add(s.FaceDetectionId))
                {
                    selected.Add(s);
                }
            }
        }

        // Deterministic scan order; empty vectors are unusable.
        var pool = candidates
            .Where(c => c.Vector.Length > 0 && !chosen.Contains(c.FaceDetectionId))
            .OrderBy(c => c.FaceDetectionId)
            .ToList();

        if (selected.Count == 0)
        {
            var firstIndex = -1;
            for (var i = 0; i < pool.Count; i++)
            {
                // Highest quality wins; ties go to the lowest FaceDetectionId, which
                // the pool order already guarantees (strict >).
                if (firstIndex < 0 || pool[i].Quality > pool[firstIndex].Quality)
                {
                    firstIndex = i;
                }
            }
            if (firstIndex < 0)
            {
                return Array.Empty<Guid>();
            }
            selected.Add(pool[firstIndex]);
            chosen.Add(pool[firstIndex].FaceDetectionId);
            pool.RemoveAt(firstIndex);
        }

        while (selected.Count < cap && pool.Count > 0)
        {
            var bestIndex = -1;
            var bestScore = double.NegativeInfinity;
            for (var i = 0; i < pool.Count; i++)
            {
                var coverage = MaxCoverage(pool[i].Vector, selected);
                if (coverage >= coverageThreshold)
                {
                    continue; // already represented by an existing reference
                }
                var novelty = 1d - coverage;
                var quality = Math.Clamp(pool[i].Quality, 0d, 1d);
                var score = novelty * (0.5d + (0.5d * quality));
                if (score > bestScore)
                {
                    bestScore = score;
                    bestIndex = i;
                }
            }
            if (bestIndex < 0)
            {
                break; // every remaining candidate is covered → stop before the cap
            }
            selected.Add(pool[bestIndex]);
            chosen.Add(pool[bestIndex].FaceDetectionId);
            pool.RemoveAt(bestIndex);
        }

        return selected.Select(s => s.FaceDetectionId).ToList();
    }

    private static double MaxCoverage(float[] vector, IReadOnlyList<ReferenceCandidate> selected)
    {
        var best = -1d;
        foreach (var s in selected)
        {
            var sim = CosineSimilarity(vector, s.Vector);
            if (sim > best)
            {
                best = sim;
            }
        }
        return best;
    }

    // Cosine similarity with explicit norms: stored embeddings are not guaranteed
    // to be unit vectors, so a plain dot product would silently mis-rank.
    public static double CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length == 0 || a.Length != b.Length)
        {
            return 0d;
        }

        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += (double)a[i] * b[i];
            na += (double)a[i] * a[i];
            nb += (double)b[i] * b[i];
        }
        if (na <= 0 || nb <= 0)
        {
            return 0d;
        }
        return dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }
}
