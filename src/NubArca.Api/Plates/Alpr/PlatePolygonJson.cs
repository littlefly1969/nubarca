using System.Text.Json;

namespace NubArca.Api.Plates.Alpr;

// Serializes an optional refined plate polygon to/from the opaque PolygonJson
// text column ([{ "x":.., "y":.. }, …], normalized [0..1]). Internal only —
// PolygonJson is never surfaced through any DTO. Mirrors FaceLandmarksJson.
public static class PlatePolygonJson
{
    public static string? Serialize(IReadOnlyList<PlatePoint>? polygon)
        => polygon is null || polygon.Count == 0 ? null : JsonSerializer.Serialize(polygon);

    public static IReadOnlyList<PlatePoint>? Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }
        try
        {
            var points = JsonSerializer.Deserialize<List<PlatePoint>>(json);
            return points is { Count: > 0 } ? points : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
