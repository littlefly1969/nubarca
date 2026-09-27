namespace NubArca.PrintAgent.Adapters;

/// <summary>
/// Which way to turn the page so the artifact fills it.
///
/// A driver defines its paper in whichever orientation it likes: a DS620's 4x6
/// is 4 wide and 6 tall, a DS-RX1's is 6 wide and 4 tall — the way the paper
/// runs through it. "Landscape" turns the page relative to THAT definition, so
/// the answer depends on both shapes, never on the picture's alone. Deciding
/// from the picture alone printed a portrait photograph shrunk onto a sideways
/// page on the DS-RX1, and put its strip cut across the photographs.
/// </summary>
public static class SheetOrientation
{
    /// <summary>
    /// True when the page must be turned from the driver's own paper definition
    /// for an artifact of this shape to fill it.
    /// </summary>
    public static bool Landscape(int artifactWidth, int artifactHeight, int paperWidth, int paperHeight) =>
        (artifactWidth > artifactHeight) != (paperWidth > paperHeight);
}
