using Microsoft.EntityFrameworkCore;
using NubArca.Api.Ai.Documents;
using NubArca.Api.Data;
using NubArca.Api.Domain.Ai;
using NubArca.Api.Rag.Domains;

namespace NubArca.Api.Rag.Retrieval;

/// ONE PERSON's documents, as a retrievable corpus.
///
/// The whole file is one idea: the derived rows are not the authority. A
/// `DocumentText`, its chunks and its embeddings record an extraction that
/// happened at some point in the past, and between then and now the file may
/// have been deleted, moved into the Private Vault, or excluded from the
/// library. Every load therefore JOINS to the live `FileItem` through
/// OwnerDocumentEligibility, and a chunk whose file no longer qualifies is not
/// in the corpus at all — not filtered out later, not ranked and then dropped.
///
/// That is deliberately not left to cleanup. A sweeper that deletes orphaned
/// chunks is housekeeping and it runs on a schedule; a privacy boundary that
/// only holds after the sweeper has run is a boundary that fails for as long as
/// the sweeper is behind. Here, deleting a file removes its answers on the very
/// next question, because the join stops matching.
///
/// FILTER BEFORE RANKING, and it is the database that does it. Loading every
/// owner's chunks and narrowing afterwards would put one person's documents in
/// the same in-memory index as another's, one bug away from a cross-owner
/// answer — and it would rank against a corpus the caller has no right to.
public sealed class OwnerDocumentCorpusSource
{
    private readonly AppDbContext _db;

    public OwnerDocumentCorpusSource(AppDbContext db)
    {
        _db = db;
    }

    /// Everything this owner may currently be answered from.
    ///
    /// `RagCorpus.Revision` is a constant token rather than a commit. A person's
    /// library has no revision — it is not a snapshot of anything and there is
    /// no build to check it against — but the corpus contract needs a non-empty
    /// value, because an EMPTY revision is how the system domains say "this
    /// index is incoherent, refuse it". A private corpus is never incoherent in
    /// that sense: it is whatever the owner currently has.
    public async Task<RagCorpus> LoadAsync(
        Guid ownerUserId, CancellationToken cancellationToken = default)
    {
        if (ownerUserId == Guid.Empty) return RagCorpus.Empty(RagDomainKey.UserDocuments);

        var rows = await EligibleChunks(ownerUserId)
            .OrderBy(r => r.Name).ThenBy(r => r.Ordinal)
            .Select(r => new Row(
                r.ChunkId, r.Ordinal, r.Heading, r.Text, r.Name, r.DocumentTextId, r.FileItemId))
            .ToListAsync(cancellationToken);

        if (rows.Count == 0) return RagCorpus.Empty(RagDomainKey.UserDocuments);

        var chunks = new List<RagIndexedChunk>(rows.Count);
        foreach (var row in rows)
        {
            chunks.Add(new RagIndexedChunk(
                // The chunk's own id, which is what evidence cites internally.
                Id: row.ChunkId.ToString("N"),
                Domain: RagDomainKey.UserDocuments,
                // SourceKey and Path are both the FILE NAME. Not the storage
                // key, not a physical path, not the logical folder path — a
                // citation needs to name the document a person recognises, and
                // everything beyond the name is either forbidden to expose or
                // detail they did not ask for.
                SourceKey: row.Name,
                Path: row.Name,
                Title: row.Name,
                Section: row.Heading ?? string.Empty,
                Text: row.Text ?? string.Empty,
                SourceKind: RagSourceKinds.Documentation,
                // A person's documents are in whatever language they wrote them
                // in, and NubArca does not know which. Asserting `it` or `en`
                // here would put a wrong value on a field ranking reads, and a
                // wrong value is worse than an absent one.
                Language: RagLanguages.Unknown,
                Revision: PrivateRevision,
                // No editorial metadata: nobody classified these, and a schema
                // being able to hold an `intent` is not a reason to invent one.
                Feature: string.Empty,
                Aliases: Array.Empty<string>(),
                Audience: string.Empty,
                Intent: string.Empty,
                Priority: 50,
                ChunkId: row.ChunkId));
        }

        return new RagCorpus(RagDomainKey.UserDocuments, PrivateRevision, chunks);
    }

    /// A cheap signature of what this owner currently has.
    ///
    /// Used for diagnostics and for the `documents status` command, NOT to cache
    /// an index across requests — see RagRetriever for why a private index is
    /// built fresh every time.
    public async Task<OwnerDocumentCorpusStats> GetStatsAsync(
        Guid ownerUserId, CancellationToken cancellationToken = default)
    {
        if (ownerUserId == Guid.Empty) return OwnerDocumentCorpusStats.Empty;

        var eligible = EligibleChunks(ownerUserId);
        var chunks = await eligible.CountAsync(cancellationToken);
        var documents = await eligible
            .Select(r => r.DocumentTextId).Distinct().CountAsync(cancellationToken);

        return new OwnerDocumentCorpusStats(documents, chunks);
    }

    /// How many of this owner's ELIGIBLE chunks have a vector under this
    /// profile. Counted through the same join, so a chunk whose file left the
    /// library stops counting the moment it does.
    public async Task<long> CountEmbeddedAsync(
        Guid ownerUserId, Guid profileId, CancellationToken cancellationToken = default)
    {
        if (ownerUserId == Guid.Empty) return 0;

        return await EligibleChunks(ownerUserId)
            .Where(r => _db.DocumentChunkEmbeddings.Any(
                e => e.DocumentChunkId == r.ChunkId && e.ProfileId == profileId))
            .LongCountAsync(cancellationToken);
    }

    /// The ONE join every private read goes through.
    ///
    /// Owner appears TWICE on purpose — once on the chunk, once through the
    /// eligibility predicate on the file — and they are not redundant. The chunk
    /// column is a denormalized copy written at extraction time, and the
    /// FileItem is the live truth. Requiring both means neither a stale copy nor
    /// a re-parented file can produce a chunk this caller may read.
    ///
    /// The Private Vault needs no clause here beyond the explicit one inside
    /// OwnerDocumentEligibility: `FileItem` carries a global query filter of
    /// `PrivateVaultId == null`, and nothing in this file says
    /// `IgnoreQueryFilters()`. A vaulted document is invisible to this join by
    /// construction.
    private IQueryable<EligibleRow> EligibleChunks(Guid ownerUserId)
        => from chunk in _db.DocumentChunks.AsNoTracking()
           join document in _db.DocumentTexts.AsNoTracking()
               on chunk.DocumentTextId equals document.Id
           join file in OwnerDocumentEligibility.Eligible(_db.FileItems.AsNoTracking(), ownerUserId)
               on document.FileItemId equals file.Id
           where chunk.OwnerUserId == ownerUserId
                 && document.OwnerUserId == ownerUserId
                 && document.Status == AiArtifactStatuses.Completed
           select new EligibleRow
           {
               ChunkId = chunk.Id,
               Ordinal = chunk.Ordinal,
               Heading = chunk.Heading,
               Text = chunk.Text,
               Name = file.Name,
               DocumentTextId = document.Id,
               FileItemId = file.Id,
           };

    /// The revision token a private corpus carries.
    ///
    /// Deliberately not empty: an empty revision is the system domains' signal
    /// for "incoherent index, refuse it", and a private corpus is never that.
    /// Deliberately not a commit either — a person's documents are not a
    /// snapshot of a build, so `user-documents` is not revision-gated and
    /// nothing compares this to `NUBARCA_GIT_SHA`.
    public const string PrivateRevision = "owner-library";

    private sealed class EligibleRow
    {
        public Guid ChunkId { get; init; }
        public int Ordinal { get; init; }
        public string? Heading { get; init; }
        public string? Text { get; init; }
        public string Name { get; init; } = string.Empty;
        public Guid DocumentTextId { get; init; }
        public Guid FileItemId { get; init; }
    }

    private sealed record Row(
        Guid ChunkId, int Ordinal, string? Heading, string? Text,
        string Name, Guid DocumentTextId, Guid FileItemId);
}

/// Safe counts about one owner's private corpus. Numbers only — never a name,
/// never a title, never an excerpt.
public sealed record OwnerDocumentCorpusStats(int Documents, int Chunks)
{
    public static readonly OwnerDocumentCorpusStats Empty = new(0, 0);
}
