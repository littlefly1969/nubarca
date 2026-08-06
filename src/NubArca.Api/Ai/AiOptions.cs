using Microsoft.Extensions.Options;
using NubArca.Api.Ai.Onnx;

namespace NubArca.Api.Ai;

// Phase 0A: configuration surface for the AI substrate. AI is OFF by default
// and Phase 0A contains NO inference, NO ONNX, NO external calls, NO pgvector
// and NO jobs — these options exist only so later phases can flip behaviour on
// without further schema/config churn. Bound from the "Ai" configuration
// section (env: Ai__Enabled, Ai__Provider, Ai__Onnx__ModelDir, ...).
public class AiOptions
{
    public const string SectionName = "Ai";

    // Master switch. When false the whole substrate is inert (the default).
    public bool Enabled { get; set; } = false;

    // Default provider key for the substrate ("none" | "deterministic" | "onnx"
    // | "external"). Phase 0A only honours "none"; the others are reserved.
    public string Provider { get; set; } = "none";

    // Active photo-similarity embedding profile (stable AiProfile.Key, never a
    // GUID). This is the EXPLICIT lifecycle control for which profile photo
    // similarity reads (PhotoSimilarityService) and which the photo-embeddings
    // backfill writes when no `--profile` is given:
    //   - set   → that exact profile is used (no implicit "latest model", no
    //             mixing across profiles); if it is missing/inactive/wrong
    //             capability/wrong dimension/missing model the read path returns
    //             a clean empty result and the operator CLI a sanitized reason.
    //   - unset → DOCUMENTED, explicit fallback to the capability's default
    //             profile (the partial-unique default-per-capability profile,
    //             i.e. the deterministic image profile in the current lab).
    // Switching models is therefore a config change + restart, never automatic.
    // See docs/ai-photo-profile-lifecycle.md.
    public string? PhotoSimilarityProfileKey { get; set; }

    // Active face-recognition profile (stable AiProfile.Key) for the face model
    // EVALUATION harness (`ai face …`). This branch is evaluation-only: it never
    // clusters, names, or persists face artifacts, and face processing stays OFF
    // by default (FaceDetectionEnabled / FaceEmbeddingsEnabled / FaceClustering-
    // Enabled below all default false). When unset, `ai face` commands require an
    // explicit `--profile <key>`. Mirrors PhotoSimilarityProfileKey's discipline:
    // model choice is a config change, never automatic.
    public string? FaceProfileKey { get; set; }

    // Cooperative-compute budgets, mirrored on the same idea as JobsOptions'
    // maintenance slicing. Unused in Phase 0A (no jobs yet) but bound so they
    // are tunable the moment Phase 0C adds handlers.
    public int MaxConcurrency { get; set; } = 1;
    public int TimeoutSeconds { get; set; } = 30;
    public int ComputeSliceSeconds { get; set; } = 30;
    public int ComputeSliceItemBudget { get; set; } = 100;

    // Per-capability enable flags. All OFF by default. Phase 0A does not act on
    // them; they make the eventual per-capability rollout a config change only.
    public bool ImageEmbeddingsEnabled { get; set; } = false;
    public bool DocumentExtractionEnabled { get; set; } = false;
    public bool DocumentEmbeddingsEnabled { get; set; } = false;
    public bool FaceDetectionEnabled { get; set; } = false;
    public bool FaceEmbeddingsEnabled { get; set; } = false;
    public bool FaceClusteringEnabled { get; set; } = false;
    public bool TagsEnabled { get; set; } = false;

    // Future-ready nested provider config. Present and bindable now, activated
    // later (Phase 0C / Phase 2). Empty by default.
    public AiOnnxOptions Onnx { get; set; } = new();
    public AiExternalOptions External { get; set; } = new();

    // Face Substrate v0 tunables: similarity thresholds + face-job safety caps.
    // Admin-editable extension point (see IFaceSettingsProvider); provisional
    // defaults from the antelopev2 evaluation. Bound from "Ai:Face:*".
    public AiFaceOptions Face { get; set; } = new();

    // TV natural-language gallery search + filter interpreter. All LOCAL; no
    // cloud. Bound from "Ai:NaturalGallerySearch:*" (env
    // Ai__NaturalGallerySearch__*). Auto-binds — no extra Configure<> call.
    public AiNaturalGallerySearchOptions NaturalGallerySearch { get; set; } = new();
}

// TV natural-language gallery command interpreter + semantic-search tunables.
// The interpreter turns a typed natural-language request into a STRICT
// structured draft (never SQL/URLs/ids). Physical gallery filters are always
// applied BEFORE semantic ranking; semantic ranking reduces the physically
// filtered candidate set to the best Top-K. Everything runs on-device.
public class AiNaturalGallerySearchOptions
{
    public const string SectionName = "NaturalGallerySearch";

    // Which command interpreter backend produces the structured draft:
    //   "deterministic" → the built-in IT/EN grammar interpreter (no weights,
    //                     bounded, warm, and the dev/test + offline default).
    //   "onnx"          → the isolated internal-only decoder-LLM sidecar
    //                     (INaturalGalleryCommandModelClient over the internal
    //                     network); falls back to deterministic when the sidecar
    //                     is unreachable and FallbackToDeterministic is true.
    public string Interpreter { get; set; } = "deterministic";

    // When the "onnx" sidecar is selected but unavailable/times out, fall back
    // to the deterministic interpreter instead of returning model-unavailable.
    public bool FallbackToDeterministic { get; set; } = true;

    // Base URL of the internal-only decoder-LLM sidecar (e.g.
    // http://nubarca-nlu:8090). Empty → onnx interpreter is unavailable.
    // Never a public host; reached only over the internal Docker network.
    public string? ModelServiceBaseUrl { get; set; }

    // Hard timeout for a single interpret call to the sidecar (server-side).
    public int InterpretTimeoutSeconds { get; set; } = 12;

    // Max characters accepted in a natural-language command (rejected above).
    public int MaxCommandLength { get; set; } = 400;

    // Semantic Top-K reduction of the physically filtered candidate set.
    // DefaultTopK is used when the command produces a semantic query and the
    // client does not (and cannot) pick a number; MaximumTopK is the hard
    // server ceiling; MinimumTopK the floor. See the query design docs.
    public int DefaultTopK { get; set; } = 300;
    public int MaximumTopK { get; set; } = 500;
    public int MinimumTopK { get; set; } = 1;

    // Upper bound on the physically filtered candidate id set materialised for
    // semantic ranking. A more selective physical filter keeps this small; a
    // very broad filter is capped here (disclosed via a warning, never silent).
    public int MaxSemanticCandidates { get; set; } = 20000;

    // EXPERIMENTAL, OFF by default. When true the interpreter MAY also emit an
    // English translation of the semantic query and the search path MAY fuse
    // original-language + English retrieval. Do not enable without a benchmark
    // showing a real retrieval improvement (SigLIP2 is already multilingual).
    public bool UseEnglishSemanticTranslation { get; set; } = false;

    // Dev-only: echo the raw command text into debug logs. MUST stay false in
    // production — command text is personal and is never logged/audited.
    public bool DebugLogCommands { get; set; } = false;

    public int ClampTopK(int? requested)
    {
        var value = requested ?? DefaultTopK;
        var min = Math.Max(1, MinimumTopK);
        var max = Math.Max(min, MaximumTopK);
        return Math.Clamp(value, min, max);
    }
}

// Local ONNX backend configuration. Reserved for a later phase.
public class AiOnnxOptions
{
    // Directory holding ONNX model files. Empty until a local backend is wired.
    public string? ModelDir { get; set; }

    // CPU-safety: cap ONNX Runtime intra-op threads for face inference. The eval
    // host previously power-cut under a full-throttle benchmark (ORT pins every
    // core). Null / <= 0 => ORT default (all cores) — a real backfill SHOULD set
    // this conservatively (e.g. 4). Bound from "Ai:Onnx:IntraOpThreads".
    public int? IntraOpThreads { get; set; }

    // Execution provider (bound from "Ai:Onnx:ExecutionProvider"):
    //   "onnxruntime"      → in-process ONNX Runtime CPU (safe fallback / default)
    //   "openvino-direct"  → in-process OpenVINO EP (see Ai:Onnx:OpenVino:*)
    // Unknown values are REJECTED at startup — never silently treated as CPU.
    // The legacy Python sidecar providers were removed with the SigLIP direct
    // milestone.
    public string ExecutionProvider { get; set; } = "onnxruntime";

    // In-process OpenVINO EP settings — used only for "openvino-direct".
    // Bound from "Ai:Onnx:OpenVino:*".
    public AiOnnxOpenVinoOptions OpenVino { get; set; } = new();
}

// In-process OpenVINO Execution Provider configuration (provider "openvino-direct").
// Per-model device placement so API and worker can use independent CPU/GPU
// assignments without changing embedding contracts.
public class AiOnnxOpenVinoOptions
{
    // Directory holding the OpenVINO-enabled libonnxruntime.so.<ver> + provider
    // libraries produced by scripts/openvino-direct. Required for "openvino-direct".
    public string? NativeDir { get; set; }

    // Optional OpenVINO model-compile cache directory (writable) to cut cold start.
    public string? CacheDir { get; set; }

    // Per-model device. Single-device models accept "CPU" or "GPU". The image tower
    // additionally accepts "DUAL:CPU,GPU": a bounded CPU+GPU tandem (two exclusive
    // FP32 sessions behind a dispatcher) that needs two worker job slots
    // (Jobs__MaxConcurrentJobs=2) to feed both devices; the GPU leg degrades
    // gracefully to CPU-only when unavailable. See
    // docs/model-deployment/openvino-siglip2-benchmark-2026-07.md.
    public string PhotoImageDevice { get; set; } = "CPU";
    public string PhotoTextDevice { get; set; } = "CPU";
    public string FaceDetectorDevice { get; set; } = "CPU";
    public string FaceRecognizerDevice { get; set; } = "CPU";

    // GPU inference precision. MUST be FP32 for output-equivalence with the CPU
    // reference / sidecar (FP16, the OpenVINO GPU default, measurably diverges).
    public string GpuPrecision { get; set; } = "FP32";

    public string DeviceFor(OnnxModel model) => model switch
    {
        OnnxModel.PhotoImage => PhotoImageDevice,
        OnnxModel.PhotoText => PhotoTextDevice,
        OnnxModel.FaceDetector => FaceDetectorDevice,
        OnnxModel.FaceRecognizer => FaceRecognizerDevice,
        _ => "CPU",
    };
}

public sealed class AiOnnxOptionsValidator : IValidateOptions<AiOptions>
{
    private static bool IsValidDevice(string? device) =>
        string.Equals(device?.Trim(), "CPU", StringComparison.OrdinalIgnoreCase)
        || string.Equals(device?.Trim(), "GPU", StringComparison.OrdinalIgnoreCase);

    public ValidateOptionsResult Validate(string? name, AiOptions options)
    {
        var onnx = options.Onnx;
        if (!OnnxExecutionProviders.IsKnown(onnx.ExecutionProvider))
        {
            return ValidateOptionsResult.Fail(
                "Ai:Onnx:ExecutionProvider must be 'onnxruntime' or 'openvino-direct'.");
        }

        var provider = OnnxExecutionProviders.Normalize(onnx.ExecutionProvider);

        if (provider == OnnxExecutionProviders.OpenVinoDirect)
        {
            var ov = onnx.OpenVino;
            if (string.IsNullOrWhiteSpace(ov.NativeDir))
            {
                return ValidateOptionsResult.Fail(
                    "Ai:Onnx:OpenVino:NativeDir is required when 'openvino-direct' is selected.");
            }
            foreach (var (label, device, allowDual) in new[]
            {
                ("PhotoImageDevice", ov.PhotoImageDevice, true),
                ("PhotoTextDevice", ov.PhotoTextDevice, false),
                ("FaceDetectorDevice", ov.FaceDetectorDevice, false),
                ("FaceRecognizerDevice", ov.FaceRecognizerDevice, false),
            })
            {
                if (!IsValidDevice(device) && !(allowDual && OnnxDevice.IsDual(device)))
                {
                    return ValidateOptionsResult.Fail(allowDual
                        ? $"Ai:Onnx:OpenVino:{label} must be 'CPU', 'GPU' or 'DUAL:CPU,GPU'."
                        : $"Ai:Onnx:OpenVino:{label} must be 'CPU' or 'GPU'.");
                }
            }
            if (!string.Equals(ov.GpuPrecision?.Trim(), "FP32", StringComparison.OrdinalIgnoreCase))
            {
                return ValidateOptionsResult.Fail(
                    "Ai:Onnx:OpenVino:GpuPrecision must be 'FP32' (required for output-equivalence).");
            }
        }

        return ValidateOptionsResult.Success;
    }
}

// External (hosted API) backend configuration. Reserved for a later phase.
// ApiKeyRef is a NAME/reference to a secret, never the secret value itself.
public class AiExternalOptions
{
    public string? BaseUrl { get; set; }
    public string? ApiKeyRef { get; set; }
}

// Face Substrate v0 tunables. Similarity thresholds are provisional (from the
// antelopev2 cosine evaluation) and MUST stay admin-editable — they are consumed
// via IFaceSettingsProvider, never hardcoded deep in clustering/search code.
// Clustering itself is a LATER branch; these values only define where its future
// thresholds come from + how they validate. Bound from "Ai:Face:*".
public class AiFaceOptions
{
    // Conservative automatic-clustering threshold: cosine >= this groups faces
    // into one person (clean clusters; large-age-gap photos may split).
    public double ClusterSimilarityThreshold { get; set; } = 0.40;

    // Wider candidate/review threshold: broader matches surfaced for human review
    // (catches moderate age gaps; risks look-alike-relative merges).
    public double CandidateSimilarityThreshold { get; set; } = 0.30;

    // Default similarity threshold for future UI person/face search.
    public double SearchDefaultSimilarityThreshold { get; set; } = 0.35;

    // Hard bounds a user-adjustable search threshold must stay within.
    public double SearchMinSimilarity { get; set; } = 0.20;
    public double SearchMaxSimilarity { get; set; } = 0.95;

    // CPU-safety: max faces persisted per image (guards a pathological detector
    // result set). <= 0 disables the cap.
    public int MaxFacesPerImage { get; set; } = 50;

    // Face-preview crop padding as a fraction of the face box added on EACH side
    // (0.15 = 15%). This makes the preview crop the SAME normalized FaceDetection
    // bounding box the context viewer overlays, expanded by this margin per side,
    // so the chip matches the box the user sees. Clamped to [0, 3] at use.
    // UI-only; does not affect embeddings.
    public double PreviewPaddingPercent { get; set; } = 0.15;

    // ---- Clustering strategy -------------------------------------------------
    // "exact"        → bounded O(n^2) union-find (safety-capped at MaxFacesToCluster,
    //                  the historical path; kept as fallback + test oracle).
    // "pgvector_knn" → scalable HNSW kNN over face_embedding_vectors_512: each
    //                  eligible face finds its top-K nearest ELIGIBLE neighbours via
    //                  the cosine index, edges above threshold build a sparse graph,
    //                  connected components become clusters. Scales to 100k+ faces.
    // Default is pgvector_knn; when the pgvector backend is unavailable (SQLite /
    // non-pgvector Postgres) the service AUTOMATICALLY falls back to exact, so this
    // default is safe everywhere.
    public string ClusteringMode { get; set; } = FaceClusteringModes.PgvectorKnn;

    // kNN neighbours fetched per anchor face (graph out-degree cap). Higher helps
    // large per-person face sets stay mutually linked under mutual-kNN.
    public int KnnNeighbors { get; set; } = 60;

    // hnsw.ef_search used for the clustering neighbour queries (recall vs. speed).
    public int KnnEfSearch { get; set; } = 100;

    // Edge is kept / a component is "tight" (Suggested) when cohesion >= this.
    public double KnnMinSimilarity { get; set; } = 0.40;

    // Graph edges form at >= this (looser, so transitive components can form); a
    // component is Suggested only if its cohesion reaches KnnMinSimilarity, else
    // NeedsReview.
    public double KnnCandidateSimilarity { get; set; } = 0.30;

    // Hard cap on eligible anchor faces processed in one owner run (memory/CPU).
    public int KnnMaxEligibleFacesPerRun { get; set; } = 100000;

    // Components smaller than this are not surfaced as clusters.
    public int KnnMinClusterSize { get; set; } = 2;

    // Optional safety cap: components larger than this are marked NeedsReview
    // (likely an over-merge). 0 = no cap.
    public int KnnMaxClusterSize { get; set; } = 0;

    // Louvain community-detection resolution γ. 1.0 = standard modularity. Values
    // > 1 favour SMALLER, tighter communities (splits dense over-merged blobs into
    // person-sized groups); values < 1 favour larger communities. Admin-editable
    // (overlaid by the ai_settings override, like the similarity thresholds).
    public double KnnLouvainResolution { get; set; } = 1.0;
}

public static class FaceClusteringModes
{
    public const string Exact = "exact";
    public const string PgvectorKnn = "pgvector_knn";

    public static bool IsKnown(string? mode) =>
        string.Equals(mode, Exact, StringComparison.OrdinalIgnoreCase)
        || string.Equals(mode, PgvectorKnn, StringComparison.OrdinalIgnoreCase);
}
