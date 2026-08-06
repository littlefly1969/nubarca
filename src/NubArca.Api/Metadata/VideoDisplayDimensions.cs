namespace NubArca.Api.Metadata;

/// <summary>
/// Resolves the DISPLAY (on-screen) dimensions of a video from its stored CODED
/// stream dimensions and rotation flag.
///
/// ffprobe reports the coded width/height of the video stream (e.g. 1920×1080)
/// while the rotation side-data / display matrix is stored separately in
/// <c>blob_metadata.rotation</c>. ffmpeg autorotates when it extracts a poster
/// frame (no <c>-noautorotate</c>), so a phone clip shot vertically with a 90°
/// display matrix produces a PORTRAIT poster even though its coded dimensions
/// are landscape. Exposing the coded dimensions verbatim would give the media
/// wall a landscape tile shape for a poster that is actually portrait, so the
/// coded dimensions are swapped for a quarter-turn rotation (90° / 270°) to
/// match what the viewer — and the poster — actually show.
/// </summary>
public static class VideoDisplayDimensions
{
    /// <summary>
    /// Returns the width/height as displayed: swapped when the rotation is a
    /// quarter turn (90° or 270°), unchanged for 0° / 180° or when the rotation
    /// is unknown. Null dimensions pass through untouched.
    /// </summary>
    public static (int? Width, int? Height) Resolve(int? width, int? height, int? rotation)
    {
        // Rotation is normalized to [0,360); treat any quarter turn as a swap.
        var quarterTurn = rotation is int r && ((r % 180) + 180) % 180 == 90;
        return quarterTurn ? (height, width) : (width, height);
    }
}
