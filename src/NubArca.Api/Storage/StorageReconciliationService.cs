using Microsoft.EntityFrameworkCore;
using NubArca.Api.Data;

namespace NubArca.Api.Storage;

// Slice 65: operator reconciliation between the physical blob store and the
// BlobObject table. Reports counts only — NEVER logs a storage key or a
// physical path. Dry-run by default; physical deletion of orphans requires
// an explicit opt-in. Mutates the filesystem only (deletes orphan physical
// objects when asked); never touches the database.
//
// "Orphan" means NO live owner, not merely "no BlobObject row". Ownership of a
// physical object can be recognised by the model without going through
// blob_objects — a rendered print artifact is owned by
// PrintJob.ArtifactStorageKey alone — and such an object must never be counted
// as an orphan or deleted. Any future owner of that shape belongs in the same
// set, beside the print artifacts.
public sealed class StorageReconciliationService
{
    private readonly AppDbContext _db;
    private readonly IBlobStorage _storage;
    private readonly IBlobStorage _derivedStorage;

    // Slice 72: `derivedStorage` is the separate derived-media store, if
    // configured. Optional so direct-construction test sites keep compiling;
    // null falls back to the original store (single-root default). When the two
    // roots are the same instance/path the second-store work collapses to the
    // original behaviour.
    public StorageReconciliationService(
        AppDbContext db, IBlobStorage storage, IDerivedBlobStorage? derivedStorage = null)
    {
        _db = db;
        _storage = storage;
        _derivedStorage = derivedStorage ?? storage;
    }

    public async Task<StorageReconciliationResult> RunAsync(
        StorageReconciliationOptions options,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        // Known storage keys from the DB. Personal-cloud scale: a single
        // HashSet is fine. Membership lookups drive both passes.
        var knownKeys = await _db.BlobObjects.AsNoTracking()
            .Select(b => b.StorageKey)
            .ToListAsync(cancellationToken);
        var known = new HashSet<string>(knownKeys, StringComparer.Ordinal);

        // A BlobObject row is NOT the only thing that owns a physical object.
        // A rendered print artifact is written straight to the derived store by
        // PrintStationService / PartyPrintSubmissionService and is referenced
        // only by PrintJob.ArtifactStorageKey — a plain string column, by
        // design: the print artifact is not content-addressed library content
        // and must not be given a synthetic BlobObject just to satisfy this
        // tool. Its bytes land in the very same objects/{a}/{b}/{sha256}
        // layout this scan walks, so without this set every live print artifact
        // looks like an orphan and --delete-orphans deletes a queued job's
        // rendered image out from under the Print Agent.
        //
        // Deliberately NOT filtered by job state: a job holds its artifact for
        // its whole life, PrintJob rows are never deleted and the column is
        // never cleared, so any non-null key is a live owner.
        var printArtifactKeys = await _db.PrintJobs.AsNoTracking()
            .Where(j => j.ArtifactStorageKey != null)
            .Select(j => j.ArtifactStorageKey!)
            .ToListAsync(cancellationToken);
        // Ownership recognised by the model but not by blob_objects. Kept as a
        // SEPARATE set so pass 2 keeps meaning exactly "blob rows whose bytes
        // are gone" and BlobObjectRows keeps counting blob rows only.
        var ownedWithoutBlobRow = new HashSet<string>(printArtifactKeys, StringComparer.Ordinal);

        var splitRoots = !ReferenceEquals(_derivedStorage, _storage);

        // Pass 1 — on-disk objects with no BlobObject row (orphans). Scan the
        // original root and, when a separate derived root is configured, the
        // derived root too. A `seen` set dedups so the same physical key across
        // both (or identical roots) is counted once. Derived artifacts have
        // BlobObject rows (shared table), so they are NOT orphans — only truly
        // unreferenced files are.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var orphans = 0;
        var orphansDeleted = 0;
        var protectedWithoutBlobRow = 0;

        async Task ScanAsync(IBlobStorage store)
        {
            await foreach (var key in store.EnumerateStorageKeysAsync(cancellationToken))
            {
                if (!seen.Add(key))
                {
                    continue;
                }
                if (known.Contains(key))
                {
                    continue;
                }
                if (ownedWithoutBlobRow.Contains(key))
                {
                    // Live non-blob owner (a print job's rendered artifact).
                    // Not an orphan, and never deletable by this tool. Counted
                    // separately so an operator can see why the orphan number
                    // is lower than "objects minus blob rows". `seen` already
                    // dedups, so meeting the same artifact in both roots — or
                    // shared by two jobs that rendered identical bytes — counts
                    // exactly once.
                    protectedWithoutBlobRow++;
                    continue;
                }

                orphans++;
                if (!options.DryRun && options.DeleteOrphans
                    && (options.Limit is null || orphansDeleted < options.Limit))
                {
                    // Delete from both roots (idempotent for a missing file) so
                    // an orphan is removed wherever it physically lives.
                    await _storage.DeleteAsync(key, cancellationToken);
                    if (splitRoots)
                    {
                        await _derivedStorage.DeleteAsync(key, cancellationToken);
                    }
                    orphansDeleted++;
                }
            }
        }

        await ScanAsync(_storage);
        if (splitRoots)
        {
            await ScanAsync(_derivedStorage);
        }
        var scanned = seen.Count;

        // Pass 2 — BlobObject rows whose physical object is missing from BOTH
        // roots. A derived artifact present in the derived root must NOT be
        // misclassified as missing source data. Originals never live in the
        // derived root, but checking both is cheap and correct. Missing rows
        // are never auto-repaired here (originals need a backup; derived
        // artifacts regenerate via `media derivatives backfill`).
        var missing = 0;
        foreach (var key in known)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var present = await _storage.ExistsAsync(key, cancellationToken)
                || (splitRoots && await _derivedStorage.ExistsAsync(key, cancellationToken));
            if (!present)
            {
                missing++;
            }
        }

        var result = new StorageReconciliationResult(
            PhysicalObjectsScanned: scanned,
            BlobObjectRows: known.Count,
            OrphanPhysicalObjects: orphans,
            OrphansDeleted: orphansDeleted,
            MissingPhysicalObjects: missing,
            DryRun: options.DryRun,
            ProtectedNonBlobObjects: protectedWithoutBlobRow);

        log?.Invoke(
            $"storage reconcile{(options.DryRun ? " (dry-run)" : "")}: " +
            $"scanned {scanned} object(s), {known.Count} blob row(s); " +
            $"protected-non-blob {protectedWithoutBlobRow}; " +
            $"orphan-on-disk {orphans} (deleted {orphansDeleted}); " +
            $"missing-on-disk {missing}.");

        return result;
    }
}

public sealed record StorageReconciliationOptions
{
    // Dry-run is the default everywhere; destructive deletion needs both
    // DryRun=false AND DeleteOrphans=true.
    public bool DryRun { get; init; } = true;
    public bool DeleteOrphans { get; init; }
    public int? Limit { get; init; }
}

public sealed record StorageReconciliationResult(
    int PhysicalObjectsScanned,
    int BlobObjectRows,
    int OrphanPhysicalObjects,
    int OrphansDeleted,
    int MissingPhysicalObjects,
    bool DryRun,
    // On-disk objects that have no BlobObject row but ARE owned by a live
    // non-blob reference (today: PrintJob.ArtifactStorageKey). Reported so the
    // orphan count is explainable; these are never deleted by this tool.
    int ProtectedNonBlobObjects = 0);
