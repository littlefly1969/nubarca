namespace NubArca.Api.Ai.Video.Faces;

// VFACE-01: the deterministic per-detection quality signal.
//
// There is no quality MODEL in this codebase (FaceDetection.FaceQualityScore is
// nullable and unwired for photos), and this slice explicitly does not introduce
// one. The score is therefore an honest, explainable heuristic over the two
// signals the detector already gives us:
//
//   quality = confidence × size_factor
//   size_factor = clamp(min(width, height) in pixels / reference, 0, 1)
//
// Rationale: a small face is both harder to recognise and a weaker tracking
// anchor, so it must not outweigh a large one when a track's representative
// detection and its quality-weighted aggregate are chosen. The result is always
// finite and inside [0, 1], which the database check constraint also enforces.
public static class VideoFaceQuality
{
    public static double Score(
        double? confidence, double faceWidthPixels, double faceHeightPixels, int referencePixels)
    {
        var c = confidence is { } value && double.IsFinite(value)
            ? Math.Clamp(value, 0d, 1d)
            : 0d;

        if (referencePixels <= 0 || !double.IsFinite(faceWidthPixels) || !double.IsFinite(faceHeightPixels))
        {
            return 0d;
        }

        var edge = Math.Min(faceWidthPixels, faceHeightPixels);
        if (edge <= 0)
        {
            return 0d;
        }

        var sizeFactor = Math.Clamp(edge / referencePixels, 0d, 1d);
        return Math.Clamp(c * sizeFactor, 0d, 1d);
    }
}
