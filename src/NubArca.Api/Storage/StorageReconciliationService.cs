using Microsoft.EntityFrameworkCore;
using NubArca.Api.Data;

namespace NubArca.Api.Storage;

// Slice 65: operator reconciliation between the physical blob store and the
// BlobObject table. Reports counts only — NEVER logs a storage key or a
// physical path. Dry-run by default; physical deletion of orphans requires
// an explicit opt-in. Mutates the filesystem only (deletes orphan physical
// objects when asked); never touches the database.
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
            DryRun: options.DryRun);

        log?.Invoke(
            $"storage reconcile{(options.DryRun ? " (dry-run)" : "")}: " +
            $"scanned {scanned} object(s), {known.Count} blob row(s); " +
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
    bool DryRun);
