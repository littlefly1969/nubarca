using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NubArca.Api.Data;
using NubArca.Api.Domain.Ai;

namespace NubArca.Api.Ai.DocumentVisual;

/// THE SECOND STAGE, and only ever a second stage.
///
/// Dense retrieval finds K candidate pages; this loads their stored
/// multi-vectors, embeds the question as its own sequence, and reorders those K
/// by exact MaxSim. It does not widen the candidate set, does not query the
/// database for anything the dense pass did not already authorize, and cannot
/// introduce a page the owner-prefiltered pass did not surface.
///
/// THAT ORDERING IS THE WHOLE SECURITY ARGUMENT for not needing a multi-vector
/// ANN engine. A global PLAID/WARP/TACHIOM index would have to re-establish
/// owner filtering inside a specialised engine; reranking a list the eligible
/// query already produced inherits it. So the engine stays a replaceable
/// optimisation, and this release does not need one.
///
/// It is also entirely optional. No promoted profile, no configured worker, an
/// unreachable worker, a candidate with no stored multi-vector: every one of
/// them returns null, and the dense order stands. Late interaction improving
/// retrieval is a measurement; late interaction being REQUIRED would be a new
/// way for visual search to break.
public sealed class VisualLateInteractionReranker
{
    private readonly AppDbContext _db;
    private readonly IAiProfileRegistry _profiles;
    private readonly IVisualLateInteractionProvider? _provider;
    private readonly IAiVectorSerializer _serializer;
    private readonly IOptions<DocumentVisualOptions> _options;
    private readonly ILogger<VisualLateInteractionReranker> _log;

    public VisualLateInteractionReranker(
        AppDbContext db,
        IAiProfileRegistry profiles,
        IAiVectorSerializer serializer,
        IOptions<DocumentVisualOptions> options,
        ILogger<VisualLateInteractionReranker> log,
        IVisualLateInteractionProvider? provider = null)
    {
        _db = db;
        _profiles = profiles;
        _serializer = serializer;
        _options = options;
        _log = log;
        _provider = provider;
    }

    /// Reordered candidates, or NULL meaning "the dense order stands".
    ///
    /// Null and an empty list are different answers on purpose: empty would
    /// silently delete every candidate the dense pass found, turning an absent
    /// optional reranker into no visual results at all.
    public async Task<List<DocumentVisualCandidate>?> RerankAsync(
        Guid ownerUserId,
        string queryText,
        IReadOnlyList<DocumentVisualCandidate> candidates,
        CancellationToken cancellationToken = default)
    {
        var options = _options.Value;
        if (!options.LateInteractionEnabled || _provider is null) return null;
        if (candidates.Count == 0) return null;

        var key = (options.LateProfileKey ?? string.Empty).Trim();
        if (key.Length == 0) return null;

        var profile = await _profiles.GetProfileByKeyAsync(key, cancellationToken);
        if (profile is null || !profile.Enabled || profile.Dimension is not { } dimension)
        {
            return null;
        }

        if (dimension > options.EffectiveMaxLateInteractionDimension) return null;
        if (!_provider.CheckReadiness(profile).Ready) return null;

        var pool = candidates
            .Take(options.EffectiveMaxMultiVectorCandidateUnits)
            .ToList();
        var unitIds = pool.Select(c => c.VisualUnitId).ToList();

        // The stored page vectors for THESE units only. The dense pass already
        // proved every one of them belongs to this owner and is eligible; this
        // reads them back by id and adds no predicate of its own, because a
        // second, weaker spelling of the boundary is how the two drift apart.
        var stored = await _db.DocumentVisualEmbeddings.AsNoTracking()
            .Where(e => unitIds.Contains(e.DocumentVisualUnitId)
                        && e.ProfileId == profile.Id
                        && e.Layout == DocumentVisualEmbeddingLayouts.LateInteraction
                        && e.Dimension == dimension)
            .Select(e => new { e.DocumentVisualUnitId, e.VectorCount, e.EmbeddingBytes })
            .ToListAsync(cancellationToken);

        if (stored.Count == 0) return null;

        MultiVectorEmbeddingResult query;
        try
        {
            query = await _provider.EmbedQueryAsync(profile, queryText, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // An unreachable or failing worker degrades to the dense order. It
            // is never an error for the question and never a verdict about a
            // document.
            return null;
        }

        if (!Validate(query, dimension, options)) return null;

        var byUnit = new Dictionary<Guid, double>();
        foreach (var row in stored)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (row.VectorCount > options.EffectiveMaxVectorsPerVisualUnit) continue;
            if (row.EmbeddingBytes.Length > options.EffectiveMaxMultiVectorBytesPerUnit) continue;

            var page = MaxSim.Decode(_serializer, row.EmbeddingBytes, row.VectorCount, dimension);
            if (page is null) continue;

            var score = MaxSim.Score(query.Vectors, page, dimension);
            if (double.IsFinite(score)) byUnit[row.DocumentVisualUnitId] = score;
        }

        if (byUnit.Count == 0) return null;

        // A CANDIDATE WITH NO MULTI-VECTOR KEEPS ITS PLACE BEHIND THE RERANKED
        // ONES rather than being dropped. Half a corpus embedded under a newly
        // promoted late profile must not make the other half disappear from
        // search while the backfill runs.
        var reranked = pool
            .Where(c => byUnit.ContainsKey(c.VisualUnitId))
            .OrderByDescending(c => byUnit[c.VisualUnitId])
            .ThenBy(c => c.VisualUnitId)
            .ToList();

        var remainder = pool.Where(c => !byUnit.ContainsKey(c.VisualUnitId));

        var result = reranked.Concat(remainder).ToList();

        _log.LogInformation(
            "document-visual: late rerank candidates={Candidates} scored={Scored}",
            pool.Count, byUnit.Count);

        return result;
    }

    /// The provider's own output, checked against the profile and the ceilings.
    ///
    /// A model that returns more vectors than declared, the wrong dimension, or
    /// a non-finite component fails the profile rather than being trimmed. There
    /// is no silent reshape anywhere on this path.
    public static bool Validate(
        MultiVectorEmbeddingResult result, int dimension, DocumentVisualOptions options)
    {
        if (result.VectorCount <= 0) return false;
        if (result.VectorCount > options.EffectiveMaxVectorsPerVisualUnit) return false;
        if (result.Dimension != dimension) return false;
        if (dimension > options.EffectiveMaxLateInteractionDimension) return false;

        var bytes = (long)result.VectorCount * dimension * sizeof(float);
        if (bytes > options.EffectiveMaxMultiVectorBytesPerUnit) return false;

        foreach (var vector in result.Vectors)
        {
            if (vector.Length != dimension) return false;
            foreach (var value in vector)
            {
                if (!float.IsFinite(value)) return false;
            }
        }

        return true;
    }
}
