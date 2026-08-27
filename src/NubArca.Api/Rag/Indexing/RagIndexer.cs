using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NubArca.Api.Ai;
using NubArca.Api.Ai.TextEmbeddings;
using NubArca.Api.Data;
using NubArca.Api.Domain.Rag;
using NubArca.Api.Rag.Chunking;
using NubArca.Api.Rag.Domains;
using NubArca.Api.Rag.Sources;
using NubArca.Api.Rag.Storage;

namespace NubArca.Api.Rag.Indexing;

/// Turns a snapshot into an index, idempotently.
///
/// The whole design is content hashes. A source whose SHA-256 has not changed
/// keeps its chunks; a chunk whose text hash has not changed keeps its
/// embedding. That is what makes reindexing after `git pull` cost inference on
/// the files that actually changed rather than on the repository, and it is why
/// the second run of an unchanged snapshot writes nothing at all.
///
/// Deletion is part of indexing rather than a separate sweep. A source that has
/// left the snapshot loses its membership in THIS domain, and a source with no
/// memberships left is removed entirely — chunks, embeddings and vector rows
/// following by cascade. An index that only ever grows would keep answering from
/// a file somebody deleted three releases ago.
///
/// A source row is CONTENT and a membership row is a snapshot claim, so domains
/// sharing a document upgrade independently and sequentially. See
/// UpsertSourceAsync for why that separation exists and what it replaced.
public sealed class RagIndexer : IRagIndexer
{
    private readonly AppDbContext _db;
    private readonly IRagDomainRegistry _domains;
    private readonly IEnumerable<IRagSourceProvider> _providers;
    private readonly TextEmbeddingResolver _embeddings;
    private readonly IAiVectorSerializer _serializer;
    private readonly RagVectorIndexService _vectors;
    private readonly IOptions<RagOptions> _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<RagIndexer> _log;

    public RagIndexer(
        AppDbContext db,
        IRagDomainRegistry domains,
        IEnumerable<IRagSourceProvider> providers,
        TextEmbeddingResolver embeddings,
        IAiVectorSerializer serializer,
        RagVectorIndexService vectors,
        IOptions<RagOptions> options,
        TimeProvider clock,
        ILogger<RagIndexer> log)
    {
        _db = db;
        _domains = domains;
        _providers = providers;
        _embeddings = embeddings;
        _serializer = serializer;
        _vectors = vectors;
        _options = options;
        _clock = clock;
        _log = log;
    }

    public async Task<RagIndexOutcome> IndexAsync(
        RagIndexRequest request, CancellationToken cancellationToken = default)
    {
        // An unknown domain is refused before anything is read. There is no
        // "create the domain if it does not exist": a domain is a policy, and a
        // policy cannot be created by typing it into a command line.
        var domain = _domains.GetRequired(request.Domain);

        var provider = _providers.FirstOrDefault(p =>
            string.Equals(p.Domain, domain.Key, StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                $"No RAG source provider is registered for domain '{domain.Key}'.");

        if (string.IsNullOrWhiteSpace(request.Revision))
        {
            throw new ArgumentException(
                "A RAG index needs the revision it was built from.", nameof(request));
        }

        var now = _clock.GetUtcNow().UtcDateTime;
        var state = new IndexState();
        var seenSourceIds = new HashSet<Guid>();
        var maxChunks = _options.Value.EffectiveMaxIndexedChunks;

        await foreach (var descriptor in provider.EnumerateAsync(
            new RagSourceRequest(request.RootPath, request.Revision), cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.Limit is int limit && state.SourcesSeen >= limit) break;

            state.SourcesSeen++;
            if (request.DryRun) continue;

            var sourceId = await UpsertSourceAsync(descriptor, domain.Key, now, state, cancellationToken);
            seenSourceIds.Add(sourceId);

            if (state.ChunkTotal > maxChunks)
            {
                throw new InvalidOperationException(
                    $"RAG index for '{domain.Key}' exceeded the configured chunk ceiling ({maxChunks}).");
            }
        }

        // RECONCILIATION IS ONLY EVER A COMPLETE RUN'S CONCLUSION.
        //
        // "I did not see this source" means "it left the snapshot" only if the
        // run could have seen it. A `--limit 10` pass over a complete index saw
        // ten sources and nothing else, and treating the remaining eighteen
        // hundred as deleted removed their memberships — a partial run
        // destroying most of the index it was asked to extend.
        if (request.MayReconcile)
        {
            state.SourcesRemoved = await RemoveDepartedAsync(domain.Key, seenSourceIds, cancellationToken);
        }

        var embedding = await EmbedAsync(domain.Key, request, seenSourceIds, state, cancellationToken);

        _log.LogInformation(
            "rag index: domain={Domain} revision={Revision} partial={Partial} reconciled={Reconciled} "
            + "sources={Sources} chunks={Chunks} embeddings={Embeddings}",
            domain.Key, Short(request.Revision), request.IsPartial, request.MayReconcile,
            state.SourcesSeen, state.ChunksCreated + state.ChunksUpdated, state.EmbeddingsCreated);

        return new RagIndexOutcome(
            domain.Key, request.Revision,
            state.SourcesSeen, state.SourcesCreated, state.SourcesUpdated, state.SourcesUnchanged,
            state.SourcesRemoved,
            state.ChunksCreated, state.ChunksUpdated, state.ChunksRemoved, state.ChunksUnchanged,
            state.EmbeddingsCreated, state.EmbeddingsRemoved, state.VectorsIndexed,
            embedding.ProfileKey, embedding.Reason,
            Partial: request.IsPartial,
            ReconciliationPerformed: request.MayReconcile);
    }

    // ---- sources ------------------------------------------------------------

    /// Resolve the descriptor to a CONTENT row, point this domain's membership
    /// at it, and leave every other domain's membership exactly where it was.
    ///
    /// This is the whole source lifecycle, and it replaces a fail-closed rule
    /// that could not be satisfied. The predecessor kept one row per source key
    /// carrying the revision AND the bytes AND the chunks, so advancing
    /// `nubarca-repository` from A to B rewrote what `product-help` was serving
    /// at A. Refusing that was right; the problem was that Help could not go
    /// first either, so two domains sharing a file could only ever move in one
    /// atomic multi-domain reindex — and a release lifecycle that has no legal
    /// first step is not a lifecycle.
    ///
    /// Content identity is (SourceKey, ContentHash, IndexFormatVersion). The
    /// deadlock dissolves because the common case does not write at all: a file
    /// unchanged between A and B is the SAME content row, so the second domain's
    /// upgrade is one membership revision moving forward and zero chunks and
    /// zero embeddings re-derived.
    ///
    /// When the bytes DID change there are two shapes, and telling them apart is
    /// what keeps reindexing cheap. If this domain is the only one using the row,
    /// it is rewritten IN PLACE, so the ordinal-by-ordinal chunk comparison still
    /// applies and an edit to one paragraph still costs one embedding. Forking a
    /// new content row unconditionally would have been simpler and would have
    /// thrown away every vector of every edited file on every `git pull` — the
    /// exact cost the content hashing exists to avoid. A row ANOTHER domain is
    /// serving is never rewritten: that one forks, and the two rows coexist for
    /// exactly as long as the two domains disagree.
    private async Task<Guid> UpsertSourceAsync(
        RagSourceDescriptor descriptor, string domainKey, DateTime now,
        IndexState state, CancellationToken cancellationToken)
    {
        // Every content row this key has. Usually one; two while a shared source
        // is mid-upgrade.
        var candidates = await _db.RagSources
            .Where(s => s.SourceKey == descriptor.SourceKey)
            .ToListAsync(cancellationToken);

        var exact = candidates.FirstOrDefault(s =>
            s.ContentHash == descriptor.ContentHash
            && s.IndexFormatVersion == RagIndexFormat.Current);

        RagSource source;
        bool rechunk;

        if (exact is not null)
        {
            // Nothing to derive: these bytes, read this way, are already indexed.
            // All that can happen here is this domain's membership adopting them.
            source = exact;
            state.SourcesUnchanged++;

            // Except when the row has no chunks — an indexing run interrupted
            // between the source insert and the chunk insert. Its hash says
            // "already done", so the chunk count is what actually answers whether
            // it is.
            rechunk = !await _db.RagChunks.AnyAsync(
                c => c.SourceId == source.Id, cancellationToken);
        }
        else
        {
            var mine = await ContentRowUsedByAsync(candidates, domainKey, cancellationToken);
            var sharedWithAnotherDomain = mine is not null
                && await _db.RagDomainSources.AnyAsync(
                    m => m.SourceId == mine.Id && m.DomainKey != domainKey, cancellationToken);

            if (mine is not null && !sharedWithAnotherDomain)
            {
                // Ours alone: follow the content forward in place and keep every
                // chunk whose text did not change, with its embedding.
                source = mine;
                state.SourcesUpdated++;
            }
            else
            {
                source = new RagSource { Id = Guid.NewGuid(), CreatedAt = now };
                _db.RagSources.Add(source);
                // CREATED counts a document the index had never seen. UPDATED
                // covers a fork of a key that already existed, which is what an
                // operator reads after the first domain of a shared upgrade.
                if (candidates.Count > 0) state.SourcesUpdated++; else state.SourcesCreated++;
            }

            rechunk = true;
        }

        source.SourceKey = descriptor.SourceKey;
        source.Path = descriptor.Path;
        source.Title = descriptor.Title;
        source.SourceKind = descriptor.SourceKind;
        source.ContentHash = descriptor.ContentHash;
        source.IndexFormatVersion = RagIndexFormat.Current;
        source.Language = descriptor.Language;
        source.CodeLanguage = descriptor.CodeLanguage;

        if (rechunk)
        {
            // The corpus cache signature is built from this stamp, and a source's
            // chunks change only when its content does. A rewrite that left it
            // alone would leave a running web host serving the previous text
            // until it restarted.
            source.UpdatedAt = now;
            await ReplaceChunksAsync(source.Id, descriptor, now, state, cancellationToken);
        }
        else
        {
            state.ChunksUnchanged += await _db.RagChunks
                .CountAsync(c => c.SourceId == source.Id, cancellationToken);
        }

        await UpsertMembershipAsync(source.Id, domainKey, descriptor, now, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);

        // THIS domain's memberships on the key's OTHER content rows are stale
        // the moment it adopts this one — a domain describes one snapshot, so it
        // cannot be using two interpretations of the same document at once.
        // Other domains' memberships are untouched, which is the point.
        var superseded = candidates
            .Where(c => c.Id != source.Id)
            .Select(c => c.Id)
            .ToList();
        if (superseded.Count > 0)
        {
            await ReleaseSupersededAsync(domainKey, superseded, cancellationToken);
        }

        return source.Id;
    }

    /// The content row this domain is currently using for this key, if any.
    ///
    /// This is what makes "rewrite in place" safe to ask about: the question is
    /// never "is some row for this key free", it is "is the row I am already
    /// serving free", and a row another domain also holds is not.
    private async Task<RagSource?> ContentRowUsedByAsync(
        IReadOnlyList<RagSource> candidates, string domainKey, CancellationToken cancellationToken)
    {
        if (candidates.Count == 0) return null;

        var ids = candidates.Select(c => c.Id).ToList();
        var mineId = await _db.RagDomainSources
            .Where(m => m.DomainKey == domainKey && ids.Contains(m.SourceId))
            .Select(m => (Guid?)m.SourceId)
            .FirstOrDefaultAsync(cancellationToken);

        return mineId is null ? null : candidates.First(c => c.Id == mineId.Value);
    }

    /// Drop this domain's membership of superseded content, then delete the
    /// content that nothing points at any more.
    ///
    /// The order is the invariant: a content row survives exactly as long as its
    /// LAST membership. Deleting it when the first domain moves would take the
    /// chunks the other domain is still answering from.
    private async Task ReleaseSupersededAsync(
        string domainKey, IReadOnlyList<Guid> supersededSourceIds, CancellationToken cancellationToken)
    {
        var stale = await _db.RagDomainSources
            .Where(m => m.DomainKey == domainKey && supersededSourceIds.Contains(m.SourceId))
            .ToListAsync(cancellationToken);
        if (stale.Count > 0)
        {
            _db.RagDomainSources.RemoveRange(stale);
            await _db.SaveChangesAsync(cancellationToken);
        }

        await RemoveUnclaimedSourcesAsync(supersededSourceIds, cancellationToken);
    }

    /// Delete source content rows no membership claims. Chunks, embeddings and
    /// vector rows follow by cascade.
    private async Task<int> RemoveUnclaimedSourcesAsync(
        IReadOnlyList<Guid> sourceIds, CancellationToken cancellationToken)
    {
        if (sourceIds.Count == 0) return 0;

        var stillClaimed = await _db.RagDomainSources
            .Where(m => sourceIds.Contains(m.SourceId))
            .Select(m => m.SourceId)
            .Distinct()
            .ToListAsync(cancellationToken);

        var removable = sourceIds.Distinct().Except(stillClaimed).ToList();
        if (removable.Count == 0) return 0;

        var sources = await _db.RagSources
            .Where(s => removable.Contains(s.Id))
            .ToListAsync(cancellationToken);
        _db.RagSources.RemoveRange(sources);
        await _db.SaveChangesAsync(cancellationToken);
        return sources.Count;
    }

    private async Task UpsertMembershipAsync(
        Guid sourceId, string domainKey, RagSourceDescriptor descriptor, DateTime now,
        CancellationToken cancellationToken)
    {
        var metadata = descriptor.DomainMetadata is { Count: > 0 }
            ? JsonSerializer.Serialize(descriptor.DomainMetadata)
            : null;
        var priority = Math.Clamp(descriptor.Priority, 1, 100);

        var membership = await _db.RagDomainSources.FirstOrDefaultAsync(
            m => m.DomainKey == domainKey && m.SourceId == sourceId, cancellationToken);

        if (membership is null)
        {
            _db.RagDomainSources.Add(new RagDomainSource
            {
                Id = Guid.NewGuid(),
                DomainKey = domainKey,
                SourceId = sourceId,
                Revision = descriptor.Revision,
                Priority = priority,
                MetadataJson = metadata,
                CreatedAt = now,
            });
            return;
        }

        if (membership.Revision == descriptor.Revision
            && membership.Priority == priority
            && string.Equals(membership.MetadataJson, metadata, StringComparison.Ordinal))
        {
            return;
        }
        membership.Revision = descriptor.Revision;
        membership.Priority = priority;
        membership.MetadataJson = metadata;
        membership.UpdatedAt = now;
    }

    // ---- chunks -------------------------------------------------------------

    /// Chunks are matched by ORDINAL, not replaced wholesale.
    ///
    /// An edit to one paragraph of a long document usually leaves most of its
    /// chunks byte-identical, and a delete-then-insert would throw away every
    /// embedding to re-derive the same vectors. So each ordinal is compared by
    /// text hash: unchanged keeps its embedding, changed loses it (the old
    /// vector describes text that no longer exists), and surplus ordinals are
    /// deleted with their embeddings and vector rows following by cascade.
    private async Task ReplaceChunksAsync(
        Guid sourceId, RagSourceDescriptor descriptor, DateTime now,
        IndexState state, CancellationToken cancellationToken)
    {
        var drafts = RagChunkers.Chunk(descriptor.Text, descriptor.CodeLanguage);
        var existing = await _db.RagChunks
            .Where(c => c.SourceId == sourceId)
            .OrderBy(c => c.Ordinal)
            .ToListAsync(cancellationToken);
        var byOrdinal = existing.ToDictionary(c => c.Ordinal);

        state.ChunkTotal += drafts.Count;

        foreach (var draft in drafts)
        {
            var textHash = RagHash.Sha256Hex(draft.Text);
            var metadata = draft.Symbols.Count > 0
                ? JsonSerializer.Serialize(new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [RagMetadataKeys.Symbols] = string.Join(' ', draft.Symbols),
                })
                : null;

            if (byOrdinal.TryGetValue(draft.Ordinal, out var chunk))
            {
                if (chunk.TextHash == textHash)
                {
                    state.ChunksUnchanged++;
                    continue;
                }
                chunk.Heading = draft.Heading;
                chunk.Text = draft.Text;
                chunk.TextHash = textHash;
                chunk.Language = descriptor.Language;
                chunk.MetadataJson = metadata;
                state.ChunksUpdated++;
                state.EmbeddingsRemoved += await DropEmbeddingsAsync(chunk.Id, cancellationToken);
                continue;
            }

            _db.RagChunks.Add(new RagChunk
            {
                Id = Guid.NewGuid(),
                SourceId = sourceId,
                Ordinal = draft.Ordinal,
                Heading = draft.Heading,
                Text = draft.Text,
                TextHash = textHash,
                Language = descriptor.Language,
                MetadataJson = metadata,
                CreatedAt = now,
            });
            state.ChunksCreated++;
        }

        var surplus = existing.Where(c => c.Ordinal > drafts.Count).ToList();
        if (surplus.Count > 0)
        {
            _db.RagChunks.RemoveRange(surplus);
            state.ChunksRemoved += surplus.Count;
        }
    }

    private async Task<int> DropEmbeddingsAsync(Guid chunkId, CancellationToken cancellationToken)
    {
        var stale = await _db.RagChunkEmbeddings
            .Where(e => e.ChunkId == chunkId)
            .ToListAsync(cancellationToken);
        if (stale.Count == 0) return 0;
        _db.RagChunkEmbeddings.RemoveRange(stale);
        return stale.Count;
    }

    /// A source that is no longer in the snapshot loses THIS domain's
    /// membership. It is only deleted when nothing else claims it — the same
    /// file can be repository knowledge and approved product help, and dropping
    /// it from one domain must not remove it from the other.
    private async Task<int> RemoveDepartedAsync(
        string domainKey, HashSet<Guid> seen, CancellationToken cancellationToken)
    {
        var memberships = await _db.RagDomainSources
            .Where(m => m.DomainKey == domainKey)
            .ToListAsync(cancellationToken);

        var departed = memberships.Where(m => !seen.Contains(m.SourceId)).ToList();
        if (departed.Count == 0) return 0;

        _db.RagDomainSources.RemoveRange(departed);
        await _db.SaveChangesAsync(cancellationToken);

        await RemoveUnclaimedSourcesAsync(
            departed.Select(m => m.SourceId).Distinct().ToList(), cancellationToken);

        return departed.Count;
    }

    // ---- embeddings ---------------------------------------------------------

    private async Task<(string? ProfileKey, string? Reason)> EmbedAsync(
        string domainKey, RagIndexRequest request, IReadOnlyCollection<Guid> seenSourceIds,
        IndexState state, CancellationToken cancellationToken)
    {
        if (!request.EmbedPassages || request.DryRun) return (null, null);

        // The profile THIS domain embeds with. Indexing and retrieval must
        // resolve identically or a domain would be searched in a space it was
        // never written into — which is not an error anything would report, just
        // cosine distances between two unrelated coordinate systems.
        var resolution = await _embeddings.ResolveAsync(
            new RagDomainKey(domainKey), cancellationToken);
        if (!resolution.IsAvailable) return (null, resolution.Reason);

        var profile = resolution.Profile!;
        var provider = resolution.Provider!;
        var dimension = profile.Dimension!.Value;
        var now = _clock.GetUtcNow().UtcDateTime;
        var seen = seenSourceIds.ToList();

        // Keyset paging over the chunks of this domain that have no embedding
        // for this profile. Never `ToListAsync()` over the corpus: the
        // repository domain is tens of thousands of chunks and each one holds
        // its text.
        var cursor = Guid.Empty;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // A PARTIAL run embeds only the sources it actually saw.
            //
            // `--limit N` caps enumeration, and without this the embedding pass
            // then walked every chunk in the domain anyway — so a command whose
            // whole purpose is a bounded trial run started an hour of inference
            // over the entire corpus. A partial run does partial work
            // everywhere, not just in the half somebody remembered to bound.
            var candidates =
                from chunk in _db.RagChunks.AsNoTracking()
                join membership in _db.RagDomainSources.AsNoTracking()
                    on chunk.SourceId equals membership.SourceId
                where membership.DomainKey == domainKey
                      && chunk.Id > cursor
                      && !_db.RagChunkEmbeddings.Any(
                          e => e.ChunkId == chunk.Id && e.ProfileId == profile.Id)
                select new { chunk.Id, chunk.Text, chunk.SourceId };

            if (request.IsPartial)
            {
                candidates = candidates.Where(c => seen.Contains(c.SourceId));
            }

            var page = await candidates
                .OrderBy(c => c.Id)
                .Take(64)
                .ToListAsync(cancellationToken);

            if (page.Count == 0) break;

            foreach (var row in page)
            {
                cancellationToken.ThrowIfCancellationRequested();
                cursor = row.Id;

                float[] vector;
                try
                {
                    var result = await provider.EmbedAsync(
                        profile, row.Text, TextEmbeddingInputKind.Passage, cancellationToken);
                    vector = result.Vector;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (TextEmbeddingUnavailableException ex)
                {
                    // The model became unavailable mid-run. Stop rather than
                    // continue writing nothing, and report the reason: a
                    // half-embedded corpus is a resumable state, and a silent
                    // one is not.
                    return (profile.Key, ex.ReasonCode);
                }

                if (vector.Length != dimension) return (profile.Key, RagFailureReasons.EmbeddingDimensionUnsupported);

                var embeddingId = Guid.NewGuid();
                _db.RagChunkEmbeddings.Add(new RagChunkEmbedding
                {
                    Id = embeddingId,
                    ChunkId = row.Id,
                    ProfileId = profile.Id,
                    EmbeddingBytes = _serializer.Serialize(vector, dimension),
                    Dimension = dimension,
                    CreatedAt = now,
                });
                await _db.SaveChangesAsync(cancellationToken);
                state.EmbeddingsCreated++;

                // Best-effort: a missing vector row is repaired by a later sync
                // and never invalidates the canonical embedding.
                var upsert = await _vectors.TryUpsertAsync(
                    embeddingId, row.Id, profile.Id, vector, dimension, cancellationToken);
                if (upsert == RagVectorUpsertOutcome.Indexed) state.VectorsIndexed++;
            }
        }

        await _vectors.RemoveOrphanedAsync(profile.Id, cancellationToken);
        return (profile.Key, null);
    }

    /// Revision prefixes only. A full SHA in a log line is fine; a short one is
    /// what an operator compares against `git log`.
    private static string Short(string revision)
        => revision.Length <= 12 ? revision : revision[..12];

    private sealed class IndexState
    {
        public int SourcesSeen;
        public int SourcesCreated;
        public int SourcesUpdated;
        public int SourcesUnchanged;
        public int SourcesRemoved;
        public int ChunksCreated;
        public int ChunksUpdated;
        public int ChunksRemoved;
        public int ChunksUnchanged;
        public int ChunkTotal;
        public int EmbeddingsCreated;
        public int EmbeddingsRemoved;
        public int VectorsIndexed;
    }
}
