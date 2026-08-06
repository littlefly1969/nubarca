using NubArca.Api.Plates;
using Xunit;

namespace NubArca.Api.Tests.Plates;

// Provider parsing + legacy Enabled fallback. Guarantees Slice 2/3 config
// (Enabled=true, no Provider) keeps meaning DeterministicDev, and that production
// defaults (nothing set) resolve to Disabled.
public sealed class PlateProviderParsingTests
{
    [Theory]
    [InlineData("Onnx", false, PlateAlprProvider.Onnx)]
    [InlineData("onnx", false, PlateAlprProvider.Onnx)]
    [InlineData("DeterministicDev", false, PlateAlprProvider.DeterministicDev)]
    [InlineData("Disabled", true, PlateAlprProvider.Disabled)]
    [InlineData("", true, PlateAlprProvider.DeterministicDev)]   // legacy fallback
    [InlineData("", false, PlateAlprProvider.Disabled)]          // production default
    [InlineData("nonsense", true, PlateAlprProvider.DeterministicDev)] // invalid → fallback
    public void ResolveAlpr(string provider, bool enabled, PlateAlprProvider expected)
        => Assert.Equal(expected, PlateProviderParsing.ResolveAlpr(provider, enabled));

    [Theory]
    [InlineData("ExistingNubArcaFaceDetector", false, PlateFaceRedactionProvider.ExistingNubArcaFaceDetector)]
    [InlineData("DeterministicDev", false, PlateFaceRedactionProvider.DeterministicDev)]
    [InlineData("OnnxDedicatedFaceDetector", false, PlateFaceRedactionProvider.OnnxDedicatedFaceDetector)]
    [InlineData("", true, PlateFaceRedactionProvider.DeterministicDev)]  // legacy fallback
    [InlineData("", false, PlateFaceRedactionProvider.Disabled)]         // production default
    public void ResolveFaceRedaction(string provider, bool enabled, PlateFaceRedactionProvider expected)
        => Assert.Equal(expected, PlateProviderParsing.ResolveFaceRedaction(provider, enabled));

    [Fact]
    public void Options_ResolveProvider_Uses_Fallback()
    {
        Assert.Equal(PlateAlprProvider.DeterministicDev,
            new PlatesAlprOptions { Enabled = true }.ResolveProvider());
        Assert.Equal(PlateAlprProvider.Disabled,
            new PlatesAlprOptions().ResolveProvider());
        Assert.Equal(PlateFaceRedactionProvider.Disabled,
            new PlatesFaceRedactionOptions().ResolveProvider());
        Assert.Equal(PlateFaceRedactionProvider.ExistingNubArcaFaceDetector,
            new PlatesFaceRedactionOptions
            {
                Enabled = true,
                Provider = "ExistingNubArcaFaceDetector",
            }.ResolveProvider());
    }
}
