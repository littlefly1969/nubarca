using Microsoft.EntityFrameworkCore;
using NubArca.Api.Data;
using NubArca.Api.Domain;

namespace NubArca.Api.Storage;

// Slice 97 (bug 3): BlobObject.ReferenceCount is DERIVED accounting, not the
// source of truth — the owners are the rows that reference a blob:
//
//   * file_items.BlobObjectId      (originals; soft-deleted rows still own
//                                   their reference until the sweeper purges)
//   * file_thumbnails.BlobObjectId (derived artifacts)
//   * face_previews.BlobObjectId   (regenerable face crops; plain correlation
//                                   id rather than a BlobObject foreign key)
//   * plate_images.BlobObjectId    (owner-private Plates; hard-deleted, so every
//                                   existing row owns one reference)
//   * plate_redacted_media.BlobObjectId (owner-private Plates redacted-media
//                                   cache; derived artifact, one ref per row)
//   * aesthetic_lab_items.BlobObjectId (owner-private Aesthetics Lab; hard-
//                                   deleted, so every existing row owns one
//                                   reference)
//   * aesthetic_lab_derivatives.BlobObjectId (Aesthetics Lab derived renditions;
//                                   derived artifact, one ref per row)
//   * album_transfer_items.BlobObjectId (SHARE-COPY-01 detached-copy manifests;
//                                   ONLY while the parent transfer is pending —
//                                   accept/decline/cancel/expire release)
//
// A hard interruption between the (auto-committed) refcount increment and the
// owner-row commit — worker kill, OOM, crash — leaks one reference by design
// (the slice-95 transaction removal documents this), and a swallowed release
// failure does the same. A nonzero refcount with no owners is invisible to
// the janitor (it only reclaims ReferenceCount == 0), so the bytes are pinned
// forever. This service recomputes the truth from the owner tables:
//
//   audit-references   read-only comparison, aggregate counts only
//   repair-references  sets ReferenceCount to the computed value (guarded
//                      against concurrent legitimate changes); never touches
//                      physical bytes — the janitor reclaims zero-ref blobs
//                      under its normal grace rules.
//
// Output is counts only — never a storage key, SHA, id, name, or path.
public sealed class BlobReferenceAuditService
{
    private readonly AppDbContext _db;
    private readonly TimeProvider _clock;

    public BlobReferenceAuditService(AppDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    public async Task<BlobReferenceAuditReport> AuditAsync(CancellationToken cancellationToken = default)
    {
        var (rows, computed) = await LoadAsync(cancellationToken);

        int matched = 0, tooHigh = 0, tooLow = 0, orphanedNonzero = 0, zeroRefOwned = 0;
        long totalDb = 0, totalComputed = 0;
        foreach (var row in rows)
        {
            var actual = computed.TryGetValue(row.Id, out var c) ? c : 0;
            totalDb += row.ReferenceCount;
            totalComputed += actual;

            if (row.ReferenceCount == actual)
            {
                matched++;
                continue;
            }
            if (row.ReferenceCount > actual)
            {
                tooHigh++;
                if (actual == 0)
                {
                    orphanedNonzero++;
                }
            }
            else
            {
                tooLow++;
                if (row.ReferenceCount == 0)
                {
                    zeroRefOwned++;
                }
            }
        }

        return new BlobReferenceAuditReport(
            TotalBlobs: rows.Count,
            MatchedReferenceCount: matched,
            DbRefcountTooHigh: tooHigh,
            DbRefcountTooLow: tooLow,
            OrphanedNonzeroRefcount: orphanedNonzero,
            ZeroRefWithRealReferences: zeroRefOwned,
            TotalDbReferences: totalDb,
            TotalComputedReferences: totalComputed);
    }

    public async Task<BlobReferenceRepairResult> RepairAsync(
        bool dryRun, CancellationToken cancellationToken = default)
    {
        var (rows, computed) = await LoadAsync(cancellationToken);

        var repaired = 0;
        var skippedConcurrent = 0;
        var mismatched = 0;
        var now = _clock.GetUtcNow().UtcDateTime;
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var actual = computed.TryGetValue(row.Id, out var c) ? c : 0;
            if (row.ReferenceCount == actual)
            {
                continue;
            }
            mismatched++;
            if (dryRun)
            {
                continue;
            }

            // Optimistic guard: only correct the row if it still holds the
            // value the audit observed. A live upload/release that moved the
            // count in the meantime invalidates OUR computation for this blob,
            // not the system — skip it; the next run re-audits.
            var updated = await _db.BlobObjects
                .Where(b => b.Id == row.Id && b.ReferenceCount == row.ReferenceCount)
                .ExecuteUpdateAsync(
                    s => s
                        .SetProperty(b => b.ReferenceCount, actual)
                        .SetProperty(
                            b => b.PurgeEligibleAt,
                            _ => actual == 0
                                ? (DateTime?)now
                                : null),
                    cancellationToken);
            if (updated == 1)
            {
                repaired++;
            }
            else
            {
                skippedConcurrent++;
            }
        }

        return new BlobReferenceRepairResult(
            TotalBlobs: rows.Count,
            Mismatched: mismatched,
            Repaired: repaired,
            SkippedConcurrentChange: skippedConcurrent,
            DryRun: dryRun);
    }

    private async Task<(List<BlobRow> Rows, Dictionary<Guid, int> Computed)> LoadAsync(
        CancellationToken cancellationToken)
    {
        var rows = await _db.BlobObjects.AsNoTracking()
            .Select(b => new BlobRow(b.Id, b.ReferenceCount))
            .ToListAsync(cancellationToken);

        // Owner references, grouped server-side, mirroring the release sites
        // exactly: a FileItem releases its blob at SOFT delete
        // (FileItemService.SoftDeleteAsync), so only active rows own a
        // reference; FileThumbnail rows release at hard purge
        // (FileItemSweeper), so every existing row owns one regardless of the
        // parent file's trash state.
        // IgnoreQueryFilters: Private Vault content still OWNS its blob reference.
        // The global vault filter must NOT apply here — under-counting would make
        // RepairAsync zero a live blob's ReferenceCount, letting the janitor
        // delete bytes that vault files depend on. Refcount truth spans all rows.
        var fileRefs = await _db.FileItems.AsNoTracking()
            .IgnoreQueryFilters()
            .Where(f => f.DeletedAt == null)
            .GroupBy(f => f.BlobObjectId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);
        var thumbRefs = await _db.FileThumbnails.AsNoTracking()
            .GroupBy(t => t.BlobObjectId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);
        // Face previews are refcount-managed derived blobs even though
        // FacePreview.BlobObjectId deliberately has no FK. Omitting this plain
        // correlation makes every live preview look like an orphan and makes
        // RepairAsync zero it, after which the janitor can delete bytes still
        // owned by the preview row.
        var facePreviewRefs = await _db.FacePreviews.AsNoTracking()
            .GroupBy(p => p.BlobObjectId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);
        // Owner-private Plates: each PlateImage row owns exactly one reference to
        // its blob. Delete is a HARD delete that releases the reference in the
        // same transaction (PlateImageService.DeleteAsync), so — like
        // FileThumbnail — every existing row owns one reference regardless of
        // anything else. Omitting this would make RepairAsync zero a live plate
        // blob's ReferenceCount and let the janitor delete bytes a plate needs.
        var plateRefs = await _db.PlateImages.AsNoTracking()
            .GroupBy(p => p.BlobObjectId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);
        // Owner-private Plates redacted-media cache: each row owns exactly one
        // reference to its DERIVED blob (mirrors FileThumbnail). The reference is
        // released when the row is invalidated or its PlateImage is hard-deleted
        // (PlateRedactedMediaService / PlateImageService.DeleteAsync). Omitting
        // this would let RepairAsync zero a live cache blob and the janitor
        // delete bytes the cache still needs.
        var plateRedactedRefs = await _db.PlateRedactedMedia.AsNoTracking()
            .GroupBy(m => m.BlobObjectId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);
        // Party face-search indicator crops: each session with a stored crop owns
        // exactly one reference to its DERIVED blob, released when the search is
        // deleted (PartyFaceSearchService.DeleteAsync / expiry cleanup). The crop
        // is NOT regenerable (the selfie is discarded), so zeroing a live one
        // would permanently lose the TV indicator thumbnail.
        var faceCropRefs = await _db.PartyFaceSearchSessions.AsNoTracking()
            .Where(s => s.FaceCropBlobObjectId != null)
            .GroupBy(s => s.FaceCropBlobObjectId!.Value)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);
        // Owner-private Aesthetics Lab: each lab item owns exactly one reference
        // to its blob. Delete is a HARD delete that releases the reference in the
        // same transaction (AestheticLabService.RemoveAsync), so — like
        // PlateImage — every existing row owns one reference. Omitting this would
        // make RepairAsync zero a live lab blob and let the janitor delete bytes
        // the lab needs.
        var labItemRefs = await _db.AestheticLabItems.AsNoTracking()
            .GroupBy(i => i.BlobObjectId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);
        // Aesthetics Lab derived renditions (small/medium): each row owns exactly
        // one reference to its DERIVED blob (mirrors FileThumbnail). Released when
        // the row or its lab item is removed. Omitting this would let RepairAsync
        // zero a live derived blob and the janitor delete bytes the cache needs.
        var labDerivativeRefs = await _db.AestheticLabDerivatives.AsNoTracking()
            .GroupBy(d => d.BlobObjectId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);
        // SHARE-COPY-01 pending detached copies: while a transfer is PENDING its
        // manifest owns one reference per item, which is precisely what keeps a
        // pending copy alive when the sender permanently deletes the source
        // files. References are released when the transfer leaves pending
        // (accept hands them to the recipient's new FileItems; cancel, decline
        // and expiry release them outright), so only pending rows own one.
        //
        // Omitting this would make RepairAsync zero the reference behind every
        // pending transfer and let the janitor delete bytes those copies need —
        // and `storage blobs audit-references` runs on every production deploy.
        var pendingTransferRefs = await _db.AlbumTransferItems.AsNoTracking()
            .Where(i => _db.AlbumTransfers.Any(t =>
                t.Id == i.AlbumTransferId && t.State == AlbumTransferStates.Pending))
            .GroupBy(i => i.BlobObjectId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        var computed = new Dictionary<Guid, int>(rows.Count);
        foreach (var r in fileRefs)
        {
            computed[r.Key] = computed.TryGetValue(r.Key, out var v) ? v + r.Count : r.Count;
        }
        foreach (var r in thumbRefs)
        {
            computed[r.Key] = computed.TryGetValue(r.Key, out var v) ? v + r.Count : r.Count;
        }
        foreach (var r in facePreviewRefs)
        {
            computed[r.Key] = computed.TryGetValue(r.Key, out var v) ? v + r.Count : r.Count;
        }
        foreach (var r in plateRefs)
        {
            computed[r.Key] = computed.TryGetValue(r.Key, out var v) ? v + r.Count : r.Count;
        }
        foreach (var r in plateRedactedRefs)
        {
            computed[r.Key] = computed.TryGetValue(r.Key, out var v) ? v + r.Count : r.Count;
        }
        foreach (var r in faceCropRefs)
        {
            computed[r.Key] = computed.TryGetValue(r.Key, out var v) ? v + r.Count : r.Count;
        }
        foreach (var r in labItemRefs)
        {
            computed[r.Key] = computed.TryGetValue(r.Key, out var v) ? v + r.Count : r.Count;
        }
        foreach (var r in labDerivativeRefs)
        {
            computed[r.Key] = computed.TryGetValue(r.Key, out var v) ? v + r.Count : r.Count;
        }
        foreach (var r in pendingTransferRefs)
        {
            computed[r.Key] = computed.TryGetValue(r.Key, out var v) ? v + r.Count : r.Count;
        }
        return (rows, computed);
    }

    private sealed record BlobRow(Guid Id, long ReferenceCount);
}

public sealed record BlobReferenceAuditReport(
    int TotalBlobs,
    int MatchedReferenceCount,
    int DbRefcountTooHigh,
    int DbRefcountTooLow,
    // Subset of DbRefcountTooHigh with NO owner at all — the janitor-invisible
    // leak this slice exists for.
    int OrphanedNonzeroRefcount,
    // Subset of DbRefcountTooLow at zero despite live owners — the janitor
    // could DELETE bytes a real row still needs. The most dangerous bucket.
    int ZeroRefWithRealReferences,
    long TotalDbReferences,
    long TotalComputedReferences);

public sealed record BlobReferenceRepairResult(
    int TotalBlobs,
    int Mismatched,
    int Repaired,
    int SkippedConcurrentChange,
    bool DryRun);
