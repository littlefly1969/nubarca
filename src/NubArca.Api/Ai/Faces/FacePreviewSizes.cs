namespace NubArca.Api.Ai.Faces;

// Supported face-preview crop sizes (square edge, px). Small for chips/avatars,
// medium for larger cards, large for the context viewer's focused face.
public static class FacePreviewSizes
{
    public const string Small = "small";
    public const string Medium = "medium";
    public const string Large = "large";

    private static readonly IReadOnlyDictionary<string, int> Edges =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            [Small] = 192,
            [Medium] = 320,
            [Large] = 512,
        };

    public static bool IsKnown(string? size) =>
        !string.IsNullOrWhiteSpace(size) && Edges.ContainsKey(size);

    public static int GetEdge(string size) => Edges[size];

    public static string Normalize(string size) => Edges.Keys.First(k =>
        string.Equals(k, size, StringComparison.OrdinalIgnoreCase));
}
