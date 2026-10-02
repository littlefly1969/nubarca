namespace NubArca.Api.Print;

/// <summary>
/// Where the frame's centre falls on the photograph (fractions of it) and how
/// far in. The one framing every editor and renderer reads — see
/// <see cref="PhotoPlacementGeometry"/>.
/// </summary>
public readonly record struct PhotoPlacement(double CenterX, double CenterY, double Zoom)
{
    /// <summary>The photograph covering the frame, centred: an untouched framing.</summary>
    public static readonly PhotoPlacement Default = new(0.5, 0.5, 1);
}

/// <summary>A rectangle in fractions of the FRAME (left/top may be negative for the photograph).</summary>
public readonly record struct FrameRect(double Left, double Top, double Width, double Height)
{
    public double Right => Left + Width;
    public double Bottom => Top + Height;
}

/// <summary>
/// HOW A PHOTOGRAPH SITS IN A FRAME — the server's half of the one framing
/// shared by the party print, the party's own content, the guest book, the
/// owner's direct print and every television. The browser's half is
/// <c>packages/contracts/src/photoPlacement.ts</c>; <c>photoPlacement.cases.json</c>
/// holds the two to the same answers, case by case.
///
/// <list type="bullet">
/// <item><c>zoom = 1</c> — the photograph COVERS the frame, exactly as every
/// framing stored before zooming out existed meant it.</item>
/// <item><c>zoom &gt; 1</c> — in from there, to <see cref="MaxZoom"/>.</item>
/// <item><c>ContainZoom ≤ zoom &lt; 1</c> — out from there; the frame's own
/// background shows beside the photograph.</item>
/// <item><c>zoom = ContainZoom</c> — the whole photograph in the frame.</item>
/// </list>
///
/// On each axis independently a photograph larger than the frame pans, held so
/// no gap opens on that axis, and one smaller than the frame is centred with
/// equal bands. A band is a consequence of zooming out, never of a pan. At
/// zoom ≥ 1 the visible part is the crop the framing always meant
/// (<see cref="LegacyCrop"/>), so nothing already stored changes.
/// </summary>
public static class PhotoPlacementGeometry
{
    public const double MaxZoom = 4;

    /// <summary>
    /// How far below <see cref="ContainZoom"/> a client's zoom may fall and still
    /// mean "the whole photograph": the browser measures the picture from a
    /// derived preview whose rounding can differ from the original's by a
    /// fraction of a percent. Such a zoom is drawn at contain.
    /// </summary>
    public const double ZoomTolerance = 0.01;

    private static bool Usable(double aspect) => double.IsFinite(aspect) && aspect > 0;

    /// <summary>The zoom at which the whole photograph is inside the frame; 1 for the same shape.</summary>
    public static double ContainZoom(double photoAspect, double frameAspect) =>
        !Usable(photoAspect) || !Usable(frameAspect)
            ? 1
            : Math.Min(Math.Min(photoAspect / frameAspect, frameAspect / photoAspect), 1);

    /// <summary>The zoom actually drawn: held between contain and the maximum.</summary>
    public static double EffectiveZoom(double photoAspect, double frameAspect, double zoom) =>
        !double.IsFinite(zoom) ? 1 : Math.Min(MaxZoom, Math.Max(ContainZoom(photoAspect, frameAspect), zoom));

    /// <summary>A placement an editor could have produced for this photograph and frame.</summary>
    public static bool IsValid(double photoAspect, double frameAspect, PhotoPlacement placement) =>
        IsStructurallyValid(placement)
        && placement.Zoom >= ContainZoom(photoAspect, frameAspect) * (1 - ZoomTolerance);

    /// <summary>
    /// What is checkable without the photograph: finite, centre inside it, a
    /// positive zoom no larger than the maximum. The database's own constraint.
    /// </summary>
    public static bool IsStructurallyValid(PhotoPlacement placement) =>
        double.IsFinite(placement.CenterX) && double.IsFinite(placement.CenterY) && double.IsFinite(placement.Zoom)
        && placement.CenterX is >= 0 and <= 1 && placement.CenterY is >= 0 and <= 1
        && placement.Zoom > 0 && placement.Zoom <= MaxZoom;

    /// <summary>
    /// Where the photograph is drawn, in fractions of the frame. An unknown
    /// photograph shape is drawn as one filling the frame exactly.
    /// </summary>
    public static FrameRect Place(double photoAspect, double frameAspect, PhotoPlacement placement)
    {
        var frame = Usable(frameAspect) ? frameAspect : 1;
        var photo = Usable(photoAspect) ? photoAspect : frame;
        var zoom = EffectiveZoom(photo, frame, placement.Zoom);
        // Units of the frame's height: the frame is (frame × 1), the photograph
        // (photo × 1) scaled by `cover` covers it, then by `zoom`.
        var cover = Math.Max(frame / photo, 1);
        var scale = cover * zoom;
        var width = photo * scale / frame;
        var height = scale;
        return new FrameRect(Axis(width, placement.CenterX), Axis(height, placement.CenterY), width, height);
    }

    /// <summary>The part of the frame the photograph covers — where a date may sit.</summary>
    public static FrameRect Visible(FrameRect placed)
    {
        var left = Math.Max(0, placed.Left);
        var top = Math.Max(0, placed.Top);
        var right = Math.Min(1, placed.Right);
        var bottom = Math.Min(1, placed.Bottom);
        return new FrameRect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }

    /// <summary>
    /// The part of the PHOTOGRAPH the frame shows, as fractions of it — at zoom ≥ 1
    /// exactly the crop the framing meant before zooming out existed.
    /// </summary>
    public static (double X, double Y, double Width, double Height) LegacyCrop(FrameRect placed) => (
        placed.Width <= 1 ? 0 : -placed.Left / placed.Width,
        placed.Height <= 1 ? 0 : -placed.Top / placed.Height,
        placed.Width <= 1 ? 1 : 1 / placed.Width,
        placed.Height <= 1 ? 1 : 1 / placed.Height);

    private static double Axis(double size, double center)
    {
        if (size <= 1) return (1 - size) / 2;
        var c = double.IsFinite(center) ? Math.Clamp(center, 0, 1) : 0.5;
        return Math.Min(0, Math.Max(1 - size, 0.5 - (c * size)));
    }
}
