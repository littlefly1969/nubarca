namespace NubArca.Api.Domain.Print;

/// <summary>
/// One source photograph of a composed print job, in the order it appears.
///
/// A photo print composes one; a four-photo strip composes four. They live in a
/// child TABLE rather than only inside the render specification so each one is a
/// real foreign key: a source cannot silently point at a file that no longer
/// exists, and "which photographs did this print use?" is a query rather than a
/// JSON parse.
///
/// The crop is stored NORMALISED to the auto-oriented source (0..1 of its width
/// and height), never in the pixels of whatever screen composed it, so the
/// server and the browser can compute the same framing from the same numbers.
/// </summary>
public sealed class PrintJobSource
{
    public Guid Id { get; set; }
    public Guid PrintJobId { get; set; }

    /// <summary>Zero-based position in the composition: strip slot 0..3.</summary>
    public int SlotIndex { get; set; }

    public Guid FileItemId { get; set; }

    public double CropX { get; set; }
    public double CropY { get; set; }
    public double CropWidth { get; set; }
    public double CropHeight { get; set; }

    /// <summary>The whole photograph, which is what an untouched selection means.</summary>
    public static PrintJobSource Full(Guid printJobId, int slotIndex, Guid fileItemId) => new()
    {
        Id = Guid.NewGuid(),
        PrintJobId = printJobId,
        SlotIndex = slotIndex,
        FileItemId = fileItemId,
        CropX = 0,
        CropY = 0,
        CropWidth = 1,
        CropHeight = 1,
    };

    /// <summary>
    /// A crop is usable when it is inside the image and has area. Rejected here
    /// rather than clamped: a nonsensical crop is a broken client, and silently
    /// printing something else is worse than refusing.
    /// </summary>
    public static bool IsValidCrop(double x, double y, double width, double height) =>
        double.IsFinite(x) && double.IsFinite(y)
        && double.IsFinite(width) && double.IsFinite(height)
        && x >= 0 && y >= 0
        && width > 0 && height > 0
        && x + width <= 1.0000001
        && y + height <= 1.0000001;
}
