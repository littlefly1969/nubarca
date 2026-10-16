using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NubArca.Api.Ai;
using NubArca.Api.Ai.Onnx;
using NubArca.Api.Ai.SubjectCutout;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace NubArca.Api.Tests.Ai.SubjectCutout;

// The real BiRefNet export through the real session factory. Opt-in: point
// NUBARCA_SUBJECT_CUTOUT_MODEL at the model file (see
// docs/model-deployment/subject-cutout.md). NUBARCA_SUBJECT_CUTOUT_PHOTO, when
// set, also cuts out that photo and writes the PNG next to it for a look.
[Trait("Category", "External")]
public sealed class SubjectCutoutModelTests
{
    [SkippableFact]
    public void The_stock_export_lifts_a_disc_off_a_plain_background()
    {
        var modelPath = Environment.GetEnvironmentVariable("NUBARCA_SUBJECT_CUTOUT_MODEL");
        Skip.If(string.IsNullOrWhiteSpace(modelPath), "Opt-in: NUBARCA_SUBJECT_CUTOUT_MODEL is not set.");

        var ai = new AiOptions { Enabled = true };
        ai.SubjectCutout.Enabled = true;
        ai.SubjectCutout.ModelFile = modelPath!;
        using var factory = new OnnxInferenceSessionFactory(
            Options.Create(ai), NullLogger<OnnxInferenceSessionFactory>.Instance);
        var model = new OnnxSubjectMaskModel(Options.Create(ai), factory);
        Assert.Null(model.UnavailableReason());

        using var photo = new Image<Rgb24>(640, 480, new Rgb24(225, 228, 232));
        photo.Mutate(ctx => ctx.Fill(Color.FromRgb(200, 40, 30), new EllipsePolygon(320, 240, 120)));

        using var cutout = Cut(model, photo, ai.SubjectCutout.InputSize);

        Assert.NotNull(cutout);
        Assert.InRange(cutout!.Width, 200, 300);
        Assert.InRange(cutout.Height, 200, 300);
        Assert.Equal(255, cutout[cutout.Width / 2, cutout.Height / 2].A);
        Assert.Equal(0, cutout[0, 0].A);

        var photoPath = Environment.GetEnvironmentVariable("NUBARCA_SUBJECT_CUTOUT_PHOTO");
        if (!string.IsNullOrWhiteSpace(photoPath))
        {
            using var real = Image.Load<Rgb24>(photoPath);
            using var realCutout = Cut(model, real, ai.SubjectCutout.InputSize);
            Assert.NotNull(realCutout);
            realCutout!.SaveAsPng(System.IO.Path.ChangeExtension(photoPath, ".cutout.png"));
        }
    }

    private static Image<Rgba32>? Cut(ISubjectMaskModel model, Image<Rgb24> photo, int size)
        => SubjectCutoutImaging.ToCutout(
            photo, model.PredictLogits(SubjectCutoutImaging.ToInputTensor(photo, size), size), size);
}
