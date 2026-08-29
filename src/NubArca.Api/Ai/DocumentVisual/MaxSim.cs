namespace NubArca.Api.Ai.DocumentVisual;

/// LATE-INTERACTION SCORING, as one arithmetic primitive.
///
///     score(Q, D) = Σ_i max_j dot(Q_i, D_j)
///
/// Every query vector finds its best match anywhere on the page and those
/// bests are summed. That is what makes late interaction good at documents: a
/// question with four ideas in it can match four different regions of one page,
/// which a single pooled vector per page cannot express.
///
/// SEPARATED FROM EVERY MODEL ON PURPOSE. It is pure arithmetic over two lists
/// of floats, so it can be tested against a hand-computed fixture rather than
/// against whatever a checkpoint happens to produce — and swapping the model
/// family, or dropping in a fused SIMD kernel later, cannot change what the
/// score MEANS.
///
/// NOTHING HERE RESHAPES ANYTHING. A dimension mismatch, an empty side, a
/// non-finite component: each is refused with a stated answer rather than
/// padded, truncated or coerced into a number that would rank.
public static class MaxSim
{
    /// The score, or NaN when the inputs do not describe comparable sequences.
    ///
    /// NaN rather than an exception because this runs inside a ranking loop over
    /// stored rows, and one corrupt row must be skippable without failing
    /// somebody's question. Callers filter on `double.IsFinite`.
    public static double Score(
        IReadOnlyList<float[]> query, IReadOnlyList<float[]> document, int dimension)
    {
        if (dimension <= 0) return double.NaN;
        if (query.Count == 0 || document.Count == 0) return double.NaN;

        double total = 0;
        foreach (var q in query)
        {
            if (q.Length != dimension) return double.NaN;

            var best = double.NegativeInfinity;
            foreach (var d in document)
            {
                if (d.Length != dimension) return double.NaN;

                double dot = 0;
                for (var i = 0; i < dimension; i++)
                {
                    dot += (double)q[i] * d[i];
                }

                if (double.IsNaN(dot)) return double.NaN;
                if (dot > best) best = dot;
            }

            if (!double.IsFinite(best)) return double.NaN;
            total += best;
        }

        return double.IsFinite(total) ? total : double.NaN;
    }

    /// Decode a stored multi-vector blob into its sequence.
    ///
    /// The declared `vectorCount` and `dimension` are checked against the byte
    /// length rather than trusted, because a blob whose length disagrees is not
    /// an error anywhere else in the system: read as fewer, longer vectors it
    /// produces a perfectly finite score for a page that does not exist.
    public static IReadOnlyList<float[]>? Decode(
        IAiVectorSerializer serializer, byte[] bytes, int vectorCount, int dimension)
    {
        if (vectorCount <= 0 || dimension <= 0) return null;
        if ((long)bytes.Length != (long)vectorCount * dimension * sizeof(float)) return null;

        float[] flat;
        try
        {
            flat = serializer.Deserialize(bytes, vectorCount * dimension);
        }
        catch (ArgumentException)
        {
            return null;
        }

        var vectors = new List<float[]>(vectorCount);
        for (var v = 0; v < vectorCount; v++)
        {
            var slice = new float[dimension];
            Array.Copy(flat, v * dimension, slice, 0, dimension);
            foreach (var value in slice)
            {
                if (!float.IsFinite(value)) return null;
            }
            vectors.Add(slice);
        }

        return vectors;
    }

    /// Pack a sequence into the canonical float32 blob, vectors end to end.
    public static byte[] Encode(
        IAiVectorSerializer serializer, IReadOnlyList<float[]> vectors, int dimension)
    {
        if (vectors.Count == 0) throw new ArgumentException("No vectors.", nameof(vectors));

        var flat = new float[vectors.Count * dimension];
        for (var v = 0; v < vectors.Count; v++)
        {
            if (vectors[v].Length != dimension)
            {
                throw new ArgumentException(
                    $"Vector {v} has {vectors[v].Length} components but {dimension} were expected.",
                    nameof(vectors));
            }
            Array.Copy(vectors[v], 0, flat, v * dimension, dimension);
        }

        return serializer.Serialize(flat);
    }
}
