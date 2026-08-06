using Microsoft.EntityFrameworkCore;
using NubArca.Api.Data;
using NubArca.Api.Domain;

namespace NubArca.Api.PhotoExport;

// SINGLE SOURCE OF TRUTH for "which FileItems are exportable photos".
//
// Centralized on purpose: every export path (snapshot build, counts, tests)
// goes through this one predicate, so the rule can be extended in exactly one
// place. In particular, when Private Vault lands the exclusion is added HERE
// (see the FUTURE note) and the whole export inherits it automatically — no
// vault content can leak through export because no other place re-implements
// eligibility.
//
// Current rule = normal visible photo library only:
//   * owned by the requesting user;
//   * not soft-deleted;
//   * an actual image, by the SAME classification /api/images uses
//     (server-detected content type preferred; client MIME only as a fallback
//     for pre-metadata blobs). This excludes non-photos, derived artifacts
//     (thumbnails/previews are not FileItems), and spoofed image MIMEs.
public static class PhotoExportEligibility
{
    public static IQueryable<FileItem> EligiblePhotos(AppDbContext db, Guid ownerUserId)
    {
        return db.FileItems
            .AsNoTracking()
            .Where(f => f.OwnerUserId == ownerUserId
                && f.DeletedAt == null
                && (db.BlobMetadata.Any(m => m.BlobObjectId == f.BlobObjectId
                        && m.DetectedContentType != null
                        && m.DetectedContentType.StartsWith("image/"))
                    || (!db.BlobMetadata.Any(m => m.BlobObjectId == f.BlobObjectId)
                        && f.MimeType.StartsWith("image/"))));

        // FUTURE (Private Vault): append the vault-scope exclusion to the
        // predicate above — e.g. `&& f.VaultId == null` — so private/locked/
        // encrypted content is never enumerated by export, search, or organizer.
        // Add it HERE only; do not duplicate eligibility logic elsewhere.
    }
}
