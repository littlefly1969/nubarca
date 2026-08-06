using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Plates.Alpr;
using NubArca.Api.Plates.Redaction;

namespace NubArca.Api.Plates;

// SINGLE SOURCE OF TRUTH for the owner-private Plates (Targhe) service graph,
// called by ALL hosts: the web API (Program.cs), the CLI/worker host
// (CliEntryPoint) — which runs the ALPR analysis job — and the test fixture
// (SqliteWebApplicationFactory). Keeping one list prevents the "worker has no
// handler/service for the enqueued plates.analyze job" divergence.
//
// Provider-selected pipelines (Slice 4): ALPR = DeterministicDev | Onnx; face
// redaction = DeterministicDev | ExistingNubArcaFaceDetector. Production
// defaults keep both disabled; no ONNX weights are committed. The ALPR pipeline
// shares NO model/profile with the AI face substrate; the face-redaction
// existing-detector option REUSES the AI face-box detector for boxes only (no
// FaceDetection/embeddings/clusters/people).
public static class PlatesServiceRegistration
{
    public static IServiceCollection AddNubArcaPlates(this IServiceCollection services)
    {
        services.AddScoped<IPlateImageService, PlateImageService>();
        services.AddScoped<IPlateAnalysisService, PlateAnalysisService>();

        // Stateless deterministic ALPR backends (dev/test implementation).
        services.AddSingleton<IPlateDetector, DeterministicPlateDetector>();
        services.AddSingleton<IPlateOcrReader, DeterministicPlateOcrReader>();

        // Privacy-only face redaction (Slice 3 + Slice 4 providers). Separate from
        // People/Face identity: no embeddings/clusters/persons, no cross-owner
        // data. Provider selection (Plates:FaceRedaction:Provider):
        //   DeterministicDev              -> deterministic dev/test detector
        //   ExistingNubArcaFaceDetector -> reuse the AI face-box detector (boxes
        //                                    only; no FaceDetection/embeddings)
        // Master switch Plates:FaceRedaction:Enabled gates the whole feature.
        services.AddSingleton<DeterministicPlateFaceRedactionDetector>();
        services.AddScoped<ExistingNubArcaPlateFaceBoxDetector>();
        services.AddScoped<IPlateFaceRedactionDetector, PlateFaceRedactionDetectorSelector>();
        services.AddSingleton<ImageRedactionRenderer>();
        services.AddScoped<IPlateFaceRedactionService, PlateFaceRedactionService>();
        services.AddScoped<IPlateRedactedMediaService, PlateRedactedMediaService>();

        // ALPR provider selection (Plates:Alpr:Provider): DeterministicDev | Onnx.
        // The deterministic + ONNX pipelines are registered as themselves and the
        // selector is the single IPlateAnalysisPipeline the worker depends on.
        services.AddSingleton<OnnxRuntimeSessionCache>();
        services.AddSingleton<DeterministicPlateAnalysisPipeline>();
        services.AddSingleton<OnnxPlateAnalysisPipeline>();
        services.AddSingleton<IPlateAnalysisPipeline, PlateAnalysisPipelineSelector>();

        return services;
    }
}
