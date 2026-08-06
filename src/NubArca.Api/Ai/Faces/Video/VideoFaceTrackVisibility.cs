using Microsoft.EntityFrameworkCore;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Domain.Ai;

namespace NubArca.Api.Ai.Faces.Video;

// VFACE-02: the SINGLE definition of "this owner may inspect or decide about
// this canonical face track".
//
// A track is blob-level and shared by every FileItem on that blob, across every
// owner. The gate is therefore not the track but the reference: the caller must
// have at least one CURRENTLY VISIBLE, eligible FileItem pointing at the track's
// blob. `db.FileItems` carries the global Private-Vault query filter, so a
// vault-only reference is excluded by construction — no vault existence, count
// or timing can leak through this path.
//
// Every read and every write goes through here, so track ids cannot be
// enumerated across owners: a foreign or vaulted track simply does not exist for
// the caller and produces the same generic 404 as a missing one.
internal static class VideoFaceTrackVisibility
{
    // The canonical tracks this owner may see. Composable: callers add their own
    // filters, joins and projections on top.
    public static IQueryable<VideoFaceTrack> VisibleTracks(AppDbContext db, Guid ownerUserId)
        => db.VideoFaceTracks.AsNoTracking().Where(track =>
            db.VideoFaceAnalysisStatuses.Any(analysis =>
                analysis.Id == track.VideoFaceAnalysisStatusId
                && db.VideoSemanticIndexes.Any(index =>
                    index.Id == analysis.VideoSemanticIndexId
                    && db.FileItems.Any(file =>
                        file.BlobObjectId == index.BlobObjectId
                        && file.OwnerUserId == ownerUserId
                        && file.DeletedAt == null
                        && file.MediaLibraryState == MediaLibraryState.Active))));

    // The owner-visible FileItems that reference the blob a track belongs to.
    // One logical media item per FileItem, exactly as the gallery already
    // presents duplicates; the temporal evidence behind them is the same
    // canonical track.
    public static IQueryable<FileItem> VisibleFilesForTracks(
        AppDbContext db, Guid ownerUserId, IReadOnlyCollection<Guid> trackIds)
        => from file in db.FileItems.AsNoTracking()
           where file.OwnerUserId == ownerUserId
               && file.DeletedAt == null
               && file.MediaLibraryState == MediaLibraryState.Active
               && db.VideoSemanticIndexes.Any(index =>
                   index.BlobObjectId == file.BlobObjectId
                   && db.VideoFaceAnalysisStatuses.Any(analysis =>
                       analysis.VideoSemanticIndexId == index.Id
                       && db.VideoFaceTracks.Any(track =>
                           track.VideoFaceAnalysisStatusId == analysis.Id
                           && trackIds.Contains(track.Id))))
           select file;
}
