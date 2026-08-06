using NubArca.Api.Data;
using NubArca.Api.Domain;

namespace NubArca.Api.Ai.Photos;

// Quality gate used only by text-to-image retrieval. Very small images are
// overwhelmingly camera sidecars, icons and thumbnails; letting them compete
// in the multimodal space creates semantic hubs that displace real photos.
//
// Unknown dimensions remain eligible: absence of metadata must never make a
// real photo disappear. Image-to-image similarity and the normal gallery do
// not use this policy.
public static class SemanticPhotoCandidatePolicy
{
    public const int MinEdgePixels = 128;

    public static IQueryable<FileItem> Apply(
        IQueryable<FileItem> files,
        AppDbContext db)
        => files.Where(f => !db.BlobMetadata.Any(m =>
            m.BlobObjectId == f.BlobObjectId
            && ((m.Width != null && m.Width < MinEdgePixels)
                || (m.Height != null && m.Height < MinEdgePixels))));
}
