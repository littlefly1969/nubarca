namespace NubArca.Api.Files;

// Allowed sort fields for GET /api/images.
public enum ImageSortField
{
    Created,
    Name,
    Size,
    // Effective capture date (slice 55): embedded DateTaken when present,
    // otherwise the file's CreatedAt (upload time).
    DateTaken,
}

// Allowed sort directions.
public enum ImageSortDirection
{
    Asc,
    Desc,
}

// Parser shared between the endpoint and tests. Returns false on unknown
// values so the endpoint can map them to 400.
public static class ImageSort
{
    public const ImageSortField DefaultField = ImageSortField.Created;
    public const ImageSortDirection DefaultDirection = ImageSortDirection.Desc;

    public static bool TryParseField(string? raw, out ImageSortField field)
    {
        switch (raw?.Trim().ToLowerInvariant())
        {
            case null: case "": field = DefaultField; return true;
            case "created": field = ImageSortField.Created; return true;
            case "name": field = ImageSortField.Name; return true;
            case "size": field = ImageSortField.Size; return true;
            case "datetaken": field = ImageSortField.DateTaken; return true;
            default: field = DefaultField; return false;
        }
    }

    public static bool TryParseDirection(string? raw, out ImageSortDirection direction)
    {
        switch (raw?.Trim().ToLowerInvariant())
        {
            case null: case "": direction = DefaultDirection; return true;
            case "asc": direction = ImageSortDirection.Asc; return true;
            case "desc": direction = ImageSortDirection.Desc; return true;
            default: direction = DefaultDirection; return false;
        }
    }
}
