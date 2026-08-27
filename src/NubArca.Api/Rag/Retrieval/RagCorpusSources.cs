using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NubArca.Api.Data;
using NubArca.Api.Rag.Domains;
using NubArca.Api.Rag.ProductHelp;
using NubArca.Api.Rag.Text;

namespace NubArca.Api.Rag.Retrieval;

/// Where a domain's lexical corpus comes from, and how to tell whether it
/// changed without loading it.
///
/// The signature exists so a cached index can be validated cheaply. Rebuilding
/// a BM25 index over a repository on every question would make retrieval
/// quadratic in how often somebody asks; never rebuilding it would make `rag
/// index` require a restart to take effect.
public interface IRagCorpusSource
{
    /// A short string that changes whenever the corpus does. Cheap enough to
    /// compute on every retrieval.
    Task<string> GetSignatureAsync(RagDomainKey domain, CancellationToken cancellationToken = default);

    Task<RagCorpus> LoadAsync(RagDomainKey domain, CancellationToken cancellationToken = default);
}

/// The indexed corpus in PostgreSQL: the general path, and the only one that can
/// carry embeddings.
public sealed class DatabaseRagCorpusSource : IRagCorpusSource
{
    private readonly AppDbContext _db;

    public DatabaseRagCorpusSource(AppDbContext db)
    {
        _db = db;
    }

    public async Task<string> GetSignatureAsync(
        RagDomainKey domain, CancellationToken cancellationToken = default)
    {
        // AGGREGATES, not rows, and deliberately never over `rag_chunks`.
        //
        // The chunk table is the large one — tens of thousands of rows holding
        // the corpus text — and touching it here would put a join over all of it
        // on the path of every question. It is also unnecessary: the indexer
        // stamps a source's `UpdatedAt` whenever its content hash changes, and a
        // source's chunks only change when its content does.
        //
        // MEMBERSHIP is included for a reason found by review rather than by a
        // failing test. Ranking reads Priority, Feature, Aliases, Intent,
        // Audience, SourceKind and Language — and all of them live on
        // `rag_domain_sources`, not on the source. Reclassifying a document
        // touches only the membership row, so a signature built from source
        // timestamps alone would leave a running web process serving a lexical
        // index built from the old classification until it restarted. The CLI
        // clears its own cache after indexing; correctness must not depend on
        // that, because the web host never runs it.
        var membership =
            from m in _db.RagDomainSources.AsNoTracking()
            join source in _db.RagSources.AsNoTracking() on m.SourceId equals source.Id
            where m.DomainKey == domain.Value
            select new
            {
                // The MEMBERSHIP's revision: which snapshot this domain says it
                // is describing. The source row carries content, not a snapshot.
                m.Revision,
                SourceStamp = source.UpdatedAt ?? source.CreatedAt,
                MembershipStamp = m.UpdatedAt ?? m.CreatedAt,
            };

        var count = await membership.CountAsync(cancellationToken);
        if (count == 0) return "empty";

        var sourceStamp = await membership.MaxAsync(x => x.SourceStamp, cancellationToken);
        var membershipStamp = await membership.MaxAsync(x => x.MembershipStamp, cancellationToken);

        // Distinct revisions, not "the newest one": a domain holding two of them
        // is a mixed snapshot, and RagRetriever refuses it. Counting here means
        // the signature also changes as a reindex converges, so the cached index
        // is rebuilt the moment the domain becomes coherent again.
        var revisions = await membership
            .Select(x => x.Revision)
            .Distinct()
            .OrderBy(r => r)
            .ToListAsync(cancellationToken);

        return $"db:{count}:{sourceStamp:O}:{membershipStamp:O}:{string.Join('|', revisions)}";
    }

    public async Task<RagCorpus> LoadAsync(
        RagDomainKey domain, CancellationToken cancellationToken = default)
    {
        var rows = await (
            from chunk in _db.RagChunks.AsNoTracking()
            join source in _db.RagSources.AsNoTracking() on chunk.SourceId equals source.Id
            join membership in _db.RagDomainSources.AsNoTracking()
                on source.Id equals membership.SourceId
            where membership.DomainKey == domain.Value
            orderby source.SourceKey, chunk.Ordinal
            select new Row(
                chunk.Id, chunk.Ordinal, chunk.Heading, chunk.Text, chunk.MetadataJson,
                source.SourceKey, source.Path, source.Title, source.SourceKind,
                source.Language, membership.Revision,
                membership.Priority, membership.MetadataJson))
            .ToListAsync(cancellationToken);

        if (rows.Count == 0) return RagCorpus.Empty(domain);

        var chunks = new List<RagIndexedChunk>(rows.Count);
        foreach (var row in rows)
        {
            var domainMetadata = Deserialize(row.MembershipMetadataJson);
            var chunkMetadata = Deserialize(row.ChunkMetadataJson);

            // The DOMAIN's classification wins over the source's own. The same
            // file is `documentation` to the repository and `user-guide` to
            // Product Help, and each domain must rank it by its own reading.
            var sourceKind = domainMetadata.GetValueOrDefault(RagMetadataKeys.SourceKind, row.SourceKind);
            var language = domainMetadata.GetValueOrDefault(RagMetadataKeys.Language, row.Language);

            // A repository chunk's declared symbols are its aliases: the same
            // high-weight field Product Help fills with the words people use for
            // a feature, filled with the identifiers people search for.
            var aliases = ParseAliases(domainMetadata)
                .Concat(SplitSymbols(chunkMetadata.GetValueOrDefault(RagMetadataKeys.Symbols))
                    .SelectMany(RagText.IdentifierTerms))
                .Concat(PathTerms(row.SourceKey).SelectMany(RagText.IdentifierTerms))
                .Distinct(StringComparer.Ordinal)
                .ToList();

            chunks.Add(new RagIndexedChunk(
                Id: $"{row.SourceKey}#{row.Ordinal}",
                Domain: domain,
                SourceKey: row.SourceKey,
                Path: row.Path,
                Title: row.Title,
                Section: row.Heading,
                Text: row.Text,
                SourceKind: sourceKind,
                Language: language,
                Revision: row.Revision,
                Feature: domainMetadata.GetValueOrDefault(RagMetadataKeys.Feature, string.Empty),
                Aliases: aliases,
                Audience: domainMetadata.GetValueOrDefault(RagMetadataKeys.Audience, string.Empty),
                Intent: domainMetadata.GetValueOrDefault(RagMetadataKeys.Intent, string.Empty),
                Priority: row.Priority,
                ChunkId: row.ChunkId));
        }

        // NO MODAL REVISION. A domain holding MEMBERSHIPS from two commits is not
        // a snapshot with a majority opinion — it is an interrupted reindex, and
        // picking the most common, newest or first revision would let the corpus
        // claim a coherence it does not have. The empty revision marks it, and
        // RagRetriever refuses to answer from it.
        //
        // Note what this is now measured over. Two DOMAINS at two revisions is
        // an ordinary sequential upgrade and is none of this domain's business;
        // one domain at two revisions is still the thing that fails closed.
        var revisions = rows
            .Select(r => r.Revision)
            .Where(r => !string.IsNullOrEmpty(r))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return new RagCorpus(
            domain,
            revisions.Count == 1 ? revisions[0] : string.Empty,
            chunks,
            IsMixedRevision: revisions.Count > 1);
    }

    /// Path segments without their extension: `frontend/src/pages/PeoplePage.tsx`
    /// contributes `PeoplePage`, `pages`, `src`, `frontend`. Somebody asking
    /// "where are the face tabs defined" is half-remembering a path.
    private static IEnumerable<string> PathTerms(string sourceKey)
    {
        foreach (var segment in sourceKey.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var dot = segment.LastIndexOf('.');
            yield return dot > 0 ? segment[..dot] : segment;
        }
    }

    private static IEnumerable<string> SplitSymbols(string? symbols)
        => string.IsNullOrWhiteSpace(symbols)
            ? Array.Empty<string>()
            : symbols.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    private static IReadOnlyList<string> ParseAliases(IReadOnlyDictionary<string, string> metadata)
    {
        if (!metadata.TryGetValue(RagMetadataKeys.Aliases, out var json)) return Array.Empty<string>();
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
    }

    private static IReadOnlyDictionary<string, string> Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                   ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private readonly record struct Row(
        Guid ChunkId, int Ordinal, string Heading, string Text, string? ChunkMetadataJson,
        string SourceKey, string Path, string Title, string SourceKind,
        string Language, string Revision,
        int Priority, string? MembershipMetadataJson);
}

/// The Product Help corpus that ships INSIDE the image, as a lexical corpus.
///
/// This is why Help keeps working on an installation that has never run `rag
/// index`, and why the production image does not need a repository checkout or
/// a database write to answer a product question. It is the fallback, not a
/// second design: when a database index for `product-help` exists it wins,
/// because only the database index can carry embeddings.
public sealed class BundledProductHelpCorpusSource : IRagCorpusSource
{
    private readonly ProductHelpCorpus _corpus;
    private readonly Lazy<RagCorpus> _projected;

    public BundledProductHelpCorpusSource(ProductHelpCorpus corpus)
    {
        _corpus = corpus;
        _projected = new Lazy<RagCorpus>(Project, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public Task<string> GetSignatureAsync(RagDomainKey domain, CancellationToken cancellationToken = default)
        => Task.FromResult(domain.Value == RagDomains.ProductHelp
            ? $"bundled:{_corpus.Revision}:{_corpus.Documents.Count}"
            : "empty");

    public Task<RagCorpus> LoadAsync(RagDomainKey domain, CancellationToken cancellationToken = default)
        => Task.FromResult(domain.Value == RagDomains.ProductHelp
            ? _projected.Value
            : RagCorpus.Empty(domain));

    private RagCorpus Project()
    {
        var chunks = _corpus.Documents.Select(d => new RagIndexedChunk(
            Id: d.Id,
            Domain: RagDomainKey.ProductHelp,
            SourceKey: d.Path,
            Path: d.Path,
            Title: d.Title,
            Section: d.Section,
            Text: d.Text,
            SourceKind: d.SourceKind,
            Language: d.Language,
            Revision: _corpus.Revision,
            Feature: d.Feature,
            Aliases: d.Aliases,
            Audience: d.Audience,
            Intent: d.Intent,
            Priority: d.Priority)).ToList();
        return new RagCorpus(RagDomainKey.ProductHelp, _corpus.Revision, chunks);
    }
}
