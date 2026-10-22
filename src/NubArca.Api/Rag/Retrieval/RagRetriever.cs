using Microsoft.Extensions.Options;
using NubArca.Api.Ai.TextEmbeddings;
using NubArca.Api.Rag.Domains;

namespace NubArca.Api.Rag.Retrieval;

/// The half of retrieval that needs PostgreSQL: the owner's document corpus and
/// the owner-scoped vector search over it.
///
/// Grouped into one optional dependency rather than two nullable ones, because
/// they are absent TOGETHER or not at all: a host with no AppDbContext has no
/// documents to retrieve from.
public sealed record RagDatabaseServices(
    OwnerDocumentCorpusSource OwnerDocuments,
    OwnerDocumentVectorRetriever OwnerVectors);

/// The retriever: lexical, semantic when configured, fused.
///
/// Every caller in the product goes through here, and every policy decision that
/// governs retrieval is made here rather than by the caller:
///
///  - the DOMAIN is resolved through the code registry, so an unknown key fails
///    closed instead of resolving to a permissive default;
///  - an owner-scoped domain refuses a query with no owner before it reads
///    anything;
///  - evidence is stamped with the domain that produced it, which is what lets
///    the Assistant refuse a prompt whose evidence does not match what it asked
///    for.
///
/// Semantic retrieval is optional everywhere. Disabled, unconfigured, model
/// missing and dimension unsupported all produce the same shape of answer:
/// lexical results, and a mode string saying the semantic path did not run and
/// why.
public sealed class RagRetriever : IRagRetriever
{
    private readonly IRagDomainRegistry _domains;
    private readonly RagDatabaseServices? _database;
    private readonly IOptions<RagOptions> _options;
    private readonly IRagSemanticProfileResolver _semantic;
    private readonly ILogger<RagRetriever> _log;

    public RagRetriever(
        IRagDomainRegistry domains,
        RagDatabaseServices? database,
        IOptions<RagOptions> options,
        IRagSemanticProfileResolver semantic,
        ILogger<RagRetriever> log)
    {
        _domains = domains;
        _database = database;
        _options = options;
        _semantic = semantic;
        _log = log;
    }

    public async Task<RagRetrievalResult> RetrieveAsync(
        RagQuery query, CancellationToken cancellationToken = default)
    {
        if (!_domains.TryGet(query.Domain.Value, out var domain))
        {
            return RagRetrievalResult.Unavailable(query.Domain, RagFailureReasons.DomainUnknown);
        }
        if (domain.RequiresOwner && query.OwnerUserId is null)
        {
            return RagRetrievalResult.Unavailable(query.Domain, RagFailureReasons.OwnerRequired);
        }
        if (query.MaxEvidence <= 0 || query.MaxCharacters <= 0)
        {
            return RagRetrievalResult.None(query.Domain, RagRetrievalModes.Lexical);
        }

        var options = _options.Value;
        var text = (query.Text ?? string.Empty).Trim();
        if (text.Length == 0) return RagRetrievalResult.None(query.Domain, RagRetrievalModes.Lexical);
        if (text.Length > options.EffectiveQueryCharacters)
        {
            text = text[..options.EffectiveQueryCharacters];
        }

        // BEFORE ANY MODEL IS TOUCHED. The ceiling is checked while the corpus is
        // being built, which is upstream of the query embedding and far upstream
        // of generation — so an over-limit private library costs zero provider
        // calls, not one embedding call and then a refusal.
        var built = await BuildIndexAsync(domain, query.OwnerUserId, cancellationToken);
        var index = built.Index;
        if (index is null || index.IsEmpty)
        {
            return RagRetrievalResult.Unavailable(
                query.Domain, built.Reason ?? RagFailureReasons.IndexUnavailable);
        }

        var ranking = RagRankingProfiles.For(query.Domain);
        var shape = RagQueryShape.For(text);
        var lexical = index.Search(
            shape, options.EffectiveLexicalCandidates, query.AllowedSourceIds, query.AllowedSourceKinds);

        // Skipping the vector path here rather than inside it keeps a disabled
        // domain from paying for an embedding call it is going to discard.
        var semanticEnabled = _semantic.Resolve(query.Domain).Enabled;
        var semantic = (semanticEnabled, _database) switch
        {
            // Exact cosine over THIS owner's eligible vectors. A separate
            // retriever rather than a WHERE clause on a shared one, because
            // "approximate index plus an owner predicate" is not an
            // owner-prefiltered search — see OwnerDocumentVectorRetriever.
            (true, { OwnerVectors: { } ownerVectors }) =>
                await ownerVectors.SearchAsync(
                    index, query.OwnerUserId ?? Guid.Empty, text,
                    options.EffectiveVectorCandidates, query.AllowedSourceIds, query.AllowedSourceKinds,
                    cancellationToken),

            (true, _) => RagVectorSearchOutcome.Unavailable(RagFailureReasons.IndexUnavailable),
            _ => RagVectorSearchOutcome.Unavailable(RagFailureReasons.EmbeddingDisabled),
        };

        var mode = semantic.IsAvailable
            ? RagRetrievalModes.Hybrid
            : semantic.Reason == RagFailureReasons.EmbeddingDisabled
                ? RagRetrievalModes.Lexical
                : RagRetrievalModes.LexicalFallback(RagFailureReasons.ShortFallback(semantic.Reason!));

        var fused = RrfFusion.Fuse(lexical, semantic.Hits, options.EffectiveFusedCandidates);

        // Aggregate only: never the question, never an excerpt.
        _log.LogInformation(
            "rag: domain={Domain} mode={Mode} lexical={Lexical} vector={Vector} fused={Fused}",
            domain.Key, mode, lexical.Count, semantic.Hits.Count, fused.Count);

        // The semantic-only floor belongs to the PROFILE that produced the scores.
        if (!HasStrongEvidence(
                fused,
                RagTextEmbeddingModels.MinimumVectorScoreFor(
                    semantic.ProfileKey, options.EffectiveMinimumVectorScore)))
        {
            return RagRetrievalResult.None(query.Domain, mode, index.Corpus.Revision);
        }

        var evidence = BuildEvidence(query, shape, fused, index.Corpus.Revision);
        return evidence.Count == 0
            ? RagRetrievalResult.None(query.Domain, mode, index.Corpus.Revision)
            : new RagRetrievalResult(
                query.Domain, RagRetrievalOutcome.Strong, evidence, mode,
                semantic.ProfileKey, index.Corpus.Revision);
    }

    // ---- corpus ------------------------------------------------------------

    /// An index, or the sanitized reason there is none.
    ///
    /// The reason exists because "empty" and "too large to hold" are opposite
    /// conditions that a bare null cannot tell apart, and an operator reading
    /// `rag_index_unavailable` for a library of a million chunks would go
    /// looking for the wrong problem entirely.
    private readonly record struct IndexOutcome(RagLexicalIndex? Index, string? Reason)
    {
        public static readonly IndexOutcome None = new(null, null);
        public static IndexOutcome Refused(string reason) => new(null, reason);
        public static IndexOutcome Built(RagLexicalIndex index) => new(index, null);
    }

    private async Task<IndexOutcome> BuildIndexAsync(
        RagDomainDefinition domain, Guid? ownerUserId, CancellationToken cancellationToken)
    {
        // AN OWNER-SCOPED INDEX IS BUILT FRESH, EVERY TIME, AND NEVER CACHED.
        //
        // A cache keyed by (domain, owner) would keep every questioner's private
        // index resident for the life of the process — an unbounded map of
        // people's documents, held in memory, that nothing evicts. And it would
        // make "which index does this caller get" a question answered by a cache
        // key, where being wrong once means answering one person from another's
        // documents. Building it per question costs a scan of ONE person's text.
        if (!domain.RequiresOwner) return IndexOutcome.None;
        if (ownerUserId is not { } owner || owner == Guid.Empty) return IndexOutcome.None;
        if (_database?.OwnerDocuments is not { } documents) return IndexOutcome.None;

        // THE CEILING IS ENFORCED BEFORE THE INDEX IS BUILT, NOT AFTER.
        //
        // A private corpus is assembled per request from one person's rows, so
        // without this it would read an unbounded corpus straight into memory,
        // once per question, on a shared process.
        //
        // Read max + 1 and refuse at max + 1. That is what makes the bound
        // detectable rather than invisible: `Take(max)` would silently hand back
        // an arbitrary alphabetical prefix of somebody's library, and an answer
        // drawn from a fraction of a person's documents is indistinguishable, to
        // them, from an answer drawn from all of them. A refusal is legible; a
        // quiet truncation is a wrong answer with a confident tone.
        var ceiling = _options.Value.EffectiveMaxIndexedChunks;
        var corpus = await documents.LoadAsync(owner, ceiling + 1, cancellationToken);
        if (corpus.Chunks.Count > ceiling)
        {
            // Count only — never the owner, never a name, never a title.
            _log.LogWarning(
                "rag: {Domain} corpus exceeds the configured chunk ceiling ({Ceiling}); "
                + "retrieval refused", domain.Key, ceiling);
            return IndexOutcome.Refused(RagFailureReasons.CorpusTooLarge);
        }

        return corpus.IsEmpty
            ? IndexOutcome.None
            : IndexOutcome.Built(new RagLexicalIndex(corpus, RagRankingProfiles.For(domain.DomainKey)));
    }

    // ---- the evidence gate --------------------------------------------------

    /// A retrieval result with one accidental token overlap is not evidence.
    ///
    /// The decision is anchored on the LEXICAL gate, which has a golden set
    /// behind it: a lexical hit that survived RagLexicalIndex's minimum score,
    /// term coverage and high-field requirements means the corpus genuinely
    /// contains the question's vocabulary. A purely semantic candidate can also
    /// qualify, but only at a deliberately high cosine — because cosine is not
    /// calibrated across checkpoints and "0.7 is close" is a statement about one
    /// model, not about retrieval.
    ///
    /// The alternative — send the model whatever ranked first and let it decide
    /// whether the context was any good — was rejected: it pays a boundary
    /// crossing to ask, and a model given three irrelevant paragraphs and a
    /// question tends to answer the question anyway.
    private static bool HasStrongEvidence(IReadOnlyList<RagFusedCandidate> fused, double minimumVectorScore)
        => fused.Any(c => c.LexicalRank is not null)
           || fused.Any(c => c.VectorRank is not null
                             && c.VectorScore >= minimumVectorScore);

    private static List<RagEvidence> BuildEvidence(
        RagQuery query, RagQueryShape shape, IReadOnlyList<RagFusedCandidate> fused, string revision)
    {
        var evidence = new List<RagEvidence>();
        var budget = query.MaxCharacters;

        foreach (var candidate in fused.Take(query.MaxEvidence))
        {
            if (budget <= 0) break;
            var chunk = candidate.Chunk;
            var text = chunk.Text.Length <= budget
                ? chunk.Text
                : RagLexicalIndex.CenterOnMatch(chunk.Text, shape.AllTerms, budget);
            if (text.Length == 0) break;
            budget -= text.Length;

            evidence.Add(new RagEvidence(
                Id: chunk.Id,
                // Stamped from the CHUNK rather than from the request, so a
                // mismatch between what was asked for and what came back is
                // visible to the caller's own policy check instead of being
                // relabelled here.
                Domain: chunk.Domain,
                Path: chunk.Path,
                Title: chunk.Title,
                Section: chunk.Section,
                Text: text,
                Feature: chunk.Feature,
                SourceKind: chunk.SourceKind,
                Audience: chunk.Audience,
                Intent: chunk.Intent,
                Language: chunk.Language,
                Score: candidate.FusionScore,
                SourceKey: chunk.SourceKey,
                Revision: string.IsNullOrEmpty(chunk.Revision) ? revision : chunk.Revision,
                LexicalRank: candidate.LexicalRank,
                VectorRank: candidate.VectorRank,
                FusionRank: candidate.Rank,
                // COPIED FROM THE CHUNK, never from the query.
                //
                // Stamping `query.OwnerUserId` here would make the Assistant's
                // owner gate circular: it compares evidence owner against the
                // authenticated caller, and if this field were the caller's own
                // id written back out, that comparison could only ever confirm
                // that the request agreed with itself. Every chunk in an
                // owner-scoped corpus is stamped by the corpus source from the
                // live owner its eligibility join verified, so the gate now
                // checks retrieval's OUTPUT against the caller — two facts
                // derived independently, which is the only version of the check
                // that can fail when something is wrong.
                //
                // An unstamped chunk carries null and stays null. The gate
                // refuses unstamped evidence for an owner-scoped domain, so such
                // a chunk fails there rather than being relabelled with the
                // caller's id on the way past.
                OwnerUserId: chunk.OwnerUserId,
                // Provenance, copied from the chunk like the owner: which
                // source and where in it. Null for every system domain.
                DocumentSourceKind: chunk.DocumentSourceKind,
                DocumentSourceId: chunk.DocumentSourceId,
                Page: chunk.Page,
                LocatorKind: chunk.LocatorKind,
                LocatorIndex: chunk.LocatorIndex,
                LocatorLabel: chunk.LocatorLabel,
                Ordinal: chunk.Ordinal));
        }

        return evidence;
    }
}
