using System.Text.Json;
using NubArca.Api.Ai.Backends;

namespace NubArca.Api.Ai.Faces;

// Serialization of a face's normalized 5-point landmarks to/from the
// FaceDetection.LandmarksJson column. Compact array of {X,Y} in [0..1]; never
// contains storage identity. Parsing is tolerant (returns null on garbage).
public static class FaceLandmarksJson
{
    public static string? Serialize(IReadOnlyList<FaceLandmark>? landmarks)
        => landmarks is null || landmarks.Count == 0 ? null : JsonSerializer.Serialize(landmarks);

    public static IReadOnlyList<FaceLandmark>? Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<List<FaceLandmark>>(json);
            return parsed is { Count: > 0 } ? parsed : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
