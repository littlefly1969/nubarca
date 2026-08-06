using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NubArca.Api.Data;

namespace NubArca.Api.Files;

// Why an incoming import file should be skipped. `None` means import it.
// Precedence when both options are on: PreviouslyDeleted wins over AlreadyPresent.
public enum ImportSkipReason
{
    None = 0,
    PreviouslyDeleted,
    AlreadyPresent,
}

// Decides, for a batch of incoming content hashes, which should be skipped by
// the two owner-scoped import options. Reused by every import/sync entry point
// so the policy lives in exactly one place.
//
// Privacy: "already present" is evaluated against the owner's NORMAL library
// only (the global Private Vault query filter is left in place, never bypassed),
// so a match inside the Private Vault is neither reported nor revealed.
public interface IImportSkipEvaluator
{
    // Returns a map of content-hash → skip reason for the hashes that should be
    // skipped (hashes to import are simply absent). Owner-scoped; batched (no
    // per-file round trip). An empty/whitespace hash is ignored.
    Task<IReadOnlyDictionary<string, ImportSkipReason>> EvaluateBatchAsync(
        Guid ownerUserId,
        IReadOnlyCollection<string> sha256Hexes,
        bool skipPreviouslyDeleted,
        bool skipExistingContent,
        CancellationToken cancellationToken = default);
}

public sealed class ImportSkipEvaluator : IImportSkipEvaluator
{
    private static readonly IReadOnlyDictionary<string, ImportSkipReason> Empty =
        new Dictionary<string, ImportSkipReason>();

    private readonly AppDbContext _db;
    private readonly IOptions<DeletedContentOptions> _options;

    public ImportSkipEvaluator(AppDbContext db, IOptions<DeletedContentOptions> options)
    {
        _db = db;
        _options = options;
    }

    public async Task<IReadOnlyDictionary<string, ImportSkipReason>> EvaluateBatchAsync(
        Guid ownerUserId,
        IReadOnlyCollection<string> sha256Hexes,
        bool skipPreviouslyDeleted,
        bool skipExistingContent,
        CancellationToken cancellationToken = default)
    {
        if (!skipPreviouslyDeleted && !skipExistingContent)
        {
            return Empty;
        }

        var distinct = sha256Hexes
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.ToLowerInvariant())
            .Distinct()
            .ToList();
        if (distinct.Count == 0)
        {
            return Empty;
        }

        var result = new Dictionary<string, ImportSkipReason>(StringComparer.Ordinal);

        // 1) Previously deleted (tombstone ledger) — takes precedence.
        if (skipPreviouslyDeleted)
        {
            var pepper = _options.Value.Pepper;
            var fingerprintToSha = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var sha in distinct)
            {
                // Distinct SHAs → distinct HMACs; last-writer-wins is irrelevant.
                fingerprintToSha[ContentFingerprint.Compute(pepper, sha)] = sha;
            }

            var fingerprints = fingerprintToSha.Keys.ToList();
            var matched = await _db.OwnerDeletedContentTombstones
                .AsNoTracking()
                .Where(t => t.OwnerUserId == ownerUserId
                    && t.FingerprintScheme == ContentFingerprint.Scheme
                    && fingerprints.Contains(t.ContentFingerprint))
                .Select(t => t.ContentFingerprint)
                .ToListAsync(cancellationToken);

            foreach (var fp in matched)
            {
                if (fingerprintToSha.TryGetValue(fp, out var sha))
                {
                    result[sha] = ImportSkipReason.PreviouslyDeleted;
                }
            }
        }

        // 2) Already present in the owner's NORMAL library (vault excluded).
        if (skipExistingContent)
        {
            var candidates = distinct.Where(s => !result.ContainsKey(s)).ToList();
            if (candidates.Count > 0)
            {
                var blobs = await _db.BlobObjects
                    .AsNoTracking()
                    .Where(b => candidates.Contains(b.Sha256))
                    .Select(b => new { b.Id, b.Sha256 })
                    .ToListAsync(cancellationToken);
                if (blobs.Count > 0)
                {
                    var blobIdToSha = blobs.ToDictionary(b => b.Id, b => b.Sha256);
                    var blobIds = blobs.Select(b => b.Id).ToList();

                    // Default query filter kept → Private Vault rows excluded, so
                    // vault content is never counted or revealed as "present".
                    var present = await _db.FileItems
                        .AsNoTracking()
                        .Where(f => f.OwnerUserId == ownerUserId
                            && f.DeletedAt == null
                            && blobIds.Contains(f.BlobObjectId))
                        .Select(f => f.BlobObjectId)
                        .Distinct()
                        .ToListAsync(cancellationToken);

                    foreach (var blobId in present)
                    {
                        if (blobIdToSha.TryGetValue(blobId, out var sha))
                        {
                            result[sha] = ImportSkipReason.AlreadyPresent;
                        }
                    }
                }
            }
        }

        return result;
    }
}
