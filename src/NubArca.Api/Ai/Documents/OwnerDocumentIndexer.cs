using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NubArca.Api.Ai.TextEmbeddings;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Domain.Ai;
using NubArca.Api.Rag;
using NubArca.Api.Rag.Domains;
using NubArca.Api.Storage;

namespace NubArca.Api.Ai.Documents;

/// What one indexing pass did, in counts only.
///
/// Counts and sanitized reasons. No filenames, no owner id, no text — this is
/// printed by an operator CLI and written to a log, and neither is a place for
/// somebody's document titles.
public sealed record OwnerDocumentIndexOutcome(
    int FilesSeen,
    int Extracted,
    int Unchanged,
    int Chunked,
    int ChunksCreated,
    int ChunksRemoved,
    int EmbeddingsCreated,
    int EmbeddingsRemoved,
    int Skipped,
    IReadOnlyDictionary<string, int> SkipReasons,
    string? EmbeddingProfileKey,
    string? EmbeddingReason,
    bool Partial);

/// Turns one person's eligible documents into their own private corpus.
///
/// The whole pipeline for `user-documents`, and deliberately NOT RagIndexer.
/// That one writes `rag_sources` — installation-wide knowledge with no owner —
/// and forcing a person's documents through it for symmetry would put private
/// text in the table every system domain reads, one forgotten `WHERE` away from
/// a cross-owner leak. Private content lives in `document_texts` /
/// `document_chunks` / `document_chunk_embeddings`, which are owner-scoped by
/// schema. What the two share is the CONTRACTS — chunking, embedding, fusion,
/// evidence, policy — not the storage.
///
/// ORDER MATTERS HERE and is the security property: eligibility is established
/// from the live `FileItem` BEFORE the storage layer is asked for a single byte.
/// A file in the Private Vault, deleted, excluded from the library or belonging
/// to somebody else is not read at all — not read and then discarded.
public sealed class OwnerDocumentIndexer
{
    private readonly AppDbContext _db;
    private readonly IBlobStorage _storage;
    private readonly TextEmbeddingResolver _embeddings;
    private readonly IAiVectorSerializer _serializer;
    private readonly IOptions<DocumentExtractionOptions> _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<OwnerDocumentIndexer> _log;

    public OwnerDocumentIndexer(
        AppDbContext db,
        IBlobStorage storage,
        TextEmbeddingResolver embeddings,
        IAiVectorSerializer serializer,
        IOptions<DocumentExtractionOptions> options,
        TimeProvider clock,
        ILogger<OwnerDocumentIndexer> log)
    {
        _db = db;
        _storage = storage;
        _embeddings = embeddings;
        _serializer = serializer;
        _options = options;
        _clock = clock;
        _log = log;
    }

    public async Task<OwnerDocumentIndexOutcome> IndexOwnerAsync(
        Guid ownerUserId,
        int? limit = null,
        bool embed = false,
        CancellationToken cancellationToken = default)
    {
        if (ownerUserId == Guid.Empty)
        {
            throw new ArgumentException("An owner is required.", nameof(ownerUserId));
        }

        var options = _options.Value;
        var profile = await ExtractionProfileAsync(cancellationToken);
        var state = new IndexState();

        // KEYSET PAGING, never `ToListAsync()` over a library. A person can have
        // a hundred thousand files, and each row carries a name and a path.
        var cursor = Guid.Empty;
        var seenDocumentTextIds = new List<Guid>();

        while (limit is null || state.FilesSeen < limit)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var page = await OwnerDocumentEligibility
                .Extractable(_db.FileItems.AsNoTracking(), ownerUserId)
                .Where(f => f.Id > cursor)
                .OrderBy(f => f.Id)
                .Select(f => new CandidateFile(
                    f.Id, f.BlobObjectId, f.Name, f.MimeType, f.SizeBytes))
                .Take(64)
                .ToListAsync(cancellationToken);

            if (page.Count == 0) break;

            foreach (var file in page)
            {
                cancellationToken.ThrowIfCancellationRequested();
                cursor = file.Id;
                if (limit is int cap && state.FilesSeen >= cap) break;
                state.FilesSeen++;

                var documentTextId = await IndexFileAsync(
                    ownerUserId, file, profile, options, state, cancellationToken);
                if (documentTextId is { } id) seenDocumentTextIds.Add(id);
            }
        }

        var partial = limit is not null;
        var embedding = embed
            ? await EmbedAsync(ownerUserId, seenDocumentTextIds, partial, state, cancellationToken)
            : (null, (string?)null);

        // AGGREGATES ONLY. A count of files and a set of reason tokens; never a
        // name, never a path, never an excerpt.
        _log.LogInformation(
            "user-documents index: files={Files} extracted={Extracted} unchanged={Unchanged} "
            + "chunks={Chunks} embeddings={Embeddings} skipped={Skipped} partial={Partial}",
            state.FilesSeen, state.Extracted, state.Unchanged, state.ChunksCreated,
            state.EmbeddingsCreated, state.Skipped, partial);

        return new OwnerDocumentIndexOutcome(
            state.FilesSeen, state.Extracted, state.Unchanged, state.Chunked,
            state.ChunksCreated, state.ChunksRemoved,
            state.EmbeddingsCreated, state.EmbeddingsRemoved,
            state.Skipped, state.SkipReasons,
            embedding.Item1, embedding.Item2, partial);
    }

    private sealed record CandidateFile(
        Guid Id, Guid BlobObjectId, string Name, string MimeType, long SizeBytes);

    // ---- one document -------------------------------------------------------

    private async Task<Guid?> IndexFileAsync(
        Guid ownerUserId, CandidateFile file, AiProfile profile,
        DocumentExtractionOptions options, IndexState state, CancellationToken cancellationToken)
    {
        // SIZE BEFORE BYTES. `FileItem.SizeBytes` is recorded at upload, so an
        // oversized document is refused without opening the blob at all. A limit
        // enforced after reading is a report, not a bound.
        if (file.SizeBytes > options.EffectiveMaxSourceBytes)
        {
            state.Skip(DocumentExtractionReasons.TooLarge);
            return null;
        }

        var existing = await _db.DocumentTexts
            .FirstOrDefaultAsync(
                d => d.FileItemId == file.Id && d.ProfileId == profile.Id, cancellationToken);

        // NOTHING TO DO, AND NOTHING READ. Same bytes, same reading of them —
        // which is exactly the rename-and-move case, because those are DB-only
        // operations that leave the content-addressed blob alone.
        if (existing is not null
            && existing.SourceBlobObjectId == file.BlobObjectId
            && existing.ChunkFormatVersion == OwnerDocumentChunkFormat.Current
            && existing.Status == AiArtifactStatuses.Completed)
        {
            state.Unchanged++;
            return existing.Id;
        }

        var storageKey = await _db.BlobObjects.AsNoTracking()
            .Where(b => b.Id == file.BlobObjectId)
            .Select(b => b.StorageKey)
            .FirstOrDefaultAsync(cancellationToken);
        if (storageKey is null)
        {
            state.Skip("blob-missing");
            return null;
        }

        byte[] bytes;
        try
        {
            // Bounded read: the stream is copied into a buffer capped at the
            // size gate above plus one byte, so a blob whose recorded size lies
            // cannot become an unbounded allocation.
            await using var stream = await _storage.OpenReadAsync(storageKey, cancellationToken);
            using var buffer = new MemoryStream();
            await CopyBoundedAsync(
                stream, buffer, options.EffectiveMaxSourceBytes + 1, cancellationToken);
            bytes = buffer.ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A storage error is not a content verdict, and it must not mark the
            // document permanently skipped — the next pass tries again.
            state.Skip("unreadable");
            return null;
        }

        var extraction = NativeTextExtractor.Extract(file.MimeType, bytes, options);
        if (!extraction.Ok)
        {
            state.Skip(extraction.Reason!);
            // A CONTENT verdict is recorded so the operator can see why, and so
            // the next pass does not read the same unreadable bytes again. It is
            // stored against the blob that produced it, so replacing the file
            // with something readable re-opens the question.
            await RecordSkipAsync(
                ownerUserId, file, profile, extraction.Reason!, cancellationToken);
            return null;
        }

        var now = _clock.GetUtcNow().UtcDateTime;
        var textHash = RagHash.Sha256Hex(extraction.Text!);
        var chunksAreCurrent = existing is not null
                               && existing.TextHash == textHash
                               && existing.ChunkFormatVersion == OwnerDocumentChunkFormat.Current
                               && existing.Status == AiArtifactStatuses.Completed;

        if (existing is null)
        {
            existing = new DocumentText
            {
                Id = Guid.NewGuid(),
                FileItemId = file.Id,
                OwnerUserId = ownerUserId,
                ProfileId = profile.Id,
                CreatedAt = now,
            };
            _db.DocumentTexts.Add(existing);
        }
        else
        {
            existing.UpdatedAt = now;
        }

        // OWNER IS RE-ASSERTED from the eligibility query on every pass, never
        // carried forward from the row. A file that changed hands would
        // otherwise keep the previous owner's authority in a cache row.
        existing.OwnerUserId = ownerUserId;
        existing.SourceBlobObjectId = file.BlobObjectId;
        existing.Source = DocumentTextSources.Native;
        existing.Status = AiArtifactStatuses.Completed;
        existing.ErrorCode = null;
        existing.TextHash = textHash;
        existing.Text = extraction.Text;
        existing.CharCount = extraction.Text!.Length;
        existing.Language = extraction.Language;
        existing.ChunkFormatVersion = OwnerDocumentChunkFormat.Current;
        state.Extracted++;

        // THIS READING BECOMES THE CURRENT ONE, and any other reading of the
        // same file stops being authority in the same save.
        //
        // Two statements, one transaction, in this order — the demotion has to
        // be visible to the database before the promotion, or the filtered
        // unique index sees two current rows for one file and refuses the write.
        // That refusal is the point: it is the constraint proving there is never
        // a moment when a question could be answered from two interpretations of
        // one document at once.
        await DemoteOtherDerivationsAsync(file.Id, existing.Id, cancellationToken);
        existing.IsCurrent = true;

        await _db.SaveChangesAsync(cancellationToken);

        if (!chunksAreCurrent)
        {
            await ReplaceChunksAsync(
                existing, extraction.Text!, profile, options, state, now, cancellationToken);
            state.Chunked++;
        }

        return existing.Id;
    }

    /// Every other extraction of this file stops being authority.
    ///
    /// Today there is one production extraction profile and this ordinarily
    /// matches nothing. It exists because the rich profiles arriving on top of
    /// it make several readings of one file normal, and the moment a second
    /// profile writes is exactly the moment nobody remembers to add this.
    ///
    /// The rows are kept, not deleted: which profile produced what is the
    /// provenance a later extractor upgrade reads, and losing it would make an
    /// upgrade indistinguishable from a first extraction.
    private async Task DemoteOtherDerivationsAsync(
        Guid fileItemId, Guid keepId, CancellationToken cancellationToken)
    {
        var superseded = await _db.DocumentTexts
            .Where(d => d.FileItemId == fileItemId && d.Id != keepId && d.IsCurrent)
            .ToListAsync(cancellationToken);

        if (superseded.Count == 0) return;

        foreach (var row in superseded)
        {
            row.IsCurrent = false;
        }

        await _db.SaveChangesAsync(cancellationToken);
    }

    /// A permanent CONTENT skip, recorded against the bytes that caused it.
    ///
    /// `skipped` means a content-related permanent skip only — a binary file
    /// will still be binary next week. A storage failure or a missing provider
    /// never reaches here: those are environment states and are counted without
    /// writing a verdict, so a temporarily unreachable disk does not mark a
    /// person's documents unreadable forever.
    private async Task RecordSkipAsync(
        Guid ownerUserId, CandidateFile file, AiProfile profile, string reason,
        CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var row = await _db.DocumentTexts.FirstOrDefaultAsync(
            d => d.FileItemId == file.Id && d.ProfileId == profile.Id, cancellationToken);

        if (row is null)
        {
            row = new DocumentText
            {
                Id = Guid.NewGuid(),
                FileItemId = file.Id,
                OwnerUserId = ownerUserId,
                ProfileId = profile.Id,
                CreatedAt = now,
            };
            _db.DocumentTexts.Add(row);
        }
        else
        {
            row.UpdatedAt = now;
            // Whatever it used to say is not true of these bytes. Chunks and
            // their embeddings go with it, or the corpus would keep answering
            // from a previous version of a file that is now unreadable.
            await RemoveChunksAsync(row.Id, cancellationToken);
        }

        row.OwnerUserId = ownerUserId;
        row.SourceBlobObjectId = file.BlobObjectId;
        row.Source = DocumentTextSources.Native;
        row.Status = AiArtifactStatuses.Skipped;
        row.ErrorCode = reason;
        row.TextHash = null;
        row.Text = null;
        row.CharCount = null;
        row.ChunkFormatVersion = OwnerDocumentChunkFormat.Current;

        // A REFUSAL IS ALSO A READING. "These bytes cannot be read" is what
        // NubArca currently knows about this file, so it becomes the current
        // row and supersedes whatever came before — otherwise an earlier
        // successful extraction of DIFFERENT bytes would stay authoritative and
        // keep answering questions about a file that has since been replaced
        // with something unreadable. The chunks were already removed above; this
        // is the same statement at the level of which row is authority.
        await DemoteOtherDerivationsAsync(file.Id, row.Id, cancellationToken);
        row.IsCurrent = true;

        await _db.SaveChangesAsync(cancellationToken);
    }

    // ---- chunks -------------------------------------------------------------

    /// Chunks matched by ORDINAL, so an edit to one paragraph costs one
    /// embedding rather than all of them.
    ///
    /// Identical in shape to the system indexer's rule, and for the same reason:
    /// unchanged text keeps its vector, changed text loses it because the old
    /// one describes text that no longer exists, and surplus ordinals are
    /// deleted with their embeddings following by cascade.
    private async Task ReplaceChunksAsync(
        DocumentText document, string text, AiProfile profile,
        DocumentExtractionOptions options, IndexState state, DateTime now,
        CancellationToken cancellationToken)
    {
        var drafts = OwnerDocumentChunker.Chunk(text, options);
        var existing = await _db.DocumentChunks
            .Where(c => c.DocumentTextId == document.Id && c.ProfileId == profile.Id)
            .OrderBy(c => c.Ordinal)
            .ToListAsync(cancellationToken);
        var byOrdinal = existing.ToDictionary(c => c.Ordinal);

        foreach (var draft in drafts)
        {
            var hash = RagHash.Sha256Hex(draft.Text);

            if (byOrdinal.TryGetValue(draft.Ordinal, out var chunk))
            {
                if (chunk.TextHash == hash)
                {
                    chunk.OwnerUserId = document.OwnerUserId;
                    continue;
                }
                chunk.Heading = draft.Heading;
                chunk.Text = draft.Text;
                chunk.TextHash = hash;
                chunk.OwnerUserId = document.OwnerUserId;
                chunk.StartOffset = draft.StartOffset >= 0 ? draft.StartOffset : null;
                chunk.EndOffset = draft.EndOffset >= 0 ? draft.EndOffset : null;
                state.ChunksUpdated++;
                state.EmbeddingsRemoved += await DropEmbeddingsAsync(chunk.Id, cancellationToken);
                continue;
            }

            _db.DocumentChunks.Add(new DocumentChunk
            {
                Id = Guid.NewGuid(),
                DocumentTextId = document.Id,
                // Denormalized so every owner-scoped query can filter here
                // without a join, and so a chunk carries its own answer to
                // "whose is this" rather than inheriting one.
                OwnerUserId = document.OwnerUserId,
                ProfileId = profile.Id,
                Ordinal = draft.Ordinal,
                Heading = draft.Heading,
                Text = draft.Text,
                TextHash = hash,
                StartOffset = draft.StartOffset >= 0 ? draft.StartOffset : null,
                EndOffset = draft.EndOffset >= 0 ? draft.EndOffset : null,
                // Native text has no pages. Null rather than 1: a page number
                // that was invented is worse than one that is absent.
                Page = null,
                CreatedAt = now,
            });
            state.ChunksCreated++;
        }

        var surplus = existing.Where(c => c.Ordinal > drafts.Count).ToList();
        if (surplus.Count > 0)
        {
            _db.DocumentChunks.RemoveRange(surplus);
            state.ChunksRemoved += surplus.Count;
        }

        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task RemoveChunksAsync(Guid documentTextId, CancellationToken cancellationToken)
    {
        var chunks = await _db.DocumentChunks
            .Where(c => c.DocumentTextId == documentTextId)
            .ToListAsync(cancellationToken);
        if (chunks.Count == 0) return;
        _db.DocumentChunks.RemoveRange(chunks);
        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task<int> DropEmbeddingsAsync(Guid chunkId, CancellationToken cancellationToken)
    {
        var stale = await _db.DocumentChunkEmbeddings
            .Where(e => e.DocumentChunkId == chunkId)
            .ToListAsync(cancellationToken);
        if (stale.Count == 0) return 0;
        _db.DocumentChunkEmbeddings.RemoveRange(stale);
        return stale.Count;
    }

    // ---- embeddings ---------------------------------------------------------

    /// Local passage embeddings for THIS OWNER's chunks only.
    ///
    /// The profile is resolved for `user-documents`, which never inherits the
    /// installation-wide semantic settings — so a person's documents are
    /// embedded because somebody enabled it for that domain, not because Help
    /// has been semantic since last year.
    private async Task<(string?, string?)> EmbedAsync(
        Guid ownerUserId, IReadOnlyList<Guid> seenDocumentTextIds, bool partial,
        IndexState state, CancellationToken cancellationToken)
    {
        var resolution = await _embeddings.ResolveAsync(
            RagDomainKey.UserDocuments, cancellationToken);
        if (!resolution.IsAvailable) return (null, resolution.Reason);

        var profile = resolution.Profile!;
        var provider = resolution.Provider!;
        var dimension = profile.Dimension!.Value;
        var now = _clock.GetUtcNow().UtcDateTime;
        var seen = seenDocumentTextIds.ToList();

        var cursor = Guid.Empty;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // THE SAME LIVE BOUNDARY RETRIEVAL USES, not the chunk's own owner
            // column.
            //
            // Owner first in the query, so a paging bug cannot page into
            // somebody else's chunks — but owner alone is a derived row reading
            // a derived row. A chunk survives its file: deleting a document,
            // moving it into the Private Vault or dropping it out of the library
            // leaves `document_chunks` intact until housekeeping catches up, and
            // selecting candidates by `OwnerUserId` alone would spend local
            // inference minting FRESH vectors for exactly those rows. Retrieval
            // would still refuse to read them, so nothing would leak — but the
            // indexer would be re-arming stale content on every pass, and a
            // document the owner deleted would keep acquiring new derived data
            // for as long as the sweeper was behind.
            //
            // So embedding joins the live `FileItem` through the same predicate
            // retrieval joins, and a chunk that is not readable does not become
            // embeddable either.
            var candidates = OwnerDocumentEligibility
                .EligibleChunks(
                    _db.DocumentChunks.AsNoTracking(),
                    _db.DocumentTexts.AsNoTracking(),
                    _db.FileItems.AsNoTracking(),
                    ownerUserId)
                .Where(r => r.Chunk.Id > cursor
                            && !_db.DocumentChunkEmbeddings.Any(
                                e => e.DocumentChunkId == r.Chunk.Id && e.ProfileId == profile.Id))
                .Select(c => new { c.Chunk.Id, c.Chunk.Text, c.Chunk.DocumentTextId });

            // A PARTIAL run embeds only what it saw, exactly like the system
            // indexer: a bounded trial run must not start an hour of inference
            // over a whole library.
            if (partial)
            {
                candidates = candidates.Where(c => seen.Contains(c.DocumentTextId));
            }

            var page = await candidates.OrderBy(c => c.Id).Take(32).ToListAsync(cancellationToken);
            if (page.Count == 0) break;

            foreach (var row in page)
            {
                cancellationToken.ThrowIfCancellationRequested();
                cursor = row.Id;
                if (string.IsNullOrEmpty(row.Text)) continue;

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
                    // Resumable: the text is already indexed and lexical
                    // retrieval works on it, the vectors that completed are
                    // kept, and the reason says why it stopped.
                    return (profile.Key, ex.ReasonCode);
                }

                if (vector.Length != dimension)
                {
                    return (profile.Key, RagFailureReasons.EmbeddingDimensionUnsupported);
                }
                // A non-finite component makes every cosine involving this
                // vector meaningless, and nothing downstream would notice.
                if (!vector.All(float.IsFinite))
                {
                    return (profile.Key, RagFailureReasons.EmbeddingDimensionUnsupported);
                }

                _db.DocumentChunkEmbeddings.Add(new DocumentChunkEmbedding
                {
                    Id = Guid.NewGuid(),
                    DocumentChunkId = row.Id,
                    ProfileId = profile.Id,
                    EmbeddingBytes = _serializer.Serialize(vector, dimension),
                    Dimension = dimension,
                    CreatedAt = now,
                });
                await _db.SaveChangesAsync(cancellationToken);
                state.EmbeddingsCreated++;
            }
        }

        return (profile.Key, null);
    }

    // ---- profile ------------------------------------------------------------

    /// The native-text extraction profile, created on first use.
    ///
    /// Extraction runs no model — it is a decoder and a set of refusals — but a
    /// derived row still has to say which INTERPRETATION produced it, and
    /// `DocumentText.ProfileId` is where that is recorded. Giving it a real
    /// profile rather than borrowing the deterministic dev/test one keeps the
    /// rule in CLAUDE.md true: the deterministic backend is not semantically
    /// meaningful and must not appear in a production lineage.
    private async Task<AiProfile> ExtractionProfileAsync(CancellationToken cancellationToken)
    {
        var profile = await _db.AiProfiles
            .FirstOrDefaultAsync(p => p.Key == DocumentTextSources.NativeProfileKey, cancellationToken);
        if (profile is not null) return profile;

        var now = _clock.GetUtcNow().UtcDateTime;
        var model = await _db.AiModels
            .FirstOrDefaultAsync(m => m.Key == DocumentTextSources.NativeModelKey, cancellationToken);
        if (model is null)
        {
            model = new AiModel
            {
                Id = Guid.NewGuid(),
                Key = DocumentTextSources.NativeModelKey,
                // `none`, and that is the honest value: there is no model. The
                // row exists so a profile can point at something, and no backend
                // resolver ever looks it up.
                Provider = AiProviders.None,
                Capability = AiCapabilities.DocumentExtraction,
                Modality = AiModalities.Document,
                Version = OwnerDocumentChunkFormat.Current,
                Enabled = true,
                CreatedAt = now,
            };
            _db.AiModels.Add(model);
        }

        profile = new AiProfile
        {
            Id = Guid.NewGuid(),
            Key = DocumentTextSources.NativeProfileKey,
            AiModelId = model.Id,
            Capability = AiCapabilities.DocumentExtraction,
            Modality = AiModalities.Document,
            // Never the capability default: a future PDF or OCR profile must not
            // become the active extraction by being added.
            IsDefault = false,
            Enabled = true,
            CreatedAt = now,
        };
        _db.AiProfiles.Add(profile);
        await _db.SaveChangesAsync(cancellationToken);
        return profile;
    }

    /// Copy at most `limit` bytes. A recorded size is a claim, and this is what
    /// keeps a wrong one from becoming an unbounded read.
    private static async Task CopyBoundedAsync(
        Stream source, Stream destination, long limit, CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        long total = 0;
        while (total < limit)
        {
            var wanted = (int)Math.Min(buffer.Length, limit - total);
            var read = await source.ReadAsync(buffer.AsMemory(0, wanted), cancellationToken);
            if (read == 0) break;
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            total += read;
        }
    }

    private sealed class IndexState
    {
        public int FilesSeen;
        public int Extracted;
        public int Unchanged;
        public int Chunked;
        public int ChunksCreated;
        public int ChunksUpdated;
        public int ChunksRemoved;
        public int EmbeddingsCreated;
        public int EmbeddingsRemoved;
        public int Skipped;

        private readonly Dictionary<string, int> _skipReasons = new(StringComparer.Ordinal);

        public IReadOnlyDictionary<string, int> SkipReasons => _skipReasons;

        public void Skip(string reason)
        {
            Skipped++;
            _skipReasons[reason] = _skipReasons.GetValueOrDefault(reason) + 1;
        }
    }
}

/// How a DocumentText's text was obtained, and the identity of the native
/// extractor.
public static class DocumentTextSources
{
    /// Text the file already contained. The only source this slice implements —
    /// no PDF, no OCR, no Office.
    public const string Native = "native";

    public const string NativeModelKey = "native-text-extraction-v1";

    public const string NativeProfileKey = "doc-native-text-v1";
}
