using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NubArca.Api.Data;
using NubArca.Api.Domain.Ai;
using NubArca.Api.Storage;

namespace NubArca.Api.Ai.Documents.Sources;

/// The `file` kind: a person's own `FileItem`s.
///
/// Eligibility is OwnerDocumentEligibility, unchanged. The revision is the
/// content-addressed blob id, so a rename or a move — DB-only operations that
/// leave the blob alone — is not a change, and a content change always is.
///
/// ORDER MATTERS HERE and is the security property: eligibility is established
/// from the live `FileItem` BEFORE the storage layer is asked for a single byte.
/// A file in the Private Vault, deleted, excluded from the library or belonging
/// to somebody else is not read at all — not read and then discarded.
public sealed class FileItemDocumentSource : IOwnerDocumentSource
{
    private readonly AppDbContext _db;
    private readonly IBlobStorage _storage;
    private readonly IOptions<DocumentExtractionOptions> _options;

    public FileItemDocumentSource(
        AppDbContext db, IBlobStorage storage, IOptions<DocumentExtractionOptions> options)
    {
        _db = db;
        _storage = storage;
        _options = options;
    }

    public string Kind => DocumentSourceKinds.File;

    /// The revision token of a file reading: its blob id. Internal only.
    public static string RevisionOf(Guid blobObjectId) => blobObjectId.ToString("N");

    /// The blob a `file` revision names, or null for a token that is not one.
    public static Guid? BlobOf(string revisionHash)
        => Guid.TryParseExact(revisionHash, "N", out var id) ? id : null;

    public IQueryable<EligibleSourceRow> Eligible(AppDbContext db, Guid ownerUserId)
        => FileItemDocumentEligibility.Instance.Eligible(db, ownerUserId);

    public async Task<IReadOnlyList<DocumentSourceCandidate>> ListCandidatesAsync(
        Guid ownerUserId, Guid? afterSourceId, int take, CancellationToken cancellationToken = default)
    {
        // KEYSET PAGING, never `ToListAsync()` over a library. A person can have
        // a hundred thousand files, and each row carries a name and a path.
        var cursor = afterSourceId ?? Guid.Empty;
        var page = await OwnerDocumentEligibility
            .Extractable(_db.FileItems.AsNoTracking(), ownerUserId)
            .Where(f => f.Id > cursor)
            .OrderBy(f => f.Id)
            .Select(f => new { f.Id, f.BlobObjectId, f.Name })
            .Take(Math.Max(1, take))
            .ToListAsync(cancellationToken);

        return page
            .Select(f => new DocumentSourceCandidate(f.Id, RevisionOf(f.BlobObjectId), f.Name))
            .ToList();
    }

    public async Task<DocumentSourceCandidate?> GetCandidateAsync(
        Guid ownerUserId, Guid sourceId, CancellationToken cancellationToken = default)
    {
        var file = await OwnerDocumentEligibility
            .Extractable(_db.FileItems.AsNoTracking(), ownerUserId)
            .Where(f => f.Id == sourceId)
            .Select(f => new { f.Id, f.BlobObjectId, f.Name })
            .FirstOrDefaultAsync(cancellationToken);

        return file is null
            ? null
            : new DocumentSourceCandidate(file.Id, RevisionOf(file.BlobObjectId), file.Name);
    }

    public async Task<DocumentSourceLoad> LoadAsync(
        Guid ownerUserId, DocumentSourceCandidate candidate, CancellationToken cancellationToken = default)
    {
        var options = _options.Value;

        // RE-ASKED, LIVE. The candidate came from a page read some time ago.
        var file = await OwnerDocumentEligibility
            .Extractable(_db.FileItems.AsNoTracking(), ownerUserId)
            .Where(f => f.Id == candidate.SourceId)
            .Select(f => new { f.BlobObjectId, f.Name, f.MimeType, f.SizeBytes })
            .FirstOrDefaultAsync(cancellationToken);

        if (file is null || RevisionOf(file.BlobObjectId) != candidate.RevisionHash)
        {
            return DocumentSourceLoad.NotEligible();
        }

        // SIZE BEFORE BYTES. `FileItem.SizeBytes` is recorded at upload, so an
        // oversized document is refused without opening the blob at all. A limit
        // enforced after reading is a report, not a bound.
        //
        // The budget is derived from what the file CLAIMS to be, because nothing
        // about the content is known yet and the read has to be bounded anyway.
        // It is a resource budget and not a format decision — the bytes still
        // decide, and the per-format bound is re-checked once the format is
        // known.
        var candidateBudget = DocumentFormatProbe.CandidateSourceBudget(file.MimeType, file.Name, options);
        if (file.SizeBytes > candidateBudget)
        {
            return DocumentSourceLoad.Refused(
                DocumentExtractionReasons.TooLarge, permanent: true, recordVerdict: false);
        }

        var storageKey = await _db.BlobObjects.AsNoTracking()
            .Where(b => b.Id == file.BlobObjectId)
            .Select(b => b.StorageKey)
            .FirstOrDefaultAsync(cancellationToken);
        if (storageKey is null)
        {
            return DocumentSourceLoad.Refused(
                DocumentSourceReasons.BlobMissing, permanent: false, recordVerdict: false);
        }

        byte[] bytes;
        try
        {
            // Bounded read: the stream is copied into a buffer capped at the
            // size gate above plus one byte, so a blob whose recorded size lies
            // cannot become an unbounded allocation.
            await using var stream = await _storage.OpenReadAsync(storageKey, cancellationToken);
            using var buffer = new MemoryStream();
            await CopyBoundedAsync(stream, buffer, (long)candidateBudget + 1, cancellationToken);
            bytes = buffer.ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A storage error is not a content verdict, and it must not mark the
            // document permanently skipped — the next pass tries again.
            return DocumentSourceLoad.Refused(
                DocumentSourceReasons.Unreadable, permanent: false, recordVerdict: false);
        }

        // THE RECORDED SIZE CAN LIE, so the BYTES THAT ARRIVED are checked too.
        // Parsing a buffer cut at the budget would publish the first N bytes of
        // somebody's document as the whole of it.
        if (bytes.Length > candidateBudget)
        {
            return DocumentSourceLoad.Refused(
                DocumentExtractionReasons.TooLarge, permanent: true, recordVerdict: true);
        }

        return DocumentSourceLoad.FromBytes(bytes, file.Name, file.MimeType);
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
}

/// The `file` kind's eligibility branch on its own, with no collaborators, so
/// a caller constructed outside DI (an operator tool, a test) still has the
/// file branch. EXACTLY OwnerDocumentEligibility.Eligible — owner, not
/// deleted, not vaulted, active in the library — projected to the row shape.
public sealed class FileItemDocumentEligibility : IOwnerDocumentSourceEligibility
{
    public static readonly FileItemDocumentEligibility Instance = new();

    private FileItemDocumentEligibility() { }

    public string Kind => DocumentSourceKinds.File;

    public IQueryable<EligibleSourceRow> Eligible(AppDbContext db, Guid ownerUserId)
        => OwnerDocumentEligibility.Eligible(db.FileItems.AsNoTracking(), ownerUserId)
            .Select(f => new EligibleSourceRow
            {
                SourceId = f.Id,
                // The LIVE owner, off the FileItem the predicate matched.
                OwnerUserId = f.OwnerUserId,
                // The file NAME: what a person recognises. Never the logical
                // folder path, the storage key or a physical path.
                DisplayName = f.Name,
                // The LIVE revision: a reading of the bytes this file named
                // before it was replaced is not a reading of this file.
                LiveBlobObjectId = f.BlobObjectId,
                LiveRevision = null,
            });
}

/// Source-level reason tokens. Environment states, never content verdicts.
public static class DocumentSourceReasons
{
    /// The blob row a file names is gone. Retryable.
    public const string BlobMissing = "blob-missing";

    /// Storage could not be read. Retryable.
    public const string Unreadable = "unreadable";

    /// The job was cancelled while this source was in flight. Never a failure.
    public const string Cancelled = "cancelled";

    /// The slice yielded before this phase finished. Resumed next slice.
    public const string Yielded = "yielded";

    /// An unexpected exception. Retryable; the exception itself is never
    /// recorded, because its message can carry a path.
    public const string UnexpectedError = "unexpected-error";
}
