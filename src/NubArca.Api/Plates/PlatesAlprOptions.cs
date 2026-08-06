namespace NubArca.Api.Plates;

// Configuration for the owner-private ALPR (license-plate recognition) pipeline.
// Bound from the "Plates:Alpr" section (env: Plates__Alpr__*). Intentionally
// SEPARATE from Ai:Face*, People, Party:FaceSearch*, Gallery, and TV config —
// plate recognition shares no identity model or profile registry with faces.
//
// Disabled by default. This slice ships only a deterministic dev/test pipeline
// (no ONNX weights committed). Production MUST keep Enabled=false until a real
// detector/OCR runner is deployed with valid model paths.
public sealed class PlatesAlprOptions
{
    public const string SectionName = "Plates:Alpr";

    public bool Enabled { get; set; }

    // Runner selection: Disabled | DeterministicDev | Onnx. When empty, falls
    // back to the legacy Enabled bool (Enabled=true → DeterministicDev). See
    // PlateProviderParsing.ResolveAlpr.
    public string Provider { get; set; } = string.Empty;

    public string ProfileKey { get; set; } = "plate-alpr-v1";

    // ---- ONNX detector (Slice 4) ------------------------------------------
    // Model file path for the ONNX plate detector. Empty → the Onnx provider is
    // unavailable (safe error). Never logged in full / exposed (basename only).
    public string DetectorModelPath { get; set; } = string.Empty;
    // Detector output contract. "Yolo" is the supported family this slice.
    public string DetectorModelKind { get; set; } = "Yolo";
    public int DetectorInputWidth { get; set; } = 640;
    public int DetectorInputHeight { get; set; } = 640;
    public double DetectorConfidenceThreshold { get; set; } = 0.35;
    public double DetectorNmsThreshold { get; set; } = 0.45;

    // ---- ONNX OCR (Slice 4) -----------------------------------------------
    public string OcrModelPath { get; set; } = string.Empty;
    public string OcrModelKind { get; set; } = "FastPlateOcr";
    public int OcrInputWidth { get; set; } = 160;
    public int OcrInputHeight { get; set; } = 40;
    // CTC alphabet; the model's blank class is index 0 by convention (see
    // PlateCtcDecoder). Never country-specific substitution in this slice.
    public string OcrAlphabet { get; set; } = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";

    public double MinPlateConfidence { get; set; } = 0.35;
    public double MinOcrConfidence { get; set; } = 0.30;
    public long MaxImagePixels { get; set; } = 25_000_000;
    public int MaxDetectionsPerImage { get; set; } = 32;

    // Reserved for a future parallel worker; the current job engine processes one
    // job at a time (see JobsOptions.MaxConcurrentJobs), so this is advisory.
    public int WorkerConcurrency { get; set; } = 1;

    // Effective runner after applying the Provider/Enabled fallback.
    public PlateAlprProvider ResolveProvider() => PlateProviderParsing.ResolveAlpr(Provider, Enabled);
}
