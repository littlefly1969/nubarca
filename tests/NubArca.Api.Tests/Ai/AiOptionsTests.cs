using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NubArca.Api.Ai;

namespace NubArca.Api.Tests.Ai;

// Phase 0A: AI is configuration-only and OFF by default. These tests pin the
// defaults and verify the "Ai" section (incl. nested Onnx/External) binds.
public sealed class AiOptionsTests
{
    private static AiOptions Bind(IReadOnlyDictionary<string, string?>? settings)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(settings ?? new Dictionary<string, string?>())
            .Build();

        var services = new ServiceCollection();
        services.Configure<AiOptions>(config.GetSection(AiOptions.SectionName));
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<AiOptions>>().Value;
    }

    [Fact]
    public void Defaults_Are_Disabled_And_Provider_None()
    {
        var options = Bind(settings: null);

        Assert.False(options.Enabled);
        Assert.Equal("none", options.Provider);
        Assert.Equal(1, options.MaxConcurrency);
        Assert.Equal(30, options.TimeoutSeconds);
        Assert.Equal(30, options.ComputeSliceSeconds);
        Assert.Equal(100, options.ComputeSliceItemBudget);
    }

    [Fact]
    public void Per_Capability_Flags_Default_Off()
    {
        var options = Bind(settings: null);

        Assert.False(options.ImageEmbeddingsEnabled);
        Assert.False(options.DocumentExtractionEnabled);
        Assert.False(options.DocumentEmbeddingsEnabled);
        Assert.False(options.FaceDetectionEnabled);
        Assert.False(options.FaceEmbeddingsEnabled);
        Assert.False(options.FaceClusteringEnabled);
        Assert.False(options.TagsEnabled);
    }

    [Fact]
    public void Nested_Provider_Options_Default_Empty()
    {
        var options = Bind(settings: null);

        Assert.NotNull(options.Onnx);
        Assert.NotNull(options.External);
        Assert.Null(options.Onnx.ModelDir);
        Assert.Null(options.External.BaseUrl);
        Assert.Null(options.External.ApiKeyRef);
    }

    [Fact]
    public void Binds_Flat_And_Nested_Keys_From_Configuration()
    {
        var options = Bind(new Dictionary<string, string?>
        {
            ["Ai:Enabled"] = "true",
            ["Ai:Provider"] = "deterministic",
            ["Ai:MaxConcurrency"] = "4",
            ["Ai:ImageEmbeddingsEnabled"] = "true",
            ["Ai:Onnx:ModelDir"] = "/models/onnx",
            ["Ai:External:BaseUrl"] = "https://example.invalid",
            ["Ai:External:ApiKeyRef"] = "AI_EXTERNAL_KEY",
        });

        Assert.True(options.Enabled);
        Assert.Equal("deterministic", options.Provider);
        Assert.Equal(4, options.MaxConcurrency);
        Assert.True(options.ImageEmbeddingsEnabled);
        Assert.Equal("/models/onnx", options.Onnx.ModelDir);
        Assert.Equal("https://example.invalid", options.External.BaseUrl);
        // ApiKeyRef is a reference/name, not a secret value.
        Assert.Equal("AI_EXTERNAL_KEY", options.External.ApiKeyRef);
    }
}
