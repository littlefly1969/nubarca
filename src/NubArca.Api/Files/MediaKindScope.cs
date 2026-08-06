namespace NubArca.Api.Files;

// Slice 5 (unified media workspace): the media-kind selector shared by the
// unified /api/media and /api/albums/{id}/media endpoints. `All` returns images
// AND videos in one server-ordered stream (the "Tutti" tab); `Image`/`Video`
// narrow to a single kind (the "Foto" / "Video" tabs) and are behaviourally
// identical to the legacy /api/images and /api/videos membership rules.
//
// Membership is defined by the SERVER-DETECTED content type (BlobMetadata.
// DetectedContentType), falling back to the client MIME only for pre-metadata
// blobs — the same rule the photo/video galleries already use, so the unified
// surface can never drift from them.
public enum MediaKindScope
{
    All = 0,
    Image = 1,
    Video = 2,
}

public static class MediaKindScopeParser
{
    // Wire values are the lower-case member names; an absent parameter is `all`.
    public static bool TryParse(string? raw, out MediaKindScope kind)
    {
        switch (raw?.Trim().ToLowerInvariant())
        {
            case null:
            case "":
            case "all":
                kind = MediaKindScope.All;
                return true;
            case "image":
                kind = MediaKindScope.Image;
                return true;
            case "video":
                kind = MediaKindScope.Video;
                return true;
            default:
                kind = MediaKindScope.All;
                return false;
        }
    }

    public static string ToWire(this MediaKindScope kind) => kind switch
    {
        MediaKindScope.Image => "image",
        MediaKindScope.Video => "video",
        _ => "all",
    };

    // Cursor fingerprint for the unified media surface: the filter fingerprint
    // salted with the media kind. `ImageFilters.Fingerprint()` intentionally does
    // NOT encode kind (it is orthogonal to the filter set), so a cursor issued on
    // the "Foto" tab must not replay on "Video" or "Tutti". Always non-null (the
    // kind is always present), so an empty-filter cursor is still kind-bound.
    public static string MediaCursorFingerprint(this MediaKindScope kind, ImageFilters filters)
        => $"k={kind.ToWire()}|{filters.Fingerprint() ?? string.Empty}";
}
