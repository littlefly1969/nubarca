using Microsoft.EntityFrameworkCore;
using NubArca.Api.Ai;
using NubArca.Api.Ai.Documents;
using NubArca.Api.Ai.TextEmbeddings;
using NubArca.Api.Data;
using NubArca.Api.Domain.Ai;

namespace NubArca.Api.Rag.Retrieval;

/// Semantic retrieval over ONE PERSON's documents.
///
/// EXACT COSINE, not an approximate index, and that is a decision rather than a
/// shortcut. `ORDER BY embedding <=> query LIMIT 10` against a global HNSW with
/// `WHERE OwnerUserId = …` is NOT an owner-prefiltered nearest-neighbour search:
/// the index is traversed over everybody's vectors and the predicate is applied
/// to what the traversal happens to surface, so a person with few documents in a
/// large installation can get back nothing while their own best match sat one
/// hop off the path. The result is silently wrong — the query succeeds and
/// returns fewer, worse rows — which is the worst way for a privacy-adjacent
/// path to be wrong.
///
/// The alternatives were an index per owner (thousands of HNSW indexes) or
/// partitioning (a partition per owner). Both are real designs and both want a
/// benchmark against a real corpus, which this slice does not have. So: restrict
/// to the owner's eligible chunks FIRST, then rank all of them exactly. A
/// person's own documents are a corpus of hundreds to a few thousand chunks, and
/// a few thousand dot products is microseconds. When that stops being true,
/// there is a measurement to do and a slice to do it in.
///
/// The candidate set is bounded regardless, so a library that grew past what
/// this approach suits degrades into an ARBITRARY N rather than into an
/// unbounded read. Arbitrary, not newest: the bound is applied after ordering by
/// `chunk.Id`, and those ids are random v4 GUIDs, so their sort order carries no
/// chronology whatsoever — it is stable between two identical questions and
/// otherwise meaningless. Ordering exists to make the truncation deterministic,
/// not to make it a good choice. An owner past `MaxCandidateVectors` is getting
/// a silently partial semantic pass, and picking WHICH partial set is worth
/// having deserves the benchmark this slice does not have; recency, if it ever
/// becomes the answer, needs a timestamp column to order by.
public sealed class OwnerDocumentVectorRetriever
{
    /// Ceiling on how many of an owner's vectors are ranked in one question.
    /// Not a tuning knob for relevance — it is the bound that keeps one
    /// question from turning into an unbounded read.
    public const int MaxCandidateVectors = 20_000;

    private readonly AppDbContext _db;
    private readonly OwnerDocumentCorpusSource _corpus;
    private readonly TextEmbeddingResolver _embeddings;
    private readonly IAiVectorSerializer _serializer;

    public OwnerDocumentVectorRetriever(
        AppDbContext db,
        OwnerDocumentCorpusSource corpus,
        TextEmbeddingResolver embeddings,
        IAiVectorSerializer serializer)
    {
        _db = db;
        _corpus = corpus;
        _embeddings = embeddings;
        _serializer = serializer;
    }

    public async Task<RagVectorSearchOutcome> SearchAsync(
        RagLexicalIndex index,
        Guid ownerUserId,
        string queryText,
        int take,
        /// The SERVER-ONLY narrowing (see RagQuery.AllowedFileItemIds), applied
        /// inside the candidate query so a scoped question ranks only scoped
        /// vectors. It intersects with eligibility and can never widen it.
        IReadOnlyCollection<Guid>? allowedFileItemIds = null,
        CancellationToken cancellationToken = default)
    {
        // NO OWNER, NO SEARCH. Not "no results" — the caller asked an
        // owner-scoped question without saying whose, and answering it from
        // anybody's documents is the failure this whole domain exists to
        // prevent.
        if (ownerUserId == Guid.Empty)
        {
            return RagVectorSearchOutcome.Unavailable(RagFailureReasons.OwnerRequired);
        }

        var resolution = await _embeddings.ResolveAsync(
            RagDomainKey.UserDocuments, cancellationToken);
        if (!resolution.IsAvailable)
        {
            return RagVectorSearchOutcome.Unavailable(
                resolution.Reason ?? RagFailureReasons.EmbeddingProfileUnavailable);
        }

        var profile = resolution.Profile!;
        var dimension = profile.Dimension!.Value;

        float[] queryVector;
        try
        {
            // Query, not Passage. The provider applies whatever prefix its model
            // was trained with; embedding a question as a passage measurably
            // degrades retrieval and nothing about the vector looks wrong.
            var embedded = await resolution.Provider!.EmbedAsync(
                profile, queryText, TextEmbeddingInputKind.Query, cancellationToken);
            queryVector = embedded.Vector;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (TextEmbeddingUnavailableException ex)
        {
            // Degradation, never an error: the caller answers lexically, and
            // still only from this owner's documents.
            return RagVectorSearchOutcome.Unavailable(ex.ReasonCode);
        }

        if (queryVector.Length != dimension || !queryVector.All(float.IsFinite))
        {
            return RagVectorSearchOutcome.Unavailable(RagFailureReasons.EmbeddingDimensionUnsupported);
        }

        // OWNER AND ELIGIBILITY IN THE QUERY, BEFORE ANY LIMIT.
        //
        // The join runs through the live FileItem, so a document deleted or
        // moved into the Vault since it was embedded contributes no candidate at
        // all — its vector row may still exist, and it is unreachable. The
        // profile is matched exactly, because two profiles are two coordinate
        // systems and a cosine between them is a number with no meaning.
        var candidates = await (
            from row in OwnerDocumentEligibility.EligibleChunks(
                _db.DocumentChunks.AsNoTracking(),
                _db.DocumentTexts.AsNoTracking(),
                _db.FileItems.AsNoTracking(),
                ownerUserId,
                allowedFileItemIds)
            join embedding in _db.DocumentChunkEmbeddings.AsNoTracking()
                on row.Chunk.Id equals embedding.DocumentChunkId
            where embedding.ProfileId == profile.Id
                  && embedding.Dimension == dimension
            orderby row.Chunk.Id
            select new { row.Chunk.Id, embedding.EmbeddingBytes })
            .Take(MaxCandidateVectors)
            .ToListAsync(cancellationToken);

        if (candidates.Count == 0)
        {
            return new RagVectorSearchOutcome(
                Array.Empty<RagVectorHit>(), profile.Key, null);
        }

        // The lexical index holds the SAME owner-restricted corpus, built in the
        // same request from the same join. Resolving hits through it means a
        // vector row that somehow escaped the filter still cannot become
        // evidence: there is no chunk to attach it to.
        var byChunkId = index.Corpus.Chunks
            .Where(c => c.ChunkId != Guid.Empty)
            .ToDictionary(c => c.ChunkId);

        var scored = new List<(RagIndexedChunk Chunk, double Score)>(candidates.Count);
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!byChunkId.TryGetValue(candidate.Id, out var chunk)) continue;

            float[] vector;
            try
            {
                vector = _serializer.Deserialize(candidate.EmbeddingBytes, dimension);
            }
            catch (ArgumentException)
            {
                // A malformed row is skipped, never guessed at. It is a
                // corruption to repair by re-embedding, not a reason to fail a
                // question.
                continue;
            }

            var score = Cosine(queryVector, vector);
            if (double.IsFinite(score)) scored.Add((chunk, score));
        }

        var hits = scored
            .OrderByDescending(s => s.Score)
            // Deterministic ties: two chunks at the same cosine must come back
            // in the same order on every run, or fusion below them is unstable.
            .ThenBy(s => s.Chunk.Id, StringComparer.Ordinal)
            .Take(Math.Max(1, take))
            .Select((s, i) => new RagVectorHit(s.Chunk, s.Score, i + 1))
            .ToList();

        return new RagVectorSearchOutcome(hits, profile.Key, null);
    }

    /// Cosine, computed rather than assumed.
    ///
    /// The embedding model normalizes, so a dot product would usually do — but
    /// "usually" is doing the work in that sentence, and a profile whose model
    /// does not normalize would silently produce scores that are not cosines and
    /// compare wrongly against the evidence gate's threshold.
    private static double Cosine(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length || a.Length == 0) return double.NaN;

        double dot = 0, normA = 0, normB = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += (double)a[i] * b[i];
            normA += (double)a[i] * a[i];
            normB += (double)b[i] * b[i];
        }

        if (normA <= 0 || normB <= 0) return double.NaN;
        return dot / (Math.Sqrt(normA) * Math.Sqrt(normB));
    }
}
