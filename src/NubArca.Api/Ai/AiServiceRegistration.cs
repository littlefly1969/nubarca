using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NubArca.Api.Ai.Backends;
using NubArca.Api.Ai.Diagnostics;
using NubArca.Api.Ai.Faces;
using NubArca.Api.Ai.Jobs;
using NubArca.Api.Ai.Onnx;
using NubArca.Api.Ai.Onnx.Face;
using NubArca.Api.Ai.Photos;
using NubArca.Api.Ai.Resolution;
using NubArca.Api.Jobs;

namespace NubArca.Api.Ai;

// Phase 0B: registers the AI substrate service graph. Registration is inert by
// default — AiOptions.Enabled is false, the default provider is "none", and no
// real backend does work. Call this from any host that has an AppDbContext
// (web host, CLI/worker host, test fixture) so resolution behaves identically
// everywhere. Does NOT register a hosted worker and does NOT seed anything.
public static class AiServiceRegistration
{
    public static IServiceCollection AddAiSubstrate(this IServiceCollection services)
    {
        // Stateless singletons (no DB, no external dependency).
        services.AddSingleton<IAiVectorSerializer, AiVectorSerializer>();
        services.AddSingleton<IAiBackend, NoneAiBackend>();
        services.AddSingleton<IAiBackend, DeterministicAiBackend>();

        // Phase 2A: local ONNX image embedder (provider "onnx"). Single instance
        // (caches ONNX sessions, IDisposable) exposed as an IAiBackend so the
        // resolver can hand it out; stays unavailable until a model is present.
        services.AddSingleton<OnnxImagePreprocessor>();
        // Gate 3B: centralized in-process ONNX session construction/caching/disposal
        // (execution-provider + per-model device selection, FP32-GPU, OpenVINO native
        // init, sanitized readiness). Not yet wired into the embedders (Gate 3C).
        services.AddSingleton<IOnnxInferenceSessionFactory, OnnxInferenceSessionFactory>();
        // Gate 3C: install the OpenVINO native resolver + fail-closed ABI/conflict
        // guard + version diagnostics at startup when provider=openvino-direct
        // (no-op otherwise), so the OpenVINO-enabled core loads before any session.
        services.AddSingleton<OnnxDirectRuntimeInitializer>();
        services.AddHostedService(sp => sp.GetRequiredService<OnnxDirectRuntimeInitializer>());
        // Face AI milestone: run-once, compile-backed startup preload for the direct
        // face pipeline (detector + recognizer) with bounded synthetic validation.
        // Distinct liveness/readiness: readiness stays not-ready until BOTH models
        // compile and validate. No-op for onnxruntime / openvino-sidecar. Registered
        // AFTER OnnxDirectRuntimeInitializer so the native resolver/ABI guard runs
        // first. The state singleton backs the /health/ready readiness probe.
        services.AddSingleton<OnnxFacePreloadState>();
        services.AddSingleton<IOnnxFacePreloadState>(sp => sp.GetRequiredService<OnnxFacePreloadState>());
        services.AddHostedService<OnnxFacePreloadService>();
        services.AddSingleton<OnnxImageEmbedder>();
        services.AddSingleton<IAiBackend>(sp => sp.GetRequiredService<OnnxImageEmbedder>());
        services.AddSingleton<OnnxTextEmbedder>();
        services.AddSingleton<IAiBackend>(sp => sp.GetRequiredService<OnnxTextEmbedder>());

        // Evaluation-only local ONNX face-recognition backend (provider "onnx",
        // capabilities face-detection + face-embedding). Single instance (caches
        // ONNX sessions, IDisposable); stays unavailable until both model files
        // are present. Exposed as IAiBackend so the resolver can hand it out.
        services.AddSingleton<OnnxFacePreprocessor>();
        services.AddSingleton<OnnxFaceBackend>();
        services.AddSingleton<IAiBackend>(sp => sp.GetRequiredService<OnnxFaceBackend>());

        // Scoped services (use AppDbContext).
        services.AddScoped<IAiProfileRegistry, AiProfileRegistry>();
        services.AddScoped<IAiBackendResolver, AiBackendResolver>();
        services.AddScoped<IAiDiagnosticsWriter, AiDiagnosticsWriter>();
        services.AddScoped<IAiStatusService, AiStatusService>();
        services.AddScoped<AiDiagnosticsAggregator>();

        // Phase 1: photo similarity v0 — real backfill (writes BlobEmbedding) +
        // owner-private exact-scan similarity (no pgvector).
        services.AddScoped<PhotoEmbeddingBackfillService>();
        services.AddScoped<PhotoSimilarityService>();
        services.AddScoped<PhotoSemanticSearchService>();
        // Slice 100: physical-filter-first, semantic-ranked gallery page (TV
        // natural-language search). Ranks the active profile's text-tower query
        // ONLY inside the physically filtered candidate set.
        services.AddScoped<GallerySemanticQueryService>();

        // Slice 100: TV natural-language command interpreter (all LOCAL). The
        // deterministic IT/EN grammar is always available and is the default; the
        // decoder-LLM sidecar is an optional internal-only upgrade selected via
        // Ai:NaturalGallerySearch:Interpreter. Both feed the same deterministic
        // validation + owner-scoped person/date resolution.
        services.AddSingleton<NaturalGallery.INaturalGalleryCommandInterpreter,
            NaturalGallery.DeterministicGalleryCommandInterpreter>();
        services.AddHttpClient<NaturalGallery.INaturalGalleryCommandModelClient,
            NaturalGallery.HttpNaturalGalleryCommandModelClient>();
        services.AddScoped<NaturalGallery.INaturalGalleryCommandInterpreter,
            NaturalGallery.OnnxDecoderGalleryCommandInterpreter>();
        services.AddScoped<NaturalGallery.PersonNameResolver>();
        services.AddScoped<NaturalGallery.GalleryCommandValidator>(sp => new NaturalGallery.GalleryCommandValidator(
            sp.GetRequiredService<NaturalGallery.PersonNameResolver>(),
            sp.GetRequiredService<IOptions<AiOptions>>().Value.NaturalGallerySearch));
        services.AddScoped<NaturalGallery.NaturalGalleryCommandService>();

        // Photo-embedding profile lifecycle: explicit active-profile resolution
        // (read path) + aggregate coverage. Used by PhotoSimilarityService, the
        // backfill default selection, and the `ai photos embeddings …` CLI.
        services.AddScoped<PhotoEmbeddingProfileService>();

        // Phase 2B foundation: pgvector-backed photo similarity (raw-SQL gateway
        // to the dimension-specific vector table + HNSW). Unavailable on SQLite /
        // non-pgvector Postgres → callers fall back to exact-scan.
        services.AddScoped<PhotoVectorIndexService>();

        // Face Substrate v0: threshold settings (central, admin-editable extension
        // point) + validation, blob-level detection/embedding backfills, the
        // pgvector face gateway (512-dim), and coverage/diagnostics. Face
        // processing stays OFF by default (AiOptions flags all false).
        services.AddSingleton<IValidateOptions<AiOptions>, AiFaceOptionsValidator>();
        services.AddSingleton<IValidateOptions<AiOptions>, AiOnnxOptionsValidator>();
        // Settings are config defaults + persisted admin overrides (ai_settings),
        // so the provider is scoped (needs AppDbContext) and also exposed
        // concretely for the admin write path.
        services.AddScoped<FaceSettingsService>();
        services.AddScoped<IFaceSettingsProvider>(sp => sp.GetRequiredService<FaceSettingsService>());
        services.AddScoped<FaceVectorIndexService>();
        services.AddScoped<FaceDetectionBackfillService>();
        services.AddScoped<FaceEmbeddingBackfillService>();
        services.AddScoped<FaceCoverageService>();
        services.AddScoped<FaceDiagnosticsService>();
        services.AddScoped<FaceClusteringService>();
        services.AddScoped<PeopleService>();
        // UI-only high-quality face crops (derived cache; never an embedding source).
        services.AddScoped<FacePreviewService>();

        // Phase 2A: ONNX image-embedding evaluation harness (read-only, no writes).
        services.AddScoped<OnnxImageEvaluationService>();

        // ONNX face-recognition evaluation harness (read-only, no writes; powers
        // the `ai face …` CLI). Evaluation-only — no clustering/names/persistence.
        services.AddScoped<OnnxFaceEvaluationService>();

        // Phase 0C: skeleton AI backfill job handlers (Compute band, no-op).
        // Registered here so the web host, CLI/worker host, and test fixture all
        // pick them up identically wherever AddAiSubstrate() is called.
        services.AddScoped<IJobHandler, AiPhotosEmbeddingsBackfillJobHandler>();
        services.AddScoped<IJobHandler, AiDocumentsExtractBackfillJobHandler>();
        services.AddScoped<IJobHandler, AiDocumentsEmbeddingsBackfillJobHandler>();
        services.AddScoped<IJobHandler, AiFacesDetectBackfillJobHandler>();
        services.AddScoped<IJobHandler, AiFacesEmbeddingsBackfillJobHandler>();
        services.AddScoped<IJobHandler, AiFacesClusterBackfillJobHandler>();
        services.AddScoped<IJobHandler, AiTagsGenerateBackfillJobHandler>();

        // VSEM-01: canonical video temporal substrate. The service graph and the
        // options VALIDATOR live here so the web host, the CLI/worker host and
        // the test fixture behave identically; each host binds the
        // "Ai:VideoSegmentation" section itself (same convention as AiOptions).
        // DISABLED by default, so registration alone does nothing.
        services.AddSingleton<IValidateOptions<Video.VideoSemanticSegmentationOptions>,
            Video.VideoSemanticSegmentationOptionsValidator>();
        services.AddScoped<Video.IVideoSemanticSegmenter, Video.FfmpegVideoSemanticSegmenter>();
        services.AddScoped<Video.VideoSemanticSegmentationService>();
        services.AddScoped<Video.VideoSemanticSegmentationBackfillService>();
        services.AddScoped<Video.IVideoSemanticSegmentationScheduler, Video.VideoSemanticSegmentationScheduler>();
        services.AddScoped<IJobHandler, Video.AiVideosSegmentsBackfillJobHandler>();

        // VSEM-02: canonical SigLIP2 embeddings of the temporal samples. Same
        // host-parity rules as VSEM-01: the graph and the options VALIDATOR
        // live here; each host binds "Ai:VideoVisualEmbeddings" itself.
        // DISABLED by default, so registration alone does nothing.
        services.AddSingleton<IValidateOptions<Video.VideoVisualEmbeddingOptions>,
            Video.VideoVisualEmbeddingOptionsValidator>();
        services.AddScoped<Video.FfmpegVideoSemanticFrameExtractor>();
        services.AddScoped<Video.IVideoSemanticFrameExtractor>(
            sp => sp.GetRequiredService<Video.FfmpegVideoSemanticFrameExtractor>());
        // VFACE-01 consumes the STREAMING form of the very same extractor, so
        // staging/timeout/cleanup can never diverge between the two callers.
        services.AddScoped<Video.IVideoSemanticFrameStreamExtractor>(
            sp => sp.GetRequiredService<Video.FfmpegVideoSemanticFrameExtractor>());
        services.AddScoped<Video.VideoSemanticSampleVectorIndexService>();
        services.AddScoped<Video.VideoSemanticEmbeddingService>();
        services.AddScoped<Video.VideoSemanticEmbeddingBackfillService>();
        services.AddScoped<Video.IVideoSemanticEmbeddingScheduler, Video.VideoSemanticEmbeddingScheduler>();
        services.AddScoped<IJobHandler, Video.AiVideosEmbeddingsBackfillJobHandler>();

        // VFACE-01: canonical video face TRACKS. Same host-parity rules as
        // VSEM-01/02: the graph and the options VALIDATOR live here; each host
        // binds "Ai:VideoFaceAnalysis" itself. DISABLED by default, so
        // registration alone does nothing. Independent of the VSEM-02 embedding
        // graph — neither capability waits for the other.
        services.AddSingleton<IValidateOptions<Video.Faces.VideoFaceAnalysisOptions>,
            Video.Faces.VideoFaceAnalysisOptionsValidator>();
        services.AddScoped<Video.Faces.VideoFaceAnalysisService>();
        services.AddScoped<Video.Faces.VideoFaceAnalysisBackfillService>();
        services.AddScoped<Video.Faces.IVideoFaceAnalysisScheduler, Video.Faces.VideoFaceAnalysisScheduler>();
        services.AddScoped<IJobHandler, Video.Faces.AiVideosFacesBackfillJobHandler>();

        // VFACE-02: the OWNER-LEVEL surface over those canonical tracks —
        // decisions, suggestions, person video results and co-presence. Read/write
        // only; no job, no automatic assignment, no automatic person creation.
        services.AddScoped<Faces.Video.VideoFaceTrackPeopleService>();
        services.AddScoped<Faces.Video.VideoFaceTrackIdentitySuggestionService>();

        // VSEM-04: read-only operational diagnostics over the VSEM-01/02
        // substrate above (segmentation + embedding + pgvector coverage). No
        // new state, no new job — an aggregation seam only.
        services.AddScoped<Video.VideoSemanticDiagnosticsService>();

        // VSEM-03: unified photo+video semantic retrieval. Registered here
        // (like PhotoSemanticSearchService) so the web host, the CLI/worker
        // host and the test fixture resolve the same graph. Read-only surface
        // over the existing photo + VSEM-02 video vector layers.
        services.AddScoped<NubArca.Api.Media.Semantic.SemanticMediaCandidateService>();
        services.AddScoped<NubArca.Api.Media.Semantic.MediaSemanticSearchService>();

        // SEARCH-SEM-01: the shared result policy is scoped (it only reads
        // options), but the ranking cache is a SINGLETON — a per-request cache
        // would never be hit and page 2 would re-rank the whole library, which
        // is the entire cost this slice exists to pay once.
        services.AddScoped<NubArca.Api.Media.Semantic.SemanticResultPolicy>();
        services.AddSingleton<NubArca.Api.Media.Semantic.SemanticRankingCache>();

        return services;
    }
}
