using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NubArca.Api.Ai;
using NubArca.Api.Ai.DocumentVisual;
using NubArca.Api.Ai.Documents;
using NubArca.Api.Ai.Onnx;
using NubArca.Api.Domain.Ai;
using Xunit;
using Xunit.Abstractions;

namespace NubArca.Api.Tests.Integration;

// THE VISUAL PATH AGAINST THE REAL MODEL.
//
// Everything else about visual retrieval is measured with vectors the fixture
// seeded, which is the right default for a fast suite — it is reproducible, it
// costs nothing, and it is the correct way to test an eligibility join. It
// cannot answer the one question a visual feature exists for: does SigLIP2,
// given a RENDERED PAGE and a typed question, actually put them near each
// other.
//
// So this runs the real towers on real pixels: render a document with the
// production renderer, embed the image with the production image tower, embed a
// question with the production text tower, and compare. Both towers are the
// SAME checkpoint the photo library uses, under this slice's own profile
// identity.
//
// GATED ON `Ai__Onnx__ModelDir`, with NO fallback path. A default would be an
// installation-specific literal in tracked source, which the identity contract
// refuses and which would make this silently skip or silently fail depending on
// whose machine it ran on. Unset means SKIPPED, and a completion report has to
// say so rather than claim a lane it did not run.
[Trait("Category", "External")]
public sealed class DocumentVisualRealOnnxTests
{
    private readonly ITestOutputHelper _output;

    public DocumentVisualRealOnnxTests(ITestOutputHelper output) => _output = output;

    /// The model directory, or null when the SigLIP2 assets are not installed.
    /// Both towers and the tokenizer are required: an image encoder without its
    /// paired text encoder cannot answer a question, which is exactly what the
    /// profile resolver refuses in production.
    private static string? ModelDir
    {
        get
        {
            var dir = Environment.GetEnvironmentVariable("Ai__Onnx__ModelDir");
            if (string.IsNullOrWhiteSpace(dir)) return null;

            var model = Path.Combine(dir, OnnxImageModels.SiglipSo400mKey);
            return File.Exists(Path.Combine(model, OnnxImageModels.DefaultModelFile))
                   && File.Exists(Path.Combine(model, OnnxImageModels.DefaultTextModelFile))
                   && File.Exists(Path.Combine(model, OnnxImageModels.DefaultTokenizerFile))
                ? dir
                : null;
        }
    }

    private static AiOptions Ai(string modelDir) => new()
    {
        Enabled = true,
        Provider = AiProviders.Onnx,
        MaxConcurrency = 1,
        TimeoutSeconds = 120,
        Onnx = new AiOnnxOptions { ModelDir = modelDir },
    };

    private static AiProfile Profile() => new()
    {
        Id = Guid.NewGuid(),
        Key = DocumentVisualProfiles.DenseSiglip2So400m,
        Capability = AiCapabilities.DocumentVisualEmbedding,
        Modality = AiModalities.Multimodal,
        Dimension = DocumentVisualProfiles.DenseDimension,
        DistanceMetric = AiDistanceMetrics.Cosine,
        Enabled = true,
        // The link to the code-side preprocessing config — the SAME catalog
        // entry the photo profile points at, which is what makes "same weights,
        // same preprocessing" a fact rather than two copied constants.
        ConfigHash = OnnxImageModels.SiglipSo400mKey,
        CreatedAt = DateTime.UtcNow,
    };

    [SkippableFact]
    public void Both_Towers_Serve_The_Document_Visual_Profile()
    {
        var dir = ModelDir;
        Skip.If(dir is null, "Ai__Onnx__ModelDir is not set to an installed SigLIP2 So400m 384.");

        var options = Options.Create(Ai(dir!));
        var profile = Profile();

        using var images = new OnnxImageEmbedder(
            options, new OnnxImagePreprocessor(), NullLogger<OnnxImageEmbedder>.Instance,
            new OnnxInferenceSessionFactory(
                options, NullLogger<OnnxInferenceSessionFactory>.Instance));
        using var text = new OnnxTextEmbedder(
            options,
            new OnnxInferenceSessionFactory(
                options, NullLogger<OnnxInferenceSessionFactory>.Instance));

        // The capability the DOCUMENT profile declares — not `image-embedding`.
        Assert.True(images.Supports(AiCapabilities.DocumentVisualEmbedding));
        Assert.True(text.Supports(AiCapabilities.DocumentVisualEmbedding));
        Assert.True(images.CheckReadiness(profile).IsReady, images.CheckReadiness(profile).Reason);
        Assert.True(text.CheckReadiness(profile).IsReady, text.CheckReadiness(profile).Reason);
    }

    [SkippableFact]
    public async Task A_Rendered_Page_And_A_Question_Land_In_One_Space()
    {
        var dir = ModelDir;
        Skip.If(dir is null, "Ai__Onnx__ModelDir is not set to an installed SigLIP2 So400m 384.");

        var options = Options.Create(Ai(dir!));
        var visual = new DocumentVisualOptions { Enabled = true };
        var profile = Profile();

        var canvas = new TextCanvasVisualRenderer(Options.Create(visual));
        Skip.IfNot(canvas.CheckReadiness().Ready, "the bundled canvas font is not installed");

        using var images = new OnnxImageEmbedder(
            options, new OnnxImagePreprocessor(), NullLogger<OnnxImageEmbedder>.Instance,
            new OnnxInferenceSessionFactory(
                options, NullLogger<OnnxInferenceSessionFactory>.Instance));
        using var text = new OnnxTextEmbedder(
            options,
            new OnnxInferenceSessionFactory(
                options, NullLogger<OnnxInferenceSessionFactory>.Instance));

        // Two documents that LOOK different: a table of quarterly figures, and
        // a page of ordinary prose about something else.
        var table = await RenderAsync(canvas, visual, """
            # Quarterly budget

            | Quarter | Revenue | Costs  | Result |
            | ------- | ------- | ------ | ------ |
            | Q1      |  41,000 | 30,200 | 10,800 |
            | Q2      |  52,400 | 33,900 | 18,500 |
            | Q3      |  47,900 | 35,100 | 12,800 |
            | Q4      |  61,200 | 38,400 | 22,800 |
            """);

        var prose = await RenderAsync(canvas, visual, """
            # Travel notes

            The train to Lisbon leaves at seven in the morning and the hotel is a
            short walk from the central station. Breakfast is included, and the
            museum near the river opens at ten.
            """);

        var stopwatch = Stopwatch.StartNew();
        var tableVector = (await images.EmbedImageAsync(table, profile)).Vector;
        var imageMs = stopwatch.ElapsedMilliseconds;
        var proseVector = (await images.EmbedImageAsync(prose, profile)).Vector;

        stopwatch.Restart();
        var question = (await text.EmbedTextAsync(
            "a table of quarterly revenue and costs", profile)).Vector;
        var queryMs = stopwatch.ElapsedMilliseconds;

        // The contract, asserted rather than assumed.
        Assert.Equal(DocumentVisualProfiles.DenseDimension, tableVector.Length);
        Assert.Equal(DocumentVisualProfiles.DenseDimension, question.Length);
        Assert.All(tableVector, v => Assert.True(float.IsFinite(v)));
        Assert.All(question, v => Assert.True(float.IsFinite(v)));
        Assert.Equal(1.0, Norm(tableVector), precision: 3);
        Assert.Equal(1.0, Norm(question), precision: 3);

        var toTable = Cosine(question, tableVector);
        var toProse = Cosine(question, proseVector);

        _output.WriteLine($"image_embed_ms={imageMs} query_embed_ms={queryMs}");
        _output.WriteLine($"cosine_to_table={toTable:F4} cosine_to_prose={toProse:F4}");

        // THE ACTUAL CLAIM OF THIS SLICE, measured: a question about a table is
        // closer to the PICTURE of a table than to the picture of a page of
        // prose. Not a threshold — cosine is not calibrated across checkpoints —
        // but an ordering, which is what retrieval consumes.
        Assert.True(
            toTable > toProse,
            $"the table page must be closer than the prose page ({toTable:F4} vs {toProse:F4})");
    }

    [SkippableFact]
    public async Task Embedding_The_Same_Page_Twice_Gives_The_Same_Vector()
    {
        var dir = ModelDir;
        Skip.If(dir is null, "Ai__Onnx__ModelDir is not set to an installed SigLIP2 So400m 384.");

        var options = Options.Create(Ai(dir!));
        var visual = new DocumentVisualOptions { Enabled = true };
        var profile = Profile();

        var canvas = new TextCanvasVisualRenderer(Options.Create(visual));
        Skip.IfNot(canvas.CheckReadiness().Ready, "the bundled canvas font is not installed");

        using var images = new OnnxImageEmbedder(
            options, new OnnxImagePreprocessor(), NullLogger<OnnxImageEmbedder>.Instance,
            new OnnxInferenceSessionFactory(
                options, NullLogger<OnnxInferenceSessionFactory>.Instance));

        var page = await RenderAsync(canvas, visual, "# Heading\n\nSome ordinary body text.");

        var first = (await images.EmbedImageAsync(page, profile)).Vector;
        var second = (await images.EmbedImageAsync(page, profile)).Vector;

        // Determinism is what makes idempotence meaningful: the indexer skips a
        // document whose bytes and profiles are unchanged, and that is only
        // correct if re-running would have produced the same vector.
        Assert.Equal(first, second);
    }

    private static async Task<byte[]> RenderAsync(
        TextCanvasVisualRenderer canvas, DocumentVisualOptions options, string markdown)
    {
        var outcome = await canvas.RenderAsync(new DocumentVisualRenderRequest(
            System.Text.Encoding.UTF8.GetBytes(markdown),
            DocumentFormatKind.NativeText,
            options));

        Assert.True(outcome.Ok, outcome.Reason);
        return outcome.Artifact!.Units[0].Png;
    }

    private static double Norm(float[] vector)
    {
        double sum = 0;
        foreach (var value in vector) sum += (double)value * value;
        return Math.Sqrt(sum);
    }

    private static double Cosine(float[] a, float[] b)
    {
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += (double)a[i] * b[i];
            na += (double)a[i] * a[i];
            nb += (double)b[i] * b[i];
        }
        return dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }
}
