using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NubArca.Api.Ai.Documents.Sources;
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

/// How indexing ONE source ended, classified for per-phase state.
///
/// The classes are the point: `Skipped` is a permanent content verdict about
/// this revision, `Failed` a retryable failure, `Unavailable` an environment
/// state (a parser, OCR engine or renderer that is not installed) that must
/// never be recorded as either. No names, no text — a reason token at most.
public sealed record OwnerDocumentSourceOutcome(
    OwnerDocumentSourceOutcomeKind Kind,
    string? Reason,
    string Phase,
    Guid? DocumentTextId,
    Guid? ExtractionProfileId)
{
    public static OwnerDocumentSourceOutcome Unchanged(Guid documentTextId, Guid profileId)
        => new(OwnerDocumentSourceOutcomeKind.Unchanged, null, DocumentSourcePhases.Chunk, documentTextId, profileId);

    public static OwnerDocumentSourceOutcome Indexed(Guid documentTextId, Guid profileId)
        => new(OwnerDocumentSourceOutcomeKind.Indexed, null, DocumentSourcePhases.Chunk, documentTextId, profileId);

    public static OwnerDocumentSourceOutcome Skipped(string phase, string reason, Guid profileId)
        => new(OwnerDocumentSourceOutcomeKind.Skipped, reason, phase, null, profileId);

    public static OwnerDocumentSourceOutcome Failed(string reason)
        => new(OwnerDocumentSourceOutcomeKind.Failed, reason, DocumentSourcePhases.Extract, null, null);

    public static OwnerDocumentSourceOutcome Unavailable(string reason)
        => new(OwnerDocumentSourceOutcomeKind.Unavailable, reason, DocumentSourcePhases.Extract, null, null);

    public static OwnerDocumentSourceOutcome NotEligible()
        => new(OwnerDocumentSourceOutcomeKind.NotEligible, null, DocumentSourcePhases.Extract, null, null);

    /// A current, completed reading exists (and its chunks).
    public bool HasReading => Kind is OwnerDocumentSourceOutcomeKind.Unchanged or OwnerDocumentSourceOutcomeKind.Indexed;
}

public enum OwnerDocumentSourceOutcomeKind
{
    Unchanged,
    Indexed,
    Skipped,
    Failed,
    Unavailable,
    NotEligible,
}

/// The embed phase for one reading: complete, stopped to yield, or a sanitized
/// reason the model could not run (an environment state, never a verdict).
public sealed record OwnerDocumentEmbedOutcome(
    bool Completed, bool Stopped, int EmbeddingsCreated, string? Reason);

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
    private readonly DocumentExtractionProviders _providers;
    private readonly IOptions<DocumentExtractionOptions> _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<OwnerDocumentIndexer> _log;
    private readonly FileItemDocumentSource _files;

    public OwnerDocumentIndexer(
        AppDbContext db,
        IBlobStorage storage,
        TextEmbeddingResolver embeddings,
        IAiVectorSerializer serializer,
        DocumentExtractionProviders providers,
        IOptions<DocumentExtractionOptions> options,
        TimeProvider clock,
        ILogger<OwnerDocumentIndexer> log)
    {
        _db = db;
        _storage = storage;
        _embeddings = embeddings;
        _serializer = serializer;
        _providers = providers;
        _options = options;
        _clock = clock;
        _log = log;
        // The `file` kind, built from the same collaborators rather than
        // injected, so the operator command and every existing caller keep
        // their constructor while reading through the source seam.
        _files = new FileItemDocumentSource(db, storage, options);
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

        var state = new IndexState();

        // KEYSET PAGING, never `ToListAsync()` over a library — the source kind
        // pages its own candidates. The operator command indexes the `file`
        // kind, which is the only one this build registers.
        Guid? cursor = null;
        var seenDocumentTextIds = new List<Guid>();

        while (limit is null || state.FilesSeen < limit)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var page = await _files.ListCandidatesAsync(ownerUserId, cursor, 64, cancellationToken);
            if (page.Count == 0) break;

            foreach (var candidate in page)
            {
                cancellationToken.ThrowIfCancellationRequested();
                cursor = candidate.SourceId;
                if (limit is int cap && state.FilesSeen >= cap) break;
                state.FilesSeen++;

                var result = await IndexSourceCoreAsync(
                    _files, ownerUserId, candidate, state, cancellationToken);
                if (result.DocumentTextId is { } id) seenDocumentTextIds.Add(id);
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

    /// Extract and chunk ONE source — the unit the background job works in.
    ///
    /// The same pipeline the operator command runs, one source at a time, with
    /// its outcome CLASSIFIED so the caller can record per-phase state: a
    /// permanent content verdict, a retryable failure and an environment state
    /// are three different things and must never be written as one another.
    public Task<OwnerDocumentSourceOutcome> IndexSourceAsync(
        IOwnerDocumentSource source, Guid ownerUserId, DocumentSourceCandidate candidate,
        CancellationToken cancellationToken = default)
    {
        if (ownerUserId == Guid.Empty)
        {
            throw new ArgumentException("An owner is required.", nameof(ownerUserId));
        }

        return IndexSourceCoreAsync(source, ownerUserId, candidate, new IndexState(), cancellationToken);
    }

    /// Which source a row describes and at which revision, in the shape the
    /// writes below need. For the `file` kind the revision is the blob id and
    /// lives in `SourceBlobObjectId`, exactly as before sources existed.
    private sealed record SourceRef(string Kind, Guid SourceId, string Revision, Guid BlobObjectId)
    {
        public bool IsFile => Kind == DocumentSourceKinds.File;

        public bool Describes(DocumentText row)
            => IsFile ? row.SourceBlobObjectId == BlobObjectId : row.SourceRevision == Revision;

        public void Stamp(DocumentText row)
        {
            if (IsFile)
            {
                row.FileItemId = SourceId;
                row.SourceBlobObjectId = BlobObjectId;
                row.SourceRevision = null;
            }
            else
            {
                row.SourceKind = Kind;
                row.SourceId = SourceId;
                row.SourceRevision = Revision;
            }
        }
    }

    private async Task<OwnerDocumentSourceOutcome> IndexSourceCoreAsync(
        IOwnerDocumentSource source, Guid ownerUserId, DocumentSourceCandidate candidate,
        IndexState state, CancellationToken cancellationToken)
    {
        var options = _options.Value;
        var profile = await ExtractionProfileAsync(cancellationToken);

        var isFile = source.Kind == DocumentSourceKinds.File;
        var blobId = isFile ? FileItemDocumentSource.BlobOf(candidate.RevisionHash) ?? Guid.Empty : Guid.Empty;
        var reference = new SourceRef(source.Kind, candidate.SourceId, candidate.RevisionHash, blobId);

        // THE BYTES CHANGED, SO THE OLD READING IS NOT ABOUT THIS SOURCE ANY MORE.
        //
        // First, before the size gate, before storage is opened, before anything
        // can fail — because every one of the paths below can return without
        // producing a replacement, and each of them would otherwise leave a
        // completed extraction of the PREVIOUS revision standing as the current
        // reading. That is the worst available outcome: the questioner gets a
        // confident answer, drawn from a document that has been replaced, with
        // nothing anywhere reporting a problem. A source with no current row is
        // merely unanswerable, which is recoverable and visible.
        //
        // Only revisions that DISAGREE are demoted. A newer extraction profile
        // reading the same unchanged bytes must not take a valid document
        // offline while it works — that case promotes on success.
        await DemoteSupersededRevisionAsync(reference, cancellationToken);

        // NOTHING TO DO, AND NOTHING READ. Same revision, same reading of it —
        // which is exactly the rename-and-move case, because those are DB-only
        // operations that leave the content-addressed blob alone.
        //
        // Asked of the CURRENT row rather than of one profile's row: a source's
        // reading may belong to any of five parsers, and the current row is the
        // authority on whether it still describes these bytes.
        var existing = await _db.DocumentTexts
            .FirstOrDefaultAsync(
                d => d.SourceKind == reference.Kind && d.SourceId == reference.SourceId && d.IsCurrent,
                cancellationToken);

        // …AND THE ROW MUST BELONG TO THE PARSER THAT OWNS ITS FORMAT TODAY.
        //
        // Decided by IDENTITY, not by time: a newer profile is the one the
        // current code resolves for this format, and comparing ids is exactly
        // that question.
        var currentIsActiveProfile =
            existing is null
            || await IsActiveExtractionProfileAsync(existing, cancellationToken);

        if (existing is not null
            && currentIsActiveProfile
            && reference.Describes(existing)
            && existing.ChunkFormatVersion == OwnerDocumentChunkFormat.Current
            && existing.Status == AiArtifactStatuses.Completed)
        {
            state.Unchanged++;
            return OwnerDocumentSourceOutcome.Unchanged(existing.Id, existing.ProfileId);
        }

        var load = await source.LoadAsync(ownerUserId, candidate, cancellationToken);
        switch (load.Kind)
        {
            case DocumentSourceLoadKind.NotEligible:
                return OwnerDocumentSourceOutcome.NotEligible();

            case DocumentSourceLoadKind.Refused:
                state.Skip(load.Reason!);
                if (load.RecordVerdict)
                {
                    await RecordSkipAsync(ownerUserId, reference, profile, load.Reason!, cancellationToken);
                }
                return load.IsPermanent
                    ? OwnerDocumentSourceOutcome.Skipped(DocumentSourcePhases.Extract, load.Reason!, profile.Id)
                    : OwnerDocumentSourceOutcome.Failed(load.Reason!);
        }

        DocumentExtractionArtifact artifact;
        if (load.Kind == DocumentSourceLoadKind.Sections)
        {
            // ALREADY STRUCTURED. A kind that knows its own sections (an email's
            // headers and body) hands them over with their locators; there is
            // nothing to probe and no parser to run.
            profile = await ProfileForKeyAsync(load.SectionsProfileKey!, cancellationToken);
            artifact = new DocumentExtractionArtifact(load.SectionsSource!, load.Sections!, null);
        }
        else
        {
            var bytes = load.Bytes!;
            var fileName = load.FileName!;
            var mimeType = load.MimeType!;

            // THE BYTES DECIDE THE FORMAT, and the format decides the parser.
            //
            // Neither the declared MIME type nor the extension is consulted here:
            // both were used earlier, to decide whether opening the file was
            // worth doing, and that is the only question they are allowed to
            // answer.
            var probe = NativeTextExtractor.IsSupportedContentType(mimeType)
                ? DocumentProbeResult.Accepted(DocumentFormatKind.NativeText, mimeType)
                : DocumentFormatProbe.Probe(bytes, fileName, options);

            if (!probe.Ok)
            {
                state.Skip(probe.Reason!);
                await RecordSkipAsync(ownerUserId, reference, profile, probe.Reason!, cancellationToken);
                return OwnerDocumentSourceOutcome.Skipped(DocumentSourcePhases.Extract, probe.Reason!, profile.Id);
            }

            var format = probe.Format!.Value;

            // THE PER-FORMAT SOURCE BOUND, checked now that the format is known.
            // 4 MiB is right for a text file and wrong for an Office package,
            // which carries images that contribute nothing to extraction and
            // still count.
            if (bytes.Length > options.SourceBytesFor(format))
            {
                state.Skip(DocumentExtractionReasons.TooLarge);
                await RecordSkipAsync(
                    ownerUserId, reference, profile, DocumentExtractionReasons.TooLarge, cancellationToken);
                return OwnerDocumentSourceOutcome.Skipped(
                    DocumentSourcePhases.Extract, DocumentExtractionReasons.TooLarge, profile.Id);
            }

            // THE PARSER IS RESOLVED, NOT BRANCHED ON.
            var provider = _providers.For(format);
            if (provider is null)
            {
                // No parser compiled in for this family. An installation fact,
                // not a verdict about the document — the next pass tries again.
                state.Skip(DocumentExtractionReasons.UnsupportedDocumentFormat);
                return OwnerDocumentSourceOutcome.Unavailable(DocumentExtractionReasons.UnsupportedDocumentFormat);
            }

            // THE PROFILE FOLLOWS THE PARSER. One lineage per family, so
            // improving the spreadsheet reading does not re-extract every Word
            // document in every library.
            profile = await ProfileForKeyAsync(provider.ProfileKey, cancellationToken);

            var outcome = await provider.ExtractAsync(
                new DocumentExtractionRequest(bytes, fileName, probe.CanonicalMimeType ?? mimeType, options),
                cancellationToken);

            if (!outcome.Ok)
            {
                state.Skip(outcome.Reason!);

                // A PERMANENT verdict is recorded; an environment failure is not.
                // A missing binary or an unloadable native library marking
                // somebody's documents permanently unreadable is a configuration
                // mistake becoming data loss.
                if (!outcome.IsPermanent)
                {
                    return DocumentExtractionReasons.IsRetryableFailure(outcome.Reason!)
                        ? OwnerDocumentSourceOutcome.Failed(outcome.Reason!)
                        : OwnerDocumentSourceOutcome.Unavailable(outcome.Reason!);
                }

                // A CONTENT verdict is recorded so the operator can see why, and
                // stored against the revision that produced it, so replacing the
                // file with something readable re-opens the question.
                await RecordSkipAsync(ownerUserId, reference, profile, outcome.Reason!, cancellationToken);
                return OwnerDocumentSourceOutcome.Skipped(DocumentSourcePhases.Extract, outcome.Reason!, profile.Id);
            }

            artifact = outcome.Artifact!;
        }

        var text = DocumentTextCanonicalizer.Canonicalize(artifact.Blocks);

        // CHUNKING IS DECIDED BEFORE ANYTHING IS PUBLISHED.
        //
        // The chunk ceiling is a completeness bound like every other, so a
        // document that needs more chunks than the bound allows is refused —
        // and it has to be refused HERE, before the row is marked Completed and
        // promoted to current.
        var planned = PlanChunks(artifact.Blocks, options);
        if (!planned.Ok)
        {
            state.Skip(planned.Reason!);
            await RecordSkipAsync(ownerUserId, reference, profile, planned.Reason!, cancellationToken);
            return OwnerDocumentSourceOutcome.Skipped(DocumentSourcePhases.Chunk, planned.Reason!, profile.Id);
        }

        // Re-resolved for the parser's own profile. The early-exit check above
        // used the current row; the row that is written belongs to the lineage
        // that produced it.
        existing = await _db.DocumentTexts
            .FirstOrDefaultAsync(
                d => d.SourceKind == reference.Kind && d.SourceId == reference.SourceId
                     && d.ProfileId == profile.Id,
                cancellationToken);

        var now = _clock.GetUtcNow().UtcDateTime;
        var textHash = RagHash.Sha256Hex(text);
        var chunksAreCurrent = existing is not null
                               && existing.TextHash == textHash
                               && existing.ChunkFormatVersion == OwnerDocumentChunkFormat.Current
                               && existing.Status == AiArtifactStatuses.Completed;

        if (existing is null)
        {
            existing = new DocumentText
            {
                Id = Guid.NewGuid(),
                OwnerUserId = ownerUserId,
                ProfileId = profile.Id,
                CreatedAt = now,
            };
            reference.Stamp(existing);
            _db.DocumentTexts.Add(existing);
        }
        else
        {
            existing.UpdatedAt = now;
        }

        // OWNER IS RE-ASSERTED from the eligibility query on every pass, never
        // carried forward from the row. A source that changed hands would
        // otherwise keep the previous owner's authority in a cache row.
        existing.OwnerUserId = ownerUserId;
        reference.Stamp(existing);
        existing.Source = artifact.Source;
        existing.Status = AiArtifactStatuses.Completed;
        existing.ErrorCode = null;
        existing.TextHash = textHash;
        existing.Text = text;
        existing.CharCount = text.Length;
        existing.Language = artifact.Language;
        existing.ChunkFormatVersion = OwnerDocumentChunkFormat.Current;
        state.Extracted++;

        // THIS READING BECOMES THE CURRENT ONE, and any other reading of the
        // same source stops being authority in the same save.
        //
        // Two statements, one transaction, in this order — the demotion has to
        // be visible to the database before the promotion, or the filtered
        // unique index sees two current rows for one source and refuses the
        // write. That refusal is the point.
        await DemoteOtherDerivationsAsync(reference, existing.Id, cancellationToken);
        existing.IsCurrent = true;

        await _db.SaveChangesAsync(cancellationToken);

        if (!chunksAreCurrent)
        {
            await ReplaceChunksAsync(
                existing, planned.Chunks!, profile, state, now, cancellationToken);
            state.Chunked++;
        }

        return OwnerDocumentSourceOutcome.Indexed(existing.Id, profile.Id);
    }

    /// Does this row belong to the extraction profile the current build would
    /// select for its format?
    ///
    /// True also when the answer cannot be known — a source this build has no
    /// parser for, or a profile row that has gone missing. In both cases there
    /// is nothing better to replace the reading with, and re-extracting on a
    /// guess would take a working document offline to produce the same thing or
    /// worse.
    private async Task<bool> IsActiveExtractionProfileAsync(
        DocumentText row, CancellationToken cancellationToken)
    {
        var format = DocumentTextSources.FormatFor(row.Source);
        if (format is null) return true;

        var provider = _providers.For(format.Value);
        if (provider is null) return true;

        var activeProfileId = await _db.AiProfiles.AsNoTracking()
            .Where(p => p.Key == provider.ProfileKey)
            .Select(p => (Guid?)p.Id)
            .FirstOrDefaultAsync(cancellationToken);

        // NO PROFILE ROW FOR THE ACTIVE KEY IS AN UPGRADE, NOT AN UNKNOWN.
        //
        // The row points at a profile that exists, so if the key this parser
        // records under has no row at all, the row cannot be under that key —
        // it is under an older one, and the newer parser has simply never run
        // here yet. That is precisely the case this check exists to catch, and
        // treating it as "nothing to compare" is what made the upgrade
        // unreachable in production while hand-promoted fixtures passed.
        if (activeProfileId is null) return false;

        return activeProfileId.Value == row.ProfileId;
    }

    /// Decides how a document would chunk, and whether it may be published.
    ///
    /// Called BEFORE the row is marked Completed, because the chunk ceiling is a
    /// completeness bound: a document needing more chunks than the bound allows
    /// is refused, never indexed as its first N chunks.
    ///
    /// NATIVE TEXT KEEPS ITS OWN SPLITTER. It arrives as one block with no
    /// interior structure, and the Markdown chunker knows how to find the
    /// headings inside it — running the structural chunker over it instead would
    /// produce one enormous chunk and lose every section title.
    ///
    /// Everything else is already structured, and the structure is a hard
    /// boundary: a chunk must never span two slides, two sheets or two PDF
    /// pages, because a passage that does cites a place that does not exist.
    private static RichChunkOutcome PlanChunks(
        IReadOnlyList<ExtractedDocumentBlock> blocks, DocumentExtractionOptions options)
    {
        if (blocks.Count == 1 && blocks[0].Locator.Kind == DocumentLocatorKinds.Text)
        {
            var native = OwnerDocumentChunker.Chunk(blocks[0].Text, options);

            // ONE CEILING, ONE PLACE. The native chunker no longer caps itself,
            // so a text document past the bound is refused on exactly the same
            // terms as a spreadsheet past it.
            if (native.Count > options.EffectiveMaxChunks)
            {
                return RichChunkOutcome.Rejected(DocumentExtractionReasons.DocumentTooComplex);
            }

            return RichChunkOutcome.Chunked(native
                .Select(d => new RichChunkDraft(
                    d.Ordinal, d.Heading, d.Text, d.StartOffset, d.EndOffset,
                    DocumentLocator.None))
                .ToList());
        }

        return RichDocumentChunker.Chunk(blocks, options);
    }

    /// Writes a chunk's typed location, and `Page` only where a page exists.
    ///
    /// The one rule worth stating: `Page` is a real PDF page. A slide number, a
    /// sheet ordinal or a Word pseudo-page in that column would render as
    /// "Page 4" in a citation for a document that has no pages, and a later
    /// reader cannot tell the difference without joining back to discover the
    /// format. The locator carries those; `Page` stays null.
    private static void ApplyLocator(DocumentChunk chunk, DocumentLocator locator)
    {
        chunk.LocatorKind = locator.Kind == DocumentLocatorKinds.Text ? null : locator.Kind;
        chunk.LocatorIndex = locator.Index;
        chunk.LocatorLabel = Truncate(locator.Label, 512);
        chunk.Page = locator.Page;
    }

    private static string? Truncate(string? value, int max)
        => value is null ? null : value.Length <= max ? value : value[..max];

    /// The extraction profile for one parser, created on first use.
    ///
    /// Per family, never shared. The profile is the lineage a re-extraction is
    /// keyed by, so one profile across every rich format would make an
    /// improvement to the spreadsheet reading re-extract every Word document in
    /// every library — the cost of a change landing on people it did not affect.
    private async Task<AiProfile> ProfileForKeyAsync(
        string key, CancellationToken cancellationToken)
    {
        if (key == DocumentTextSources.NativeProfileKey)
        {
            return await ExtractionProfileAsync(cancellationToken);
        }

        var profile = await _db.AiProfiles
            .FirstOrDefaultAsync(p => p.Key == key, cancellationToken);
        if (profile is not null) return profile;

        var modelKey = key + "-model";
        var model = await _db.AiModels.FirstOrDefaultAsync(m => m.Key == modelKey, cancellationToken);
        if (model is null)
        {
            model = new AiModel
            {
                Id = Guid.NewGuid(),
                Key = modelKey,
                // No inference model is involved in Office parsing; PDF may
                // invoke OCR, and that provider is named on the OCR profile
                // rather than smuggled in here.
                Provider = AiProviders.None,
                Capability = AiCapabilities.DocumentExtraction,
                Modality = AiModalities.Text,
                Enabled = true,
                CreatedAt = _clock.GetUtcNow().UtcDateTime,
            };
            _db.AiModels.Add(model);
        }

        profile = new AiProfile
        {
            Id = Guid.NewGuid(),
            Key = key,
            AiModelId = model.Id,
            Capability = AiCapabilities.DocumentExtraction,
            Modality = AiModalities.Text,
            Enabled = true,
            CreatedAt = _clock.GetUtcNow().UtcDateTime,
        };
        _db.AiProfiles.Add(profile);
        await _db.SaveChangesAsync(cancellationToken);
        return profile;
    }

    /// A current reading of bytes the file no longer has stops being authority.
    ///
    /// Deliberately narrow: it demotes only rows whose recorded blob DISAGREES
    /// with the file's current one. Blobs are content-addressed and immutable,
    /// so "different id" is exactly "different content" — there is no heuristic
    /// here and no timestamp involved.
    ///
    /// Called before any work that can fail, which is the whole point. The
    /// spec's rule is that once a FileItem points at new bytes, the old
    /// extraction must stop being authoritative for the file, and it must stop
    /// BEFORE the replacement is attempted rather than after it succeeds —
    /// otherwise a parse that never finishes leaves the previous document
    /// answering questions about a file that no longer contains it.
    private async Task DemoteSupersededRevisionAsync(
        SourceRef source, CancellationToken cancellationToken)
    {
        var current = _db.DocumentTexts
            .Where(d => d.SourceKind == source.Kind && d.SourceId == source.SourceId && d.IsCurrent);
        var stale = await (source.IsFile
                ? current.Where(d => d.SourceBlobObjectId != source.BlobObjectId)
                : current.Where(d => d.SourceRevision != source.Revision))
            .ToListAsync(cancellationToken);

        if (stale.Count == 0) return;

        foreach (var row in stale)
        {
            row.IsCurrent = false;
        }

        // THE CHUNKS STAY. Demotion is a statement about authority, not about
        // content, and `IsCurrent` is exactly what makes them unreachable — the
        // shared boundary requires it, so a demoted row's chunks are already
        // inert without deleting anything.
        //
        // Deleting them here would also be expensive in a way that is easy to
        // miss: when this same row is re-completed for the new bytes,
        // ReplaceChunksAsync compares ordinal by ordinal and drops only the
        // chunks that actually changed, so editing one paragraph costs one
        // embedding. Clearing them first would throw that away and make every
        // content edit re-embed the whole document.
        await _db.SaveChangesAsync(cancellationToken);
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
        SourceRef source, Guid keepId, CancellationToken cancellationToken)
    {
        var superseded = await _db.DocumentTexts
            .Where(d => d.SourceKind == source.Kind && d.SourceId == source.SourceId
                        && d.Id != keepId && d.IsCurrent)
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
        Guid ownerUserId, SourceRef source, AiProfile profile, string reason,
        CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var row = await _db.DocumentTexts.FirstOrDefaultAsync(
            d => d.SourceKind == source.Kind && d.SourceId == source.SourceId && d.ProfileId == profile.Id,
            cancellationToken);

        if (row is null)
        {
            row = new DocumentText
            {
                Id = Guid.NewGuid(),
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
        source.Stamp(row);
        row.Source = DocumentTextSources.Native;
        row.Status = AiArtifactStatuses.Skipped;
        row.ErrorCode = reason;
        row.TextHash = null;
        row.Text = null;
        row.CharCount = null;
        row.ChunkFormatVersion = OwnerDocumentChunkFormat.Current;

        // A REFUSAL IS A READING, BUT IT DOES NOT OUTRANK A WORKING ONE.
        //
        // Which of the two this is depends entirely on whose bytes were
        // refused. If the file's blob changed, the previous reading was already
        // demoted on the way in, nothing else describes these bytes, and this
        // verdict is what NubArca currently knows about the file — it becomes
        // current, and the chunks removed above go with it.
        //
        // If the blob did NOT change, this is a second extractor refusing a
        // document that another profile is reading perfectly well. Promoting
        // here would let a newly-added parser that cannot handle a format
        // silently withdraw a working document from its owner's corpus — the
        // upgrade would look like data loss. So the skip is recorded as
        // provenance and the working reading keeps its authority.
        var otherCurrent = _db.DocumentTexts.Where(
            d => d.SourceKind == source.Kind
                 && d.SourceId == source.SourceId
                 && d.Id != row.Id
                 && d.IsCurrent
                 && d.Status == AiArtifactStatuses.Completed);
        var readableElsewhere = await (source.IsFile
                ? otherCurrent.Where(d => d.SourceBlobObjectId == source.BlobObjectId)
                : otherCurrent.Where(d => d.SourceRevision == source.Revision))
            .AnyAsync(cancellationToken);

        if (!readableElsewhere)
        {
            await DemoteOtherDerivationsAsync(source, row.Id, cancellationToken);
            row.IsCurrent = true;
        }

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
        DocumentText document, IReadOnlyList<RichChunkDraft> drafts, AiProfile profile,
        IndexState state, DateTime now,
        CancellationToken cancellationToken)
    {
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
                ApplyLocator(chunk, draft.Locator);
                state.ChunksUpdated++;
                state.EmbeddingsRemoved += await DropEmbeddingsAsync(chunk.Id, cancellationToken);
                continue;
            }

            var created = new DocumentChunk
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
                CreatedAt = now,
            };
            ApplyLocator(created, draft.Locator);
            _db.DocumentChunks.Add(created);
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

        // A PARTIAL run embeds only what it saw, exactly like the system
        // indexer: a bounded trial run must not start an hour of inference over
        // a whole library.
        var (reason, _) = await EmbedCandidatesAsync(
            ownerUserId, resolution, partial ? seenDocumentTextIds : null, shouldStop: null,
            state, cancellationToken);
        return (resolution.Profile!.Key, reason);
    }

    /// Passage embeddings for ONE reading's eligible chunks that lack one for
    /// the resolved profile — the background job's embed phase.
    ///
    /// `shouldStop` is polled between chunks, AFTER each vector is saved, so a
    /// long document yields to foreground work at chunk granularity and the
    /// next slice resumes exactly where this one stopped (embedded chunks drop
    /// out of the candidate query on their own).
    public async Task<OwnerDocumentEmbedOutcome> EmbedDocumentAsync(
        Guid ownerUserId, Guid documentTextId, TextEmbeddingResolution resolution,
        Func<bool>? shouldStop = null, CancellationToken cancellationToken = default)
    {
        if (!resolution.IsAvailable)
        {
            return new OwnerDocumentEmbedOutcome(false, false, 0, resolution.Reason);
        }

        var state = new IndexState();
        var (reason, stopped) = await EmbedCandidatesAsync(
            ownerUserId, resolution, new[] { documentTextId }, shouldStop, state, cancellationToken);
        return new OwnerDocumentEmbedOutcome(
            reason is null && !stopped, stopped, state.EmbeddingsCreated, reason);
    }

    private async Task<(string? Reason, bool Stopped)> EmbedCandidatesAsync(
        Guid ownerUserId, TextEmbeddingResolution resolution, IReadOnlyList<Guid>? onlyDocumentTextIds,
        Func<bool>? shouldStop, IndexState state, CancellationToken cancellationToken)
    {
        var profile = resolution.Profile!;
        var provider = resolution.Provider!;
        var dimension = profile.Dimension!.Value;
        var now = _clock.GetUtcNow().UtcDateTime;
        var seen = onlyDocumentTextIds?.ToList();

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

            if (seen is not null)
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
                    return (ex.ReasonCode, false);
                }

                if (vector.Length != dimension)
                {
                    return (RagFailureReasons.EmbeddingDimensionUnsupported, false);
                }
                // A non-finite component makes every cosine involving this
                // vector meaningless, and nothing downstream would notice.
                if (!vector.All(float.IsFinite))
                {
                    return (RagFailureReasons.EmbeddingDimensionUnsupported, false);
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

                if (shouldStop?.Invoke() == true) return (null, true);
            }
        }

        return (null, false);
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

    // ---- rich ingestion ------------------------------------------------------
    //
    // One profile per FAMILY, not one for "rich documents". They are separate so
    // that changing how spreadsheets are read does not force every Word document
    // in every library to be extracted again — the profile is the lineage a
    // re-extraction is keyed by, and a shared one would make each parser's
    // improvements everybody else's cost.

    public const string Word = "docx";
    public const string WordModelKey = "openxml-word-extraction-v1";
    public const string WordProfileKey = "doc-openxml-word-v1";

    public const string Spreadsheet = "xlsx";
    public const string SpreadsheetModelKey = "openxml-spreadsheet-extraction-v1";
    public const string SpreadsheetProfileKey = "doc-openxml-spreadsheet-v1";

    public const string Presentation = "pptx";
    public const string PresentationModelKey = "openxml-presentation-extraction-v1";
    public const string PresentationProfileKey = "doc-openxml-presentation-v1";

    public const string Pdf = "pdf";
    public const string PdfModelKey = "pdf-local-ocr-extraction-v1";
    public const string PdfProfileKey = "doc-pdf-local-ocr-v1";

    /// Which FAMILY a recorded reading came from.
    ///
    /// The inverse of the parser's own `Format`, and it exists so a stored row
    /// can be matched back to the parser that owns its format TODAY — which is
    /// how an extractor upgrade is noticed without consulting a timestamp.
    /// An unrecognised source yields null: an installation may carry rows from
    /// a reading this build no longer has a parser for, and that is a row to
    /// leave alone rather than to re-extract with something else.
    public static DocumentFormatKind? FormatFor(string? source) => source switch
    {
        Native => DocumentFormatKind.NativeText,
        Word => DocumentFormatKind.WordOpenXml,
        Spreadsheet => DocumentFormatKind.SpreadsheetOpenXml,
        Presentation => DocumentFormatKind.PresentationOpenXml,
        Pdf => DocumentFormatKind.Pdf,
        _ => null,
    };
}
