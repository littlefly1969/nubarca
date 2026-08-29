using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NubArca.Api.Data;

namespace NubArca.Api.Ai.DocumentVisual;

/// A question, asked of one person's rendered pages.
///
/// The owner is a parameter and never a request field: it is derived from the
/// authenticated caller by the endpoint and threaded down here. There is no
/// `fileIds`, no `profile`, no `renderer`, no `domain` — nothing a browser can
/// say that changes whose documents are searched or with which model.
public sealed record DocumentVisualQuery(
    Guid OwnerUserId, string Text, int MaxUnits, int MaxFiles);

/// One visual hit. Ids and a rank — no pixels, no vector, no page text.
public sealed record DocumentVisualHit(
    Guid FileItemId, Guid VisualUnitId, int Rank, double Score, string Mode);

/// The result of the visual pass, including the sanitized reason it did not run.
///
/// Unavailability is a first-class outcome and never an exception: the caller
/// answers from text alone, which is a complete and correct answer that simply
/// did not get the extra signal.
public sealed record DocumentVisualRetrievalResult(
    IReadOnlyList<DocumentVisualHit> Hits,
    IReadOnlyList<Guid> CandidateFileIds,
    string Mode,
    string? ProfileKey,
    string? Reason)
{
    public bool IsAvailable => Reason is null;

    public static DocumentVisualRetrievalResult Unavailable(string reason)
        => new(Array.Empty<DocumentVisualHit>(), Array.Empty<Guid>(),
               DocumentVisualModes.Unavailable, null, reason);
}

public static class DocumentVisualModes
{
    public const string Unavailable = "unavailable";

    /// Dense cosine, ranked inside PostgreSQL over this owner's eligible rows.
    public const string DenseAccelerated = "dense-pgvector";

    /// Dense cosine, ranked in process over this owner's bounded corpus.
    public const string DenseExact = "dense-exact";

    /// Dense candidates reranked by exact MaxSim over stored multi-vectors.
    public const string LateInteraction = "dense+late-interaction";
}

public interface IOwnerDocumentVisualRetriever
{
    Task<DocumentVisualRetrievalResult> RetrieveAsync(
        DocumentVisualQuery query, CancellationToken cancellationToken = default);

    /// Null when the visual path can run, or the sanitized reason it cannot.
    ///
    /// A separate call rather than "run a query and read its reason", because a
    /// status probe must not embed a question or touch a vector — and because
    /// an empty query would report `disabled` on a perfectly healthy
    /// installation, which is the sort of diagnostic that sends an operator
    /// looking for the wrong problem.
    Task<string?> CheckReadinessAsync(CancellationToken cancellationToken = default);
}

/// VISUAL RETRIEVAL OVER ONE PERSON'S DOCUMENTS.
///
/// What this returns is a list of places to LOOK — file candidates, ranked by
/// how much a rendered page of theirs resembles the question. It is not
/// evidence, it never becomes evidence, and nothing downstream may quote it.
/// The text side decides what NubArca is allowed to say; see
/// `PrivateDocumentAssistantService` for where the two meet.
///
/// TWO RANKING PATHS, ONE GUARANTEE. With the pgvector accelerator present the
/// cosine is computed in the database; without it the owner's bounded corpus is
/// deserialized and ranked exactly in process. Both apply owner and live
/// eligibility BEFORE any limit — the accelerator in SQL joins, the fallback in
/// the EF query that selects candidates — because "top K then filter by owner"
/// is not a filtered search, it is a search of everybody's documents with a
/// filter applied to the answer.
///
/// AND THE FALLBACK REFUSES RATHER THAN TRUNCATES. An owner whose visual corpus
/// exceeds the exact-search ceiling gets `visual-corpus-too-large` and a
/// text-only answer. Ranking an arbitrary prefix of somebody's library and
/// presenting it as their documents is the failure mode that looks like success.
public sealed class OwnerDocumentVisualRetriever : IOwnerDocumentVisualRetriever
{
    private readonly AppDbContext _db;
    private readonly DocumentVisualProfileResolver _profiles;
    private readonly DocumentVisualRenderers _renderers;
    private readonly DocumentVisualVectorIndexService _accelerator;
    private readonly IAiVectorSerializer _serializer;
    private readonly IOptions<DocumentVisualOptions> _options;
    private readonly VisualLateInteractionReranker _late;
    private readonly ILogger<OwnerDocumentVisualRetriever> _log;

    public OwnerDocumentVisualRetriever(
        AppDbContext db,
        DocumentVisualProfileResolver profiles,
        DocumentVisualRenderers renderers,
        DocumentVisualVectorIndexService accelerator,
        IAiVectorSerializer serializer,
        IOptions<DocumentVisualOptions> options,
        VisualLateInteractionReranker late,
        ILogger<OwnerDocumentVisualRetriever> log)
    {
        _db = db;
        _profiles = profiles;
        _renderers = renderers;
        _accelerator = accelerator;
        _serializer = serializer;
        _options = options;
        _late = late;
        _log = log;
    }

    public async Task<string?> CheckReadinessAsync(CancellationToken cancellationToken = default)
    {
        var resolution = await _profiles.ResolveAsync(cancellationToken);
        if (!resolution.IsAvailable)
        {
            return resolution.Reason ?? DocumentVisualReasons.ModelUnavailable;
        }

        return _renderers.ActiveRenderProfileKeys.Count == 0
            ? DocumentVisualReasons.RendererUnavailable
            : null;
    }

    public async Task<DocumentVisualRetrievalResult> RetrieveAsync(
        DocumentVisualQuery query, CancellationToken cancellationToken = default)
    {
        // NO OWNER, NO SEARCH. Not "no results": the caller asked an
        // owner-scoped question without saying whose, and answering it from
        // anybody's documents is the failure this whole area exists to prevent.
        if (query.OwnerUserId == Guid.Empty)
        {
            return DocumentVisualRetrievalResult.Unavailable(Rag.RagFailureReasons.OwnerRequired);
        }

        var text = (query.Text ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return DocumentVisualRetrievalResult.Unavailable(DocumentVisualReasons.Disabled);
        }

        var resolution = await _profiles.ResolveAsync(cancellationToken);
        if (!resolution.IsAvailable)
        {
            return DocumentVisualRetrievalResult.Unavailable(
                resolution.Reason ?? DocumentVisualReasons.ModelUnavailable);
        }

        var profile = resolution.Profile!;
        var dimension = profile.Dimension!.Value;
        var options = _options.Value;
        var renderKeys = _renderers.ActiveRenderProfileKeys;
        if (renderKeys.Count == 0)
        {
            return DocumentVisualRetrievalResult.Unavailable(DocumentVisualReasons.RendererUnavailable);
        }

        float[] queryVector;
        try
        {
            // THE PAIRED TEXT TOWER, exactly. A query embedded by any other
            // model is a point in a different space, and the cosine against it
            // is a number that looks like a score.
            var embedded = await resolution.Queries!.EmbedTextAsync(text, profile, cancellationToken);
            queryVector = embedded.Vector;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return DocumentVisualRetrievalResult.Unavailable(DocumentVisualReasons.ModelUnavailable);
        }

        if (queryVector.Length != dimension || !queryVector.All(float.IsFinite))
        {
            return DocumentVisualRetrievalResult.Unavailable(
                DocumentVisualReasons.ModelOutputUnsupported);
        }

        var take = Math.Clamp(query.MaxUnits, 1, options.EffectiveVisualUnitCandidates);

        var accelerated = await _accelerator.SearchAsync(
            profile.Id, query.OwnerUserId, queryVector, renderKeys, take, cancellationToken);

        var (scored, mode, reason) = accelerated is not null
            ? (accelerated.Select(n => new DocumentVisualCandidate(n.VisualUnitId, n.FileItemId, n.Score)).ToList(),
               DocumentVisualModes.DenseAccelerated, (string?)null)
            : await ExactAsync(query.OwnerUserId, profile.Id, dimension, queryVector, renderKeys,
                take, options, cancellationToken);

        if (reason is not null) return DocumentVisualRetrievalResult.Unavailable(reason);

        // OPTIONAL SECOND STAGE. Late interaction reranks dense candidates and
        // never replaces them: if no profile is promoted, or its worker is not
        // reachable, the dense order stands. Raw dense cosines and MaxSim scores
        // are never summed — the reranker returns an ORDER, and rank is all that
        // survives into fusion.
        var reranked = await _late.RerankAsync(
            query.OwnerUserId, text, scored, cancellationToken);
        if (reranked is not null)
        {
            scored = reranked;
            mode = DocumentVisualModes.LateInteraction;
        }

        var hits = scored
            .Select((s, i) => new DocumentVisualHit(s.FileItemId, s.VisualUnitId, i + 1, s.Score, mode))
            .ToList();

        // AGGREGATION BY BEST RANK, NOT BY SUM.
        //
        // Summing a document's page scores would let a hundred-page report
        // outrank a one-page invoice that is a better answer, purely by being
        // long — and length is the one property a visual embedding cannot see.
        // A file is as relevant as its most relevant page.
        var files = hits
            .GroupBy(h => h.FileItemId)
            .Select(g => (FileItemId: g.Key, Rank: g.Min(h => h.Rank)))
            .OrderBy(g => g.Rank)
            .Take(Math.Clamp(query.MaxFiles, 1, options.EffectiveVisualCandidateFiles))
            .Select(g => g.FileItemId)
            .ToList();

        // Aggregates only: counts and a mode. Never the question, never a file
        // name, never an owner id.
        _log.LogInformation(
            "document-visual: mode={Mode} units={Units} files={Files}", mode, hits.Count, files.Count);

        return new DocumentVisualRetrievalResult(hits, files, mode, profile.Key, null);
    }

    /// Exact cosine over this owner's bounded eligible corpus, in process.
    ///
    /// The candidate query carries the whole eligibility rule and is bounded at
    /// the ceiling PLUS ONE, so "at the ceiling" and "past it" are
    /// distinguishable from the row count alone. Reading exactly the ceiling
    /// would make a corpus one unit too large indistinguishable from one that
    /// fits, and the difference decides between a refusal and a silently partial
    /// search.
    private async Task<(List<DocumentVisualCandidate>, string, string?)> ExactAsync(
        Guid ownerUserId,
        Guid profileId,
        int dimension,
        float[] queryVector,
        IReadOnlyCollection<string> renderKeys,
        int take,
        DocumentVisualOptions options,
        CancellationToken cancellationToken)
    {
        var ceiling = options.EffectiveMaxVisualUnitsPerOwnerExactFallback;

        var candidates = await (
            from row in OwnerDocumentVisualEligibility.EligibleUnits(
                _db.DocumentVisualUnits.AsNoTracking(),
                _db.DocumentVisualIndexes.AsNoTracking(),
                _db.FileItems.AsNoTracking(),
                ownerUserId, profileId, renderKeys)
            join embedding in _db.DocumentVisualEmbeddings.AsNoTracking()
                on row.Unit.Id equals embedding.DocumentVisualUnitId
            where embedding.ProfileId == profileId
                  && embedding.Layout == DocumentVisualEmbeddingLayouts.Dense
                  && embedding.Dimension == dimension
            orderby row.Unit.Id
            select new { row.Unit.Id, FileItemId = row.File.Id, embedding.EmbeddingBytes })
            .Take(ceiling + 1)
            .ToListAsync(cancellationToken);

        if (candidates.Count > ceiling)
        {
            _log.LogWarning(
                "document-visual: owner corpus exceeds the exact-search ceiling ({Ceiling}); "
                + "visual retrieval refused", ceiling);
            return (new List<DocumentVisualCandidate>(), DocumentVisualModes.Unavailable,
                DocumentVisualReasons.CorpusTooLarge);
        }

        var scored = new List<DocumentVisualCandidate>(candidates.Count);
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            float[] vector;
            try
            {
                vector = _serializer.Deserialize(candidate.EmbeddingBytes, dimension);
            }
            catch (ArgumentException)
            {
                // A malformed row is skipped, never guessed at: a corruption to
                // repair by re-embedding, not a reason to fail a question.
                continue;
            }

            var score = Cosine(queryVector, vector);
            if (double.IsFinite(score))
            {
                scored.Add(new DocumentVisualCandidate(candidate.Id, candidate.FileItemId, score));
            }
        }

        var top = scored
            .OrderByDescending(s => s.Score)
            // Deterministic ties: two units at the same cosine must come back in
            // the same order every run, or the fusion above them is unstable.
            .ThenBy(s => s.VisualUnitId)
            .Take(take)
            .ToList();

        return (top, DocumentVisualModes.DenseExact, null);
    }

    /// Cosine, computed rather than assumed.
    ///
    /// SigLIP2's output is normalized, so a dot product would usually do — and
    /// "usually" is doing the work in that sentence. A profile whose model does
    /// not normalize would silently produce numbers that are not cosines and
    /// compare wrongly against everything calibrated on them.
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
