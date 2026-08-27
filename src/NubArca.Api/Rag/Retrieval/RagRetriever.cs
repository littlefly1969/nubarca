using Microsoft.Extensions.Options;
using NubArca.Api.Ai.TextEmbeddings;
using NubArca.Api.Rag.Domains;
using NubArca.Api.Rag.Storage;

namespace NubArca.Api.Rag.Retrieval;

/// The half of retrieval that needs PostgreSQL: the indexed corpus, the vector
/// table, and the profile resolution that connects them.
///
/// Grouped into one optional dependency rather than four nullable ones, because
/// they are absent TOGETHER or not at all. A host with no connection string
/// still answers Product Help questions from the corpus bundled in the image —
/// lexically, with no index and no vectors — and saying that once here is
/// clearer than four null checks that each look like they could vary
/// independently.
public sealed record RagDatabaseServices(
    DatabaseRagCorpusSource Corpus,
    RagVectorRetriever Vectors,
    TextEmbeddingResolver Embeddings,
    RagVectorIndexService VectorIndex,
    OwnerDocumentCorpusSource? OwnerDocuments = null,
    OwnerDocumentVectorRetriever? OwnerVectors = null);

/// The domain-general retriever: lexical, semantic when configured, fused.
///
/// Every caller in the product goes through here, and every policy decision that
/// governs retrieval is made here rather than by the caller:
///
///  - the DOMAIN is resolved through the code registry, so an unknown key fails
///    closed instead of resolving to a permissive default;
///  - an owner-scoped domain refuses a query with no owner, today, while no such
///    domain exists — so the check is already there when one does;
///  - `product-help` refuses a corpus built from a different revision than the
///    running build, because Help that describes a feature this installation
///    does not have is worse than no Help;
///  - evidence is stamped with the domain that produced it, which is what lets
///    the Assistant refuse a prompt whose evidence does not match what it asked
///    for.
///
/// Semantic retrieval is optional everywhere. Disabled, unconfigured, model
/// missing, pgvector missing and dimension unsupported all produce the same
/// shape of answer: lexical results, and a mode string saying the semantic path
/// did not run and why.
public sealed class RagRetriever : IRagRetriever
{
    private readonly IRagDomainRegistry _domains;
    private readonly RagDatabaseServices? _database;
    private readonly BundledProductHelpCorpusSource _bundled;
    private readonly RagLexicalIndexCache _cache;
    private readonly IOptions<RagOptions> _options;
    private readonly IRagSemanticProfileResolver _semantic;
    private readonly ILogger<RagRetriever> _log;

    public RagRetriever(
        IRagDomainRegistry domains,
        RagDatabaseServices? database,
        BundledProductHelpCorpusSource bundled,
        RagLexicalIndexCache cache,
        IOptions<RagOptions> options,
        IRagSemanticProfileResolver semantic,
        ILogger<RagRetriever> log)
    {
        _domains = domains;
        _database = database;
        _bundled = bundled;
        _cache = cache;
        _options = options;
        _semantic = semantic;
        _log = log;
    }

    /// The revision this process was built from, or empty outside an image.
    /// The same environment variable the deploy gates read, so "knowledge
    /// revision == running revision" compares against the SAME provenance the
    /// rest of the system uses.
    public static string RunningRevision
        => Environment.GetEnvironmentVariable("NUBARCA_GIT_SHA") ?? string.Empty;

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
        if (index.Corpus.IsMixedRevision)
        {
            // An interrupted reindex left this domain describing two commits at
            // once. Answering from it would mix two releases in one context.
            _log.LogWarning(
                "rag: {Domain} index holds more than one revision; retrieval disabled until a "
                + "complete reindex finishes", domain.Key);
            return RagRetrievalResult.Unavailable(query.Domain, RagFailureReasons.MixedRevisionIndex);
        }
        if (!AcceptsRevision(domain, index.Corpus.Revision))
        {
            _log.LogWarning(
                "rag: {Domain} index revision does not match the running build; retrieval disabled",
                domain.Key);
            return RagRetrievalResult.Unavailable(query.Domain, RagFailureReasons.RevisionMismatch);
        }

        var ranking = RagRankingProfiles.For(query.Domain);
        var shape = RagQueryShape.For(text, ranking.ExpandAliases);
        var lexical = index.Search(shape, options.EffectiveLexicalCandidates);

        // PER DOMAIN. The repository stays lexical while Help is hybrid, because
        // that is what the two of them measured — and skipping the vector path
        // here rather than inside it keeps a disabled domain from paying for an
        // embedding call it is going to discard.
        var semanticEnabled = _semantic.Resolve(query.Domain).Enabled;
        var semantic = (semanticEnabled, _database, domain.RequiresOwner) switch
        {
            // Owner-scoped: exact cosine over THIS owner's eligible vectors. A
            // separate retriever rather than a WHERE clause on the shared one,
            // because "approximate index plus an owner predicate" is not an
            // owner-prefiltered search — see OwnerDocumentVectorRetriever.
            (true, { OwnerVectors: { } ownerVectors }, true) =>
                await ownerVectors.SearchAsync(
                    index, query.OwnerUserId ?? Guid.Empty, text,
                    options.EffectiveVectorCandidates, cancellationToken),

            (true, { } services, false) =>
                await services.Vectors.SearchAsync(
                    index, text, options.EffectiveVectorCandidates, cancellationToken),

            (true, _, _) => RagVectorSearchOutcome.Unavailable(RagFailureReasons.IndexUnavailable),
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

        if (!HasStrongEvidence(fused, options))
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

    public async Task<RagDomainStatus> GetStatusAsync(
        RagDomainKey domain, Guid? ownerUserId = null, CancellationToken cancellationToken = default)
    {
        if (!_domains.TryGet(domain.Value, out var definition))
        {
            return new RagDomainStatus(domain, false, null, 0, 0, null, 0, 0, false,
                RagFailureReasons.DomainUnknown);
        }

        // Status is a READ of the same corpus, so it obeys the same rule: an
        // owner-scoped domain has no installation-wide status to report, and
        // asking without an owner answers `rag_owner_required` rather than
        // reporting somebody's counts to whoever asked.
        if (definition.RequiresOwner && (ownerUserId is not { } o || o == Guid.Empty))
        {
            return new RagDomainStatus(domain, false, null, 0, 0, null, 0, 0, false,
                RagFailureReasons.OwnerRequired);
        }

        var built = await BuildIndexAsync(definition, ownerUserId, cancellationToken);
        var index = built.Index;
        var revision = index?.Corpus.Revision;
        var mixed = index?.Corpus.IsMixedRevision == true;
        var available = index is { IsEmpty: false }
                        && !mixed
                        && AcceptsRevision(definition, revision ?? string.Empty);

        var resolution = _database is null
            ? TextEmbeddingResolution.Unavailable(RagFailureReasons.IndexUnavailable)
            : await _database.Embeddings.ResolveAsync(domain, cancellationToken);
        long embeddings = 0, vectors = 0;
        var semanticAvailable = false;
        if (resolution.IsAvailable && _database is not null)
        {
            // Canonical and accelerated counted SEPARATELY, and both scoped to
            // this domain. Reporting one number for both would hide the only
            // discrepancy `rag coverage` exists to surface: embeddings that
            // exist but were never mirrored into the vector table.
            embeddings = await _database.VectorIndex.CountCanonicalAsync(
                domain.Value, resolution.Profile!.Id, cancellationToken);
            vectors = await _database.VectorIndex.CountIndexedAsync(
                domain.Value, resolution.Profile.Id, cancellationToken);
            semanticAvailable = await _database.VectorIndex.IsBackendAvailableAsync(
                resolution.Profile.Dimension, cancellationToken);
        }

        var reason = available
            ? (semanticAvailable ? null : resolution.Reason ?? RagFailureReasons.PgvectorUnavailable)
            : index is null or { IsEmpty: true }
                ? built.Reason ?? RagFailureReasons.IndexUnavailable
                : mixed
                    ? RagFailureReasons.MixedRevisionIndex
                    : RagFailureReasons.RevisionMismatch;

        return new RagDomainStatus(
            domain,
            available,
            string.IsNullOrEmpty(revision) ? null : revision,
            index?.Corpus.Chunks.Select(c => c.SourceKey).Distinct(StringComparer.Ordinal).Count() ?? 0,
            index?.ChunkCount ?? 0,
            resolution.Profile?.Key,
            embeddings,
            vectors,
            semanticAvailable,
            reason);
    }

    // ---- corpus ------------------------------------------------------------

    /// The database index wins when it has content; the bundled Product Help
    /// corpus is the fallback. Only the database index can carry embeddings, so
    /// preferring it is also what makes semantic Help possible at all — and
    /// falling back is what keeps Help working on an installation that has never
    /// run `rag index`.
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
        var key = domain.DomainKey;

        // AN OWNER-SCOPED DOMAIN IS BUILT FRESH, EVERY TIME, AND NEVER CACHED.
        //
        // The cache is keyed by domain, and widening that key to (domain, owner)
        // would be two lines and two problems. It would keep every questioner's
        // private index resident for the life of the process — an unbounded map
        // of people's documents, held in memory, that nothing evicts. And it
        // would make "which index does this caller get" a question answered by
        // a cache key, where being wrong once means answering one person from
        // another's documents.
        //
        // Building it per question costs a scan of ONE person's text. That is a
        // corpus of hundreds to a few thousand chunks, not the twenty-three
        // thousand the repository domain has, and it is the right trade for the
        // one domain where a cache-key bug is a privacy incident.
        if (domain.RequiresOwner)
        {
            if (ownerUserId is not { } owner || owner == Guid.Empty) return IndexOutcome.None;
            if (_database?.OwnerDocuments is not { } documents) return IndexOutcome.None;

            // THE CEILING IS ENFORCED BEFORE THE INDEX IS BUILT, NOT AFTER.
            //
            // Every other domain is bounded at INDEX time, by RagIndexer, which
            // refuses a corpus past `MaxIndexedChunks`. A private corpus never
            // passes through the indexer at all — it is assembled per request
            // from one person's rows — so without this it was the one path that
            // read an unbounded corpus straight into memory, once per question,
            // on a shared process.
            //
            // Read max + 1 and refuse at max + 1. That is what makes the bound
            // detectable rather than invisible: `Take(max)` would silently hand
            // back an arbitrary alphabetical prefix of somebody's library, and
            // an answer drawn from a fraction of a person's documents is
            // indistinguishable, to them, from an answer drawn from all of them.
            // A refusal is legible; a quiet truncation is a wrong answer with a
            // confident tone.
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
                : IndexOutcome.Built(new RagLexicalIndex(corpus, RagRankingProfiles.For(key)));
        }

        if (_database is { } services)
        {
            var databaseSignature = await services.Corpus.GetSignatureAsync(key, cancellationToken);
            if (databaseSignature != "empty")
            {
                return IndexOutcome.Built(await _cache.GetOrBuildAsync(
                    key, databaseSignature, ct => services.Corpus.LoadAsync(key, ct), cancellationToken));
            }
        }

        var bundledSignature = await _bundled.GetSignatureAsync(key, cancellationToken);
        if (bundledSignature == "empty") return IndexOutcome.None;

        return IndexOutcome.Built(await _cache.GetOrBuildAsync(
            key, bundledSignature, ct => _bundled.LoadAsync(key, ct), cancellationToken));
    }

    /// REVISION GATE, for the domain that ships with a release.
    ///
    /// Help answering from a newer `main` would tell an operator to click
    /// something their installation does not have. An UNKNOWN running revision —
    /// a development run outside the image — cannot be compared, so the corpus
    /// is accepted; a KNOWN one that disagrees is refused.
    ///
    /// The repository domain is deliberately NOT gated. It is a development and
    /// diagnostics corpus whose whole point is being able to ask about a
    /// checkout, including one that is not what is running; its revision is
    /// REPORTED on every answer instead.
    private static bool AcceptsRevision(RagDomainDefinition domain, string corpusRevision)
    {
        if (domain.Key != RagDomains.ProductHelp) return true;
        var running = RunningRevision;
        if (string.IsNullOrEmpty(running)) return true;
        return string.Equals(corpusRevision, running, StringComparison.Ordinal);
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
    private static bool HasStrongEvidence(IReadOnlyList<RagFusedCandidate> fused, RagOptions options)
        => fused.Any(c => c.LexicalRank is not null)
           || fused.Any(c => c.VectorRank is not null
                             && c.VectorScore >= options.EffectiveMinimumVectorScore);

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
                // A system chunk carries null and stays null. The gate refuses
                // unstamped evidence for an owner-scoped domain, so a system
                // chunk that reached a private result fails there rather than
                // being relabelled with the caller's id on the way past.
                OwnerUserId: chunk.OwnerUserId));
        }

        return evidence;
    }
}
