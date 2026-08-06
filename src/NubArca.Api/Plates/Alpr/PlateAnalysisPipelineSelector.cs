using Microsoft.Extensions.Options;

namespace NubArca.Api.Plates.Alpr;

// Routes ALPR analysis to the configured provider (Plates:Alpr:Provider):
//   Disabled         -> unavailable → worker records model_not_configured
//   DeterministicDev -> the deterministic dev/test pipeline (Slice 2)
//   Onnx             -> the in-process ONNX detector + OCR pipeline (Slice 4)
//
// This is the SINGLE IPlateAnalysisPipeline the worker/service depends on. The
// concrete pipelines are registered as themselves and composed here.
public sealed class PlateAnalysisPipelineSelector : IPlateAnalysisPipeline
{
    private readonly DeterministicPlateAnalysisPipeline _deterministic;
    private readonly OnnxPlateAnalysisPipeline _onnx;
    private readonly IOptions<PlatesAlprOptions> _options;

    public PlateAnalysisPipelineSelector(
        DeterministicPlateAnalysisPipeline deterministic,
        OnnxPlateAnalysisPipeline onnx,
        IOptions<PlatesAlprOptions> options)
    {
        _deterministic = deterministic;
        _onnx = onnx;
        _options = options;
    }

    public bool IsAvailable => _options.Value.ResolveProvider() switch
    {
        PlateAlprProvider.DeterministicDev => _deterministic.IsAvailable,
        PlateAlprProvider.Onnx => _onnx.IsAvailable,
        _ => false,
    };

    public string? UnavailableReason => _options.Value.ResolveProvider() switch
    {
        PlateAlprProvider.DeterministicDev => null, // always available when selected
        PlateAlprProvider.Onnx => _onnx.UnavailableReason,
        _ => PlateAnalysisErrorCodes.ModelNotConfigured,
    };

    public Task<PlateAnalysisResult> AnalyzeAsync(
        PlateImageInput image, CancellationToken cancellationToken)
        => _options.Value.ResolveProvider() switch
        {
            PlateAlprProvider.DeterministicDev => _deterministic.AnalyzeAsync(image, cancellationToken),
            PlateAlprProvider.Onnx => _onnx.AnalyzeAsync(image, cancellationToken),
            _ => throw new PlateAnalysisModelException(PlateAnalysisErrorCodes.ModelNotConfigured),
        };
}
