namespace NubArca.Api.Ai.Onnx.Face;

// Evaluation-only catalog of local ONNX face-recognition model packages
// (detector + recognition), analogous to OnnxImageModels for photo similarity.
// Preprocessing/postprocessing config lives in CODE (versioned, reviewable); an
// AiProfile links to a catalog entry via its short ConfigHash (== catalog key ==
// package directory name). NO weights are committed and NOTHING is auto-
// downloaded — models are loaded from
//   Ai__Onnx__ModelDir/<PackageSubdir>/<DetectorFile>
//   Ai__Onnx__ModelDir/<PackageSubdir>/<RecognitionFile>
// at runtime and the backend stays UNAVAILABLE until both files are present.
//
// A "combined face-recognition" package = a SCRFD/RetinaFace detector (bbox +
// 5-point landmarks) followed by an ArcFace recognition embedder (512-d, cosine).
// See docs/ai-face-model-onnx-evaluation.md for the export path, license notes,
// and the preprocessing assumptions this harness exists to VALIDATE before any
// production face pipeline is built.
public sealed record OnnxFaceModelConfig(
    string Key,                     // catalog key == package dir name == AiModel.Key
    string PackageSubdir,           // subfolder under Ai__Onnx__ModelDir
    string DetectorFile,            // SCRFD/RetinaFace detector ONNX file
    string RecognitionFile,         // ArcFace recognition ONNX file
    int DetectorInputSize,          // square detector input edge (letterboxed), px
    float DetectorScoreThreshold,   // min detection confidence to keep
    float DetectorNmsThreshold,     // IoU threshold for non-max suppression
    float[] DetectorMean,           // per-channel RGB mean, 0..255 scale (SCRFD: 127.5)
    float[] DetectorStd,            // per-channel RGB std,  0..255 scale (SCRFD: 128)
    int RecognitionInputSize,       // aligned face crop edge (ArcFace: 112), px
    float[] RecognitionMean,        // per-channel RGB mean, 0..255 scale (ArcFace: 127.5)
    float[] RecognitionStd,         // per-channel RGB std,  0..255 scale (ArcFace: 127.5)
    int LandmarkCount,              // landmarks required for alignment (5)
    int Dimension,                  // recognition embedding dimension (512)
    string DistanceMetric,          // cosine
    string Capability,              // display-only combined capability
    string LicenseNote);            // human-readable licensing caveat

public static class OnnxFaceCapabilities
{
    // Display label for a combined detector+recognition package. The substrate
    // AiProfile row uses the fine-grained AiCapabilities.FaceEmbedding for
    // resolution; this string is for operator-facing CLI output only.
    public const string FaceRecognition = "face-recognition";
}

public static class OnnxFaceModels
{
    // InsightFace CODE is MIT, but the PRETRAINED model packages (antelopev2,
    // buffalo_l) are published for NON-COMMERCIAL research / personal use unless
    // separately licensed. NubArca does NOT assume any commercial grant. Verify
    // the exact terms on the model card before any commercial deployment.
    private const string InsightFaceLicenseNote =
        "InsightFace code MIT; pretrained model packages are non-commercial "
        + "research/personal use unless separately licensed. Commercial use NOT assumed.";

    // SCRFD detectors are trained with input_mean=127.5, input_std=128.0 (RGB).
    private static readonly float[] ScrfdMean = { 127.5f, 127.5f, 127.5f };
    private static readonly float[] ScrfdStd = { 128f, 128f, 128f };

    // ArcFace recognition models use input_mean=input_std=127.5 (RGB → [-1, 1]).
    private static readonly float[] ArcFaceMean = { 127.5f, 127.5f, 127.5f };
    private static readonly float[] ArcFaceStd = { 127.5f, 127.5f, 127.5f };

    // Catalog key == package directory name == AiModel.Key.
    public const string Antelopev2Key = "antelopev2";
    public const string BuffaloLKey = "buffalo_l";

    public static readonly IReadOnlyDictionary<string, OnnxFaceModelConfig> Catalog =
        new Dictionary<string, OnnxFaceModelConfig>(StringComparer.Ordinal)
        {
            // Primary quality candidate: RetinaFace/SCRFD-10GF detector +
            // ResNet100 Glint360K ArcFace recognition.
            [Antelopev2Key] = new(
                Key: Antelopev2Key, PackageSubdir: Antelopev2Key,
                DetectorFile: "scrfd_10g_bnkps.onnx", RecognitionFile: "glintr100.onnx",
                DetectorInputSize: 640, DetectorScoreThreshold: 0.5f, DetectorNmsThreshold: 0.4f,
                DetectorMean: ScrfdMean, DetectorStd: ScrfdStd,
                RecognitionInputSize: 112, RecognitionMean: ArcFaceMean, RecognitionStd: ArcFaceStd,
                LandmarkCount: 5, Dimension: 512, DistanceMetric: "cosine",
                Capability: OnnxFaceCapabilities.FaceRecognition, LicenseNote: InsightFaceLicenseNote),

            // Stable fallback candidate: SCRFD-10GF detector + ResNet50
            // WebFace600K ArcFace recognition.
            [BuffaloLKey] = new(
                Key: BuffaloLKey, PackageSubdir: BuffaloLKey,
                DetectorFile: "det_10g.onnx", RecognitionFile: "w600k_r50.onnx",
                DetectorInputSize: 640, DetectorScoreThreshold: 0.5f, DetectorNmsThreshold: 0.4f,
                DetectorMean: ScrfdMean, DetectorStd: ScrfdStd,
                RecognitionInputSize: 112, RecognitionMean: ArcFaceMean, RecognitionStd: ArcFaceStd,
                LandmarkCount: 5, Dimension: 512, DistanceMetric: "cosine",
                Capability: OnnxFaceCapabilities.FaceRecognition, LicenseNote: InsightFaceLicenseNote),
        };

    // Evaluation profile keys (NOT made default by seeding).
    public const string Antelopev2ProfileKey = "face-insightface-antelopev2-v1";
    public const string BuffaloLProfileKey = "face-insightface-buffalo-l-v1";

    // Profile key → catalog key (stored in AiProfile.ConfigHash at seed time).
    public static readonly IReadOnlyDictionary<string, string> ProfileToCatalogKey =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [Antelopev2ProfileKey] = Antelopev2Key,
            [BuffaloLProfileKey] = BuffaloLKey,
        };

    // Resolve a catalog config for a profile: prefer the profile's ConfigHash
    // (the catalog key), else derive from the profile key. Returns null when the
    // profile is not a known ONNX face profile.
    public static OnnxFaceModelConfig? ResolveConfig(string? configHash, string profileKey)
    {
        if (!string.IsNullOrWhiteSpace(configHash) && Catalog.TryGetValue(configHash, out var byHash))
        {
            return byHash;
        }

        if (ProfileToCatalogKey.TryGetValue(profileKey, out var catalogKey)
            && Catalog.TryGetValue(catalogKey, out var byProfile))
        {
            return byProfile;
        }

        return null;
    }
}
