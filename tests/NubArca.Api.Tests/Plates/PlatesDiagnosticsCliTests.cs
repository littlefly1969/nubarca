using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NubArca.Api.Cli;
using NubArca.Api.Metadata;
using NubArca.Api.Plates;
using Xunit;

namespace NubArca.Api.Tests.Plates;

// `plates models validate` CLI: correct verdicts + strictly sanitized output
// (model BASENAMES only — never absolute paths, stack traces, or forbidden
// storage internals). DB-free (only the bound options are needed).
public sealed class PlatesDiagnosticsCliTests
{
    private static IServiceProvider Provider(PlatesAlprOptions alpr, PlatesFaceRedactionOptions face)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IOptions<PlatesAlprOptions>>(Options.Create(alpr));
        services.AddSingleton<IOptions<PlatesFaceRedactionOptions>>(Options.Create(face));
        return services.BuildServiceProvider();
    }

    private static async Task<string> RunAsync(IServiceProvider sp, params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = await CliEntryPoint.RunAsync(args, stdout, stderr, () => sp);
        Assert.Equal(0, exit);
        return stdout.ToString();
    }

    private static void AssertSanitized(string output)
    {
        Assert.DoesNotContain("/opt/", output);
        Assert.DoesNotContain("Exception", output);
        Assert.DoesNotContain("   at ", output);
        foreach (var needle in MetadataExposurePolicy.ForbiddenInResponses)
        {
            Assert.DoesNotContain(needle, output, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Validate_Onnx_Alpr_Missing_Model_Reports_Basename_Only()
    {
        var alpr = new PlatesAlprOptions
        {
            Provider = "Onnx",
            DetectorModelPath = "/opt/secret/models/detector-XYZ.onnx",
            OcrModelPath = "/opt/secret/models/ocr-XYZ.onnx",
        };
        var output = await RunAsync(Provider(alpr, new PlatesFaceRedactionOptions()),
            "plates", "models", "validate", "alpr");

        Assert.Contains("provider: Onnx", output);
        Assert.Contains("detector-XYZ.onnx", output);           // basename allowed
        Assert.Contains("plate_detector_model_missing", output); // safe verdict
        Assert.DoesNotContain("/opt/secret/models", output);     // never the directory
        AssertSanitized(output);
    }

    [Fact]
    public async Task Validate_FaceRedaction_ExistingDetector_Reports_BoxesOnly()
    {
        var face = new PlatesFaceRedactionOptions
        {
            Enabled = true,
            Provider = "ExistingNubArcaFaceDetector",
        };
        var output = await RunAsync(Provider(new PlatesAlprOptions(), face),
            "plates", "models", "validate", "face-redaction");

        Assert.Contains("provider: ExistingNubArcaFaceDetector", output);
        Assert.Contains("boxes only", output);
        AssertSanitized(output);
    }

    [Fact]
    public async Task Validate_Default_Reports_Disabled()
    {
        var output = await RunAsync(
            Provider(new PlatesAlprOptions(), new PlatesFaceRedactionOptions()),
            "plates", "models", "validate");

        Assert.Contains("ALPR:", output);
        Assert.Contains("FaceRedaction:", output);
        Assert.Contains("status: disabled", output);
        AssertSanitized(output);
    }
}
