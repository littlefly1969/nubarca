namespace NubArca.Api.Plates;

// Provider selection for the Plates model pipelines. Kept SEPARATE from the AI
// substrate's AiProviders vocabulary: Plates chooses its own runner per pipeline.
// Production defaults to Disabled; the deterministic dev/test runner and the real
// ONNX/existing-detector runners are opt-in via config.

public enum PlateAlprProvider
{
    // No ALPR runner: an analysis request records a safe model_not_configured
    // outcome (an environment/config state, never a content failure).
    Disabled = 0,

    // The deterministic, non-semantic dev/test pipeline (Slice 2). Reproducible
    // fixed detections from the bytes; never a real recognizer.
    DeterministicDev = 1,

    // In-process ONNX detector + OCR (Slice 4). Requires real model files under
    // the configured paths; missing/incompatible models surface as safe errors.
    Onnx = 2,
}

public enum PlateFaceRedactionProvider
{
    // No face detector: blurFaces=true returns a safe not-configured error and
    // NEVER serves the unredacted image.
    Disabled = 0,

    // The deterministic, non-semantic dev/test detector (Slice 3): a fixed box.
    DeterministicDev = 1,

    // Reuses NubArca's existing face-box detector (the AI substrate's ONNX
    // SCRFD IFaceDetector) for BOUNDING BOXES ONLY — no embeddings, clusters,
    // people, or FaceDetection rows. Boxes feed PlateFaceRedactionBox + redaction.
    ExistingNubArcaFaceDetector = 2,

    // Optional/future: a dedicated ONNX face detector bound to a Plates-owned
    // model file. Not implemented in this slice — selecting it reports unavailable.
    OnnxDedicatedFaceDetector = 3,
}

// Parses the string config values into the enums with a backward-compatible
// fallback to the legacy Enabled bool (so Slice 2/3 config — Enabled=true, no
// Provider — keeps meaning "deterministic dev/test").
public static class PlateProviderParsing
{
    public static PlateAlprProvider ResolveAlpr(string? provider, bool enabled)
    {
        if (TryParseAlpr(provider, out var parsed))
        {
            return parsed;
        }
        return enabled ? PlateAlprProvider.DeterministicDev : PlateAlprProvider.Disabled;
    }

    public static PlateFaceRedactionProvider ResolveFaceRedaction(string? provider, bool enabled)
    {
        if (TryParseFaceRedaction(provider, out var parsed))
        {
            return parsed;
        }
        return enabled ? PlateFaceRedactionProvider.DeterministicDev : PlateFaceRedactionProvider.Disabled;
    }

    public static bool TryParseAlpr(string? provider, out PlateAlprProvider result)
    {
        result = PlateAlprProvider.Disabled;
        if (string.IsNullOrWhiteSpace(provider))
        {
            return false;
        }
        return Enum.TryParse(provider.Trim(), ignoreCase: true, out result)
            && Enum.IsDefined(result);
    }

    public static bool TryParseFaceRedaction(string? provider, out PlateFaceRedactionProvider result)
    {
        result = PlateFaceRedactionProvider.Disabled;
        if (string.IsNullOrWhiteSpace(provider))
        {
            return false;
        }
        return Enum.TryParse(provider.Trim(), ignoreCase: true, out result)
            && Enum.IsDefined(result);
    }
}
