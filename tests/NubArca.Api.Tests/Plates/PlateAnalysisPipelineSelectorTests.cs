using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NubArca.Api.Plates;
using NubArca.Api.Plates.Alpr;
using Xunit;

namespace NubArca.Api.Tests.Plates;

// Provider routing + sanitized unavailable reasons for the ALPR pipeline
// selector. No ONNX sessions are created (availability only checks File.Exists).
public sealed class PlateAnalysisPipelineSelectorTests : IDisposable
{
    private readonly OnnxRuntimeSessionCache _cache = new();
    private readonly List<string> _tempFiles = new();

    public void Dispose()
    {
        _cache.Dispose();
        foreach (var f in _tempFiles)
        {
            try { File.Delete(f); } catch { /* best effort */ }
        }
    }

    private PlateAnalysisPipelineSelector Build(PlatesAlprOptions options)
    {
        var opts = Options.Create(options);
        var deterministic = new DeterministicPlateAnalysisPipeline(
            new DeterministicPlateDetector(), new DeterministicPlateOcrReader(), opts);
        var onnx = new OnnxPlateAnalysisPipeline(
            _cache, opts, NullLogger<OnnxPlateAnalysisPipeline>.Instance);
        return new PlateAnalysisPipelineSelector(deterministic, onnx, opts);
    }

    private string TempModel()
    {
        var path = Path.Combine(Path.GetTempPath(), $"plate-model-{Guid.NewGuid():N}.onnx");
        File.WriteAllText(path, "not a real model");
        _tempFiles.Add(path);
        return path;
    }

    [Fact]
    public void Disabled_Is_Unavailable_With_ModelNotConfigured()
    {
        var selector = Build(new PlatesAlprOptions { Provider = "Disabled" });
        Assert.False(selector.IsAvailable);
        Assert.Equal(PlateAnalysisErrorCodes.ModelNotConfigured, selector.UnavailableReason);
    }

    [Fact]
    public void DeterministicDev_Is_Available()
    {
        var selector = Build(new PlatesAlprOptions { Provider = "DeterministicDev" });
        Assert.True(selector.IsAvailable);
        Assert.Null(selector.UnavailableReason);
    }

    [Fact]
    public void Legacy_Enabled_Without_Provider_Is_Deterministic()
    {
        var selector = Build(new PlatesAlprOptions { Enabled = true });
        Assert.True(selector.IsAvailable);
    }

    [Fact]
    public void Onnx_Without_Detector_Model_Reports_DetectorMissing()
    {
        var selector = Build(new PlatesAlprOptions { Provider = "Onnx" });
        Assert.False(selector.IsAvailable);
        Assert.Equal(PlateAnalysisErrorCodes.DetectorModelMissing, selector.UnavailableReason);
    }

    [Fact]
    public void Onnx_With_Detector_But_No_Ocr_Reports_OcrMissing()
    {
        var selector = Build(new PlatesAlprOptions
        {
            Provider = "Onnx",
            DetectorModelPath = TempModel(),
            // OcrModelPath left empty
        });
        Assert.False(selector.IsAvailable);
        Assert.Equal(PlateAnalysisErrorCodes.OcrModelMissing, selector.UnavailableReason);
    }
}
