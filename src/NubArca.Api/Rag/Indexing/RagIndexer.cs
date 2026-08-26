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

        if (!request.DryRun)
        {
            state.SourcesRemoved = await RemoveDepartedAsync(domain.Key, seenSourceIds, cancellationToken);
        }

        var embedding = await EmbedAsync(domain.Key, request, state, cancellationToken);

        _log.LogInformation(
            "rag index: domain={Domain} revision={Revision} sources={Sources} chunks={Chunks} embeddings={Embeddings}",
            domain.Key, Short(request.Revision), state.SourcesSeen,
            state.ChunksCreated + state.ChunksUpdated, state.EmbeddingsCreated);

        return new RagIndexOutcome(
            domain.Key, request.Revision,
            state.SourcesSeen, state.SourcesCreated, state.SourcesUpdated, state.SourcesUnchanged,
            state.SourcesRemoved,
            state.ChunksCreated, state.ChunksUpdated, state.ChunksRemoved, state.ChunksUnchanged,
            state.EmbeddingsCreated, state.EmbeddingsRemoved, state.VectorsIndexed,
            embedding.ProfileKey, embedding.Reason);
    }

    // ---- sources ------------------------------------------------------------

    private async Task<Guid> UpsertSourceAsync(
        RagSourceDescriptor descriptor, string domainKey, DateTime now,
        IndexState state, CancellationToken cancellationToken)
    {
        var source = await _db.RagSources
            .FirstOrDefaultAsync(s => s.SourceKey == descriptor.SourceKey, cancellationToken);

        var contentChanged = source is null || source.ContentHash != descriptor.ContentHash;

        if (source is null)
        {
            source = new RagSource { Id = Guid.NewGuid(), CreatedAt = now };
            _db.RagSources.Add(source);
            state.SourcesCreated++;
        }
        else if (contentChanged || source.Revision != descriptor.Revision)
        {
            source.UpdatedAt = now;
            if (contentChanged) state.SourcesUpdated++; else state.SourcesUnchanged++;
        }
        else
        {
            state.SourcesUnchanged++;
        }

        source.SourceKey = descriptor.SourceKey;
        source.Path = descriptor.Path;
        source.Title = descriptor.Title;
        source.SourceKind = descriptor.SourceKind;
        source.Revision = descriptor.Revision;
        source.ContentHash = descriptor.ContentHash;
        source.Language = descriptor.Language;
        source.CodeLanguage = descriptor.CodeLanguage;

        await UpsertMembershipAsync(source.Id, domainKey, descriptor, now, cancellationToken);

        if (contentChanged)
        {
            await ReplaceChunksAsync(source.Id, descriptor, now, state, cancellationToken);
        }
        else
        {
            state.ChunksUnchanged += await _db.RagChunks
                .CountAsync(c => c.SourceId == source.Id, cancellationToken);
        }

        await _db.SaveChangesAsync(cancellationToken);
        return source.Id;
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
                Priority = priority,
                MetadataJson = metadata,
                CreatedAt = now,
            });
            return;
        }

        if (membership.Priority == priority
            && string.Equals(membership.MetadataJson, metadata, StringComparison.Ordinal))
        {
            return;
        }
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

        var orphanIds = departed.Select(m => m.SourceId).Distinct().ToList();
        var stillClaimed = await _db.RagDomainSources
            .Where(m => orphanIds.Contains(m.SourceId))
            .Select(m => m.SourceId)
            .Distinct()
            .ToListAsync(cancellationToken);

        var removable = orphanIds.Except(stillClaimed).ToList();
        if (removable.Count > 0)
        {
            var sources = await _db.RagSources
                .Where(s => removable.Contains(s.Id))
                .ToListAsync(cancellationToken);
            _db.RagSources.RemoveRange(sources);
            await _db.SaveChangesAsync(cancellationToken);
        }

        return departed.Count;
    }

    // ---- embeddings ---------------------------------------------------------

    private async Task<(string? ProfileKey, string? Reason)> EmbedAsync(
        string domainKey, RagIndexRequest request, IndexState state, CancellationToken cancellationToken)
    {
        if (!request.EmbedPassages || request.DryRun) return (null, null);

        var resolution = await _embeddings.ResolveAsync(cancellationToken);
        if (!resolution.IsAvailable) return (null, resolution.Reason);

        var profile = resolution.Profile!;
        var provider = resolution.Provider!;
        var dimension = profile.Dimension!.Value;
        var now = _clock.GetUtcNow().UtcDateTime;

        // Keyset paging over the chunks of this domain that have no embedding
        // for this profile. Never `ToListAsync()` over the corpus: the
        // repository domain is tens of thousands of chunks and each one holds
        // its text.
        var cursor = Guid.Empty;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var page = await (
                from chunk in _db.RagChunks.AsNoTracking()
                join membership in _db.RagDomainSources.AsNoTracking()
                    on chunk.SourceId equals membership.SourceId
                where membership.DomainKey == domainKey
                      && chunk.Id > cursor
                      && !_db.RagChunkEmbeddings.Any(
                          e => e.ChunkId == chunk.Id && e.ProfileId == profile.Id)
                orderby chunk.Id
                select new { chunk.Id, chunk.Text })
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
