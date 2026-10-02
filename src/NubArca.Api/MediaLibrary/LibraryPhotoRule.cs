using System.Linq.Expressions;
using NubArca.Api.Data;
using NubArca.Api.Domain;

namespace NubArca.Api.MediaLibrary;

/// <summary>
/// "Is this file a photograph?" exactly as the Library answers it — the image
/// scope of the galleries (<c>/api/media</c>, <c>/api/images</c>), the album
/// workspace and photo export: the SERVER-DETECTED content type is
/// <c>image/*</c>; or, for a blob uploaded before metadata existed and so with
/// no <see cref="BlobMetadata"/> row at all, the file's own MIME type is
/// <c>image/*</c>. A file whose bytes were examined and not recognised (a row
/// with no detected type) is not a photograph, whatever its MIME claims.
///
/// A surface that offers an action on "a photo of the Library" asks this, so
/// it never refuses a photograph the Library shows nor accepts one it hides.
/// </summary>
public static class LibraryPhotoRule
{
    public static Expression<Func<FileItem, bool>> IsPhoto(AppDbContext db) =>
        f => db.BlobMetadata.Any(m => m.BlobObjectId == f.BlobObjectId
                && m.DetectedContentType != null
                && m.DetectedContentType.StartsWith("image/"))
            || (!db.BlobMetadata.Any(m => m.BlobObjectId == f.BlobObjectId)
                && f.MimeType.StartsWith("image/"));
}
