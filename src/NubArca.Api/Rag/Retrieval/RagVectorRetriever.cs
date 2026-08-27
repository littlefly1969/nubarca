using Microsoft.Extensions.Options;
using NubArca.Api.Ai.TextEmbeddings;
using NubArca.Api.Rag.Storage;

namespace NubArca.Api.Rag.Retrieval;

/// Semantic retrieval: embed the question locally, ask pgvector for the nearest
/// chunks OF THIS DOMAIN under THIS profile.
///
/// Three filters, all mandatory and all applied in the database rather than
/// afterwards:
///
///  - the DOMAIN, so a `product-help` question can never surface a repository
///    chunk that happens to be semantically close;
///  - the PROFILE, exactly — two embedding profiles are two coordinate systems,
///    and a cosine between them is a number with no meaning;
///  - the DIMENSION, which the vector table enforces by existing per dimension.
///
/// Every failure here is degradation, never an error: no profile, no model, no
/// pgvector and an unsupported dimension all return a reason, and the caller
/// answers lexically.
public sealed class RagVectorRetriever
{
    private readonly TextEmbeddingResolver _embeddings;
    private readonly RagVectorIndexService _vectors;
    private readonly IOptions<RagOptions> _options;

    public RagVectorRetriever(
        TextEmbeddingResolver embeddings,
        RagVectorIndexService vectors,
        IOptions<RagOptions> options)
    {
        _embeddings = embeddings;
        _vectors = vectors;
        _options = options;
    }

    public async Task<RagVectorSearchOutcome> SearchAsync(
        RagLexicalIndex index,
        string queryText,
        int take,
        CancellationToken cancellationToken = default)
    {
        // THIS domain's profile. The index already knows which domain it is, so
        // the question "which model embeds this question" is answered by the
        // corpus being searched rather than by an installation-wide setting that
        // is right for one domain and wrong for the other.
        var resolution = await _embeddings.ResolveAsync(index.Corpus.Domain, cancellationToken);
        if (!resolution.IsAvailable)
        {
            return RagVectorSearchOutcome.Unavailable(
                resolution.Reason ?? RagFailureReasons.EmbeddingProfileUnavailable);
        }

        var profile = resolution.Profile!;
        if (!RagVectorIndexService.SupportsDimension(profile.Dimension))
        {
            return RagVectorSearchOutcome.Unavailable(RagFailureReasons.EmbeddingDimensionUnsupported);
        }

        float[] vector;
        try
        {
            // Query, not Passage. The provider applies whatever its model needs;
            // getting this wrong is invisible in every test that does not
            // measure retrieval quality, which is why the kind is a required
            // argument rather than a defaulted one.
            var embedded = await resolution.Provider!.EmbedAsync(
                profile, queryText, TextEmbeddingInputKind.Query, cancellationToken);
            vector = embedded.Vector;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (TextEmbeddingUnavailableException ex)
        {
            return RagVectorSearchOutcome.Unavailable(ex.ReasonCode);
        }
        catch
        {
            return RagVectorSearchOutcome.Unavailable(RagFailureReasons.EmbeddingFailed);
        }

        var neighbours = await _vectors.SearchAsync(
            index.Domain.Value, profile.Id, vector,
            Math.Min(take, _options.Value.EffectiveVectorCandidates), cancellationToken);

        if (neighbours is null)
        {
            return RagVectorSearchOutcome.Unavailable(RagFailureReasons.PgvectorUnavailable);
        }

        var hits = new List<RagVectorHit>(neighbours.Count);
        var rank = 0;
        foreach (var neighbour in neighbours)
        {
            // A vector row whose chunk is no longer in this domain's corpus is
            // skipped rather than fetched: the corpus is the membership answer,
            // and a stale row must not be able to reintroduce a source that was
            // removed from the domain.
            if (!index.TryGetByChunkId(neighbour.ChunkId, out var chunk)) continue;
            rank++;
            hits.Add(new RagVectorHit(chunk, neighbour.Score, rank));
        }

        return new RagVectorSearchOutcome(hits, profile.Key, null);
    }
}
