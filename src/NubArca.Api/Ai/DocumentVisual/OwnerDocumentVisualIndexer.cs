using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NubArca.Api.Ai.Documents;
using NubArca.Api.Data;
using NubArca.Api.Domain.Ai;
using NubArca.Api.Storage;

namespace NubArca.Api.Ai.DocumentVisual;

/// What one visual indexing pass did, in counts only.
///
/// Counts and sanitized reason tokens. No filenames, no owner id, no page
/// content — this is printed by an operator CLI and written to a log, and
/// neither is a place for somebody's documents.
public sealed record OwnerDocumentVisualIndexOutcome(
    int FilesSeen,
    int Indexed,
    int Unchanged,
    int UnitsRendered,
    int UnitsEmbedded,
    int Skipped,
    IReadOnlyDictionary<string, int> SkipReasons,
    string? ProfileKey,
    string? Reason,
    bool Partial);

/// Turns one person's eligible documents into their own private VISUAL corpus.
///
/// The text indexer's sibling, and deliberately a separate class: the two
/// derivatives fail independently, and a document whose visual pass cannot run
/// must remain fully answerable from its text. Nothing here ever writes,
/// demotes or invalidates a `DocumentText`.
///
/// ORDER MATTERS HERE AND IS THE SECURITY PROPERTY, exactly as it is for text:
/// eligibility is established from the live `FileItem` BEFORE the storage layer
/// is asked for a single byte. A file in the Private Vault, deleted, excluded
/// from the library or belonging to somebody else is not read at all — not read
/// and then discarded.
///
/// AND PUBLICATION IS ALL OR NOTHING. Pages are rendered and embedded one at a
/// time, so memory holds one image; but no row reaches the database until every
/// required unit has succeeded, and then all of them arrive in one
/// `SaveChangesAsync` together with the `Completed` index. There is no code path
/// that writes some units and marks the index done, because the failure that
/// produces — a twenty-page contract searchable as eighteen pages, with nothing
/// saying so — is invisible to the person it misleads.
public sealed class OwnerDocumentVisualIndexer
{
    private readonly AppDbContext _db;
    private readonly IBlobStorage _storage;
    private readonly DocumentVisualRenderers _renderers;
    private readonly DocumentVisualProfileResolver _profiles;
    private readonly IAiVectorSerializer _serializer;
    private readonly IOptions<DocumentVisualOptions> _visual;
    private readonly IOptions<DocumentExtractionOptions> _extraction;
    private readonly TimeProvider _clock;
    private readonly ILogger<OwnerDocumentVisualIndexer> _log;

    public OwnerDocumentVisualIndexer(
        AppDbContext db,
        IBlobStorage storage,
        DocumentVisualRenderers renderers,
        DocumentVisualProfileResolver profiles,
        IAiVectorSerializer serializer,
        IOptions<DocumentVisualOptions> visual,
        IOptions<DocumentExtractionOptions> extraction,
        TimeProvider clock,
        ILogger<OwnerDocumentVisualIndexer> log)
    {
        _db = db;
        _storage = storage;
        _renderers = renderers;
        _profiles = profiles;
        _serializer = serializer;
        _visual = visual;
        _extraction = extraction;
        _clock = clock;
        _log = log;
    }

    public async Task<OwnerDocumentVisualIndexOutcome> IndexOwnerAsync(
        Guid ownerUserId, int? limit = null, CancellationToken cancellationToken = default)
    {
        if (ownerUserId == Guid.Empty)
        {
            throw new ArgumentException("An owner is required.", nameof(ownerUserId));
        }

        var state = new VisualIndexState();

        var resolution = await _profiles.ResolveAsync(cancellationToken);
        if (!resolution.IsAvailable)
        {
            // NOT A CONTENT VERDICT. A disabled capability or a model that is
            // not on disk is an environment state; writing it against people's
            // documents would mark them permanently unrenderable because a
            // config key was unset.
            return new OwnerDocumentVisualIndexOutcome(
                0, 0, 0, 0, 0, 0, state.SkipReasons, null, resolution.Reason, false);
        }

        var profile = resolution.Profile!;
        var options = _visual.Value;

        // KEYSET PAGING, never `ToListAsync()` over a library. A person can have
        // a hundred thousand files and each row carries a name and a path.
        var cursor = Guid.Empty;

        while (limit is null || state.FilesSeen < limit)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var page = await (
                from candidate in OwnerDocumentEligibility.Eligible(
                    _db.FileItems.AsNoTracking(), ownerUserId)
                join document in _db.DocumentTexts.AsNoTracking()
                    on candidate.Id equals document.FileItemId
                where candidate.Id > cursor
                      // THE CURRENT, COMPLETED READING OF THESE EXACT BYTES.
                      //
                      // A visual index is a second derivative of a document
                      // NubArca has already decided it can read. Requiring the
                      // text side to be current and complete keeps the two in
                      // step: the candidate expansion this feeds only means
                      // anything if the file has eligible text chunks to scope
                      // retrieval to, and rendering a document whose extraction
                      // was superseded would index pixels for a reading that is
                      // no longer authority.
                      && document.IsCurrent
                      && document.Status == AiArtifactStatuses.Completed
                      && document.OwnerUserId == ownerUserId
                      && document.SourceBlobObjectId == candidate.BlobObjectId
                orderby candidate.Id
                select new VisualCandidate(
                    candidate.Id, candidate.BlobObjectId, candidate.SizeBytes, document.Source))
                .Take(32)
                .ToListAsync(cancellationToken);

            if (page.Count == 0) break;

            foreach (var file in page)
            {
                cancellationToken.ThrowIfCancellationRequested();
                cursor = file.FileItemId;
                if (limit is int cap && state.FilesSeen >= cap) break;
                state.FilesSeen++;

                await IndexFileAsync(
                    ownerUserId, file, profile, resolution, options, state, cancellationToken);
            }
        }

        var partial = limit is not null;

        // AGGREGATES ONLY. Counts and reason tokens; never a name, never a path,
        // never a rendered page.
        _log.LogInformation(
            "document-visual index: files={Files} indexed={Indexed} unchanged={Unchanged} "
            + "units={Units} skipped={Skipped} partial={Partial}",
            state.FilesSeen, state.Indexed, state.Unchanged, state.UnitsEmbedded,
            state.Skipped, partial);

        return new OwnerDocumentVisualIndexOutcome(
            state.FilesSeen, state.Indexed, state.Unchanged, state.UnitsRendered,
            state.UnitsEmbedded, state.Skipped, state.SkipReasons, profile.Key, null, partial);
    }

    private sealed record VisualCandidate(
        Guid FileItemId, Guid BlobObjectId, long SizeBytes, string Source);

    // ---- one document -------------------------------------------------------

    private async Task IndexFileAsync(
        Guid ownerUserId,
        VisualCandidate file,
        AiProfile profile,
        DocumentVisualProfileResolution resolution,
        DocumentVisualOptions options,
        VisualIndexState state,
        CancellationToken cancellationToken)
    {
        // THE FORMAT COMES FROM THE EXTRACTION THAT ALREADY READ THESE BYTES.
        //
        // Not from the MIME type and not from the extension: the text pipeline
        // already probed the content and recorded what it turned out to be, and
        // re-deciding here would be a second opinion that can disagree.
        var format = DocumentTextSources.FormatFor(file.Source);
        if (format is not { } kind)
        {
            state.Skip(DocumentVisualReasons.FormatUnsupported);
            return;
        }

        var renderer = _renderers.For(kind);
        if (renderer is null)
        {
            // No renderer for this family on this installation — the Office
            // worker is not deployed, most likely. A skip with a reason, not a
            // failure, and the document keeps its text answers.
            state.Skip(DocumentVisualReasons.FormatUnsupported);
            return;
        }

        // IDEMPOTENCE, on all four parts of the index's identity.
        //
        // Same bytes + same render identity + same visual profile means there is
        // nothing to do, which is also the rename-and-move case: those are
        // DB-only operations that leave the content-addressed blob alone, so the
        // pixels and the vectors are still correct and re-deriving them would be
        // hours of local inference bought by renaming a folder.
        var existing = await _db.DocumentVisualIndexes
            .FirstOrDefaultAsync(
                i => i.FileItemId == file.FileItemId
                     && i.SourceBlobObjectId == file.BlobObjectId
                     && i.RenderProfileKey == renderer.RenderProfileKey
                     && i.EmbeddingProfileId == profile.Id,
                cancellationToken);

        if (existing is { Status: AiArtifactStatuses.Completed })
        {
            state.Unchanged++;
            return;
        }

        // A PERMANENT VERDICT IS NOT RETRIED. The same bytes earn the same
        // answer, and re-rendering a document that is too complex every pass is
        // a loop that costs CPU to reach the same refusal.
        if (existing is { Status: AiArtifactStatuses.Skipped, ErrorCode: { } recorded }
            && DocumentVisualReasons.IsPermanent(recorded))
        {
            state.Unchanged++;
            return;
        }

        var readiness = renderer.CheckReadiness();
        if (!readiness.Ready)
        {
            state.Skip(readiness.Reason ?? DocumentVisualReasons.RendererUnavailable);
            return;
        }

        // SIZE BEFORE BYTES, from the recorded size, so an oversized document is
        // refused without opening the blob. The same per-format budget rich
        // extraction uses — a renderer reads the same package an extractor does.
        var budget = _extraction.Value.SourceBytesFor(kind);
        if (file.SizeBytes > budget)
        {
            state.Skip(DocumentVisualReasons.DocumentTooComplex);
            return;
        }

        var storageKey = await _db.BlobObjects.AsNoTracking()
            .Where(b => b.Id == file.BlobObjectId)
            .Select(b => b.StorageKey)
            .FirstOrDefaultAsync(cancellationToken);
        if (storageKey is null)
        {
            state.Skip("blob-missing");
            return;
        }

        byte[] bytes;
        try
        {
            await using var stream = await _storage.OpenReadAsync(storageKey, cancellationToken);
            using var buffer = new MemoryStream();
            await CopyBoundedAsync(stream, buffer, (long)budget + 1, cancellationToken);
            bytes = buffer.ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A storage error is not a content verdict; the next pass retries.
            state.Skip("unreadable");
            return;
        }

        if (bytes.Length > budget)
        {
            state.Skip(DocumentVisualReasons.DocumentTooComplex);
            return;
        }

        var render = await renderer.RenderAsync(
            new DocumentVisualRenderRequest(bytes, kind, options), cancellationToken);

        if (!render.Ok)
        {
            state.Skip(render.Reason!);
            if (render.IsPermanent)
            {
                await RecordSkipAsync(
                    ownerUserId, file, profile, renderer.RenderProfileKey, render.Reason!,
                    existing, cancellationToken);
            }
            return;
        }

        var units = render.Artifact!.Units;
        if (units.Count == 0)
        {
            // A renderer that produced nothing is not a complete reading of a
            // document. Refused rather than published as an empty index — which
            // the database would refuse anyway.
            state.Skip(DocumentVisualReasons.InvalidSource);
            await RecordSkipAsync(
                ownerUserId, file, profile, renderer.RenderProfileKey,
                DocumentVisualReasons.InvalidSource, existing, cancellationToken);
            return;
        }

        state.UnitsRendered += units.Count;

        var now = _clock.GetUtcNow().UtcDateTime;
        var indexId = existing?.Id ?? Guid.NewGuid();
        var staged = new List<(DocumentVisualUnit Unit, DocumentVisualEmbedding Embedding)>(units.Count);

        foreach (var unit in units)
        {
            cancellationToken.ThrowIfCancellationRequested();

            float[] vector;
            try
            {
                var embedded = await resolution.Pages!.EmbedImageAsync(
                    unit.Png, profile, cancellationToken);
                vector = embedded.Vector;
            }
            catch (OperationCanceledException)
            {
                // CANCELLATION IS NOT A FAILURE. Nothing is written, nothing is
                // recorded against the document, and the next pass starts over.
                throw;
            }
            catch (Exception)
            {
                // A model failure is an ENVIRONMENT state. The whole document is
                // abandoned — not published without this page — and no permanent
                // verdict is recorded.
                state.Skip(DocumentVisualReasons.ModelUnavailable);
                return;
            }

            if (vector.Length != profile.Dimension || !vector.All(float.IsFinite))
            {
                state.Skip(DocumentVisualReasons.ModelOutputUnsupported);
                return;
            }

            var row = new DocumentVisualUnit
            {
                Id = Guid.NewGuid(),
                DocumentVisualIndexId = indexId,
                Ordinal = unit.Ordinal,
                RenderKind = unit.RenderKind,
                SourceLocatorKind = unit.SourceLocator?.Kind,
                SourceLocatorIndex = unit.SourceLocator?.Index,
                SourceLocatorLabel = Truncate(unit.SourceLocator?.Label, 200),
                SourcePage = unit.SourcePage,
                Width = unit.Width,
                Height = unit.Height,
                PixelHash = Convert.ToHexString(SHA256.HashData(unit.Png)).ToLowerInvariant(),
                CreatedAt = now,
            };

            staged.Add((row, new DocumentVisualEmbedding
            {
                Id = Guid.NewGuid(),
                DocumentVisualUnitId = row.Id,
                ProfileId = profile.Id,
                Layout = DocumentVisualEmbeddingLayouts.Dense,
                Dimension = profile.Dimension!.Value,
                VectorCount = 1,
                EmbeddingBytes = _serializer.Serialize(vector, profile.Dimension.Value),
                CreatedAt = now,
            }));

            // THE IMAGE IS GONE NOW. Render, embed, discard — the rendered page
            // never reaches disk, a cache, an API or a log. One unit is held at
            // a time, so a four-hundred-page document costs one page of memory.
        }

        // ---- publication, all at once ---------------------------------------

        if (existing is not null)
        {
            // A previous attempt's rows for this exact identity are replaced
            // wholesale. Deleting them first is what keeps a shorter re-render
            // from leaving orphaned high ordinals behind.
            await _db.DocumentVisualUnits
                .Where(u => u.DocumentVisualIndexId == existing.Id)
                .ExecuteDeleteAsync(cancellationToken);

            existing.OwnerUserId = ownerUserId;
            existing.Status = AiArtifactStatuses.Completed;
            existing.ErrorCode = null;
            existing.UnitCount = staged.Count;
            existing.UpdatedAt = now;
            existing.CompletedAt = now;
        }
        else
        {
            _db.DocumentVisualIndexes.Add(new DocumentVisualIndex
            {
                Id = indexId,
                FileItemId = file.FileItemId,
                OwnerUserId = ownerUserId,
                SourceBlobObjectId = file.BlobObjectId,
                RenderProfileKey = renderer.RenderProfileKey,
                EmbeddingProfileId = profile.Id,
                Status = AiArtifactStatuses.Completed,
                UnitCount = staged.Count,
                CreatedAt = now,
                CompletedAt = now,
            });
        }

        foreach (var (unit, embedding) in staged)
        {
            _db.DocumentVisualUnits.Add(unit);
            _db.DocumentVisualEmbeddings.Add(embedding);
        }

        // ONE SAVE. The index becomes `Completed` in the same write that creates
        // its units, so no reader can ever observe a completed index with a
        // missing page.
        await _db.SaveChangesAsync(cancellationToken);

        state.Indexed++;
        state.UnitsEmbedded += staged.Count;
    }

    /// A PERMANENT verdict about these bytes, recorded so the next pass does not
    /// spend the same CPU reaching the same refusal.
    ///
    /// Only ever called for a content verdict. An environment reason — no
    /// worker, no model, a timeout — is counted and forgotten, because writing
    /// it here would mark somebody's document unrenderable for as long as the
    /// row survives, which is longer than the outage that caused it.
    private async Task RecordSkipAsync(
        Guid ownerUserId,
        VisualCandidate file,
        AiProfile profile,
        string renderProfileKey,
        string reason,
        DocumentVisualIndex? existing,
        CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow().UtcDateTime;

        if (existing is not null)
        {
            await _db.DocumentVisualUnits
                .Where(u => u.DocumentVisualIndexId == existing.Id)
                .ExecuteDeleteAsync(cancellationToken);

            existing.Status = AiArtifactStatuses.Skipped;
            existing.ErrorCode = reason;
            existing.UnitCount = 0;
            existing.CompletedAt = null;
            existing.UpdatedAt = now;
        }
        else
        {
            _db.DocumentVisualIndexes.Add(new DocumentVisualIndex
            {
                Id = Guid.NewGuid(),
                FileItemId = file.FileItemId,
                OwnerUserId = ownerUserId,
                SourceBlobObjectId = file.BlobObjectId,
                RenderProfileKey = renderProfileKey,
                EmbeddingProfileId = profile.Id,
                Status = AiArtifactStatuses.Skipped,
                ErrorCode = reason,
                UnitCount = 0,
                CreatedAt = now,
            });
        }

        await _db.SaveChangesAsync(cancellationToken);
    }

    private static string? Truncate(string? value, int max)
        => value is null || value.Length <= max ? value : value[..max];

    private static async Task CopyBoundedAsync(
        Stream source, Stream destination, long limit, CancellationToken cancellationToken)
    {
        var buffer = new byte[81_920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            total += read;
            if (total > limit)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                return;
            }
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }

    private sealed class VisualIndexState
    {
        public int FilesSeen;
        public int Indexed;
        public int Unchanged;
        public int UnitsRendered;
        public int UnitsEmbedded;
        public int Skipped;

        private readonly Dictionary<string, int> _skipReasons = new(StringComparer.Ordinal);

        public IReadOnlyDictionary<string, int> SkipReasons => _skipReasons;

        public void Skip(string reason)
        {
            Skipped++;
            _skipReasons[reason] = _skipReasons.TryGetValue(reason, out var count) ? count + 1 : 1;
        }
    }
}
