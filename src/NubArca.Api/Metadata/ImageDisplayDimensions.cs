namespace NubArca.Api.Metadata;

/// <summary>
/// Resolves the DISPLAY (on-screen) dimensions of an image from its stored
/// dimensions and EXIF orientation flag.
///
/// Detection records the CODED pixel dimensions (<c>Image.Identify</c> does not
/// apply EXIF orientation) while the orientation is kept separately in
/// <c>blob_metadata.orientation</c> (EXIF 1..8). Derivative renderers auto-orient
/// the thumbnail/preview, so a phone photo shot in portrait but stored as a
/// landscape sensor frame with an orientation flag decodes as landscape yet
/// DISPLAYS portrait. Exposing the coded dimensions verbatim would give the grid
/// a landscape tile for a portrait picture (the whole photo then letterboxes over
/// the blurred backdrop), so the coded dimensions are swapped for the
/// quarter-turn EXIF orientations (5/6/7/8) to match what the viewer shows.
/// </summary>
public static class ImageDisplayDimensions
{
    /// <summary>
    /// Returns width/height as displayed: swapped for the quarter-turn EXIF
    /// orientations (5/6/7/8), unchanged otherwise or when unknown. Null
    /// dimensions pass through untouched.
    /// </summary>
    public static (int? Width, int? Height) Resolve(int? width, int? height, int? orientation)
    {
        var quarterTurn = orientation is 5 or 6 or 7 or 8;
        return quarterTurn ? (height, width) : (width, height);
    }
}
