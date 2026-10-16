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

    // Tapping one of two discs keeps only that disc. Opt-in as above, plus
    // NUBARCA_SUBJECT_POINT_ENCODER and NUBARCA_SUBJECT_POINT_DECODER (the SAM
    // 2.1 tiny export). NUBARCA_SUBJECT_CUTOUT_PHOTO with
    // NUBARCA_SUBJECT_CUTOUT_TAP="x,y" (normalized) also writes the tapped cutout.
    [SkippableFact]
    public void Tapping_one_of_two_subjects_keeps_only_that_one()
    {
        var modelPath = Environment.GetEnvironmentVariable("NUBARCA_SUBJECT_CUTOUT_MODEL");
        var encoderPath = Environment.GetEnvironmentVariable("NUBARCA_SUBJECT_POINT_ENCODER");
        var decoderPath = Environment.GetEnvironmentVariable("NUBARCA_SUBJECT_POINT_DECODER");
        Skip.If(string.IsNullOrWhiteSpace(modelPath) || string.IsNullOrWhiteSpace(encoderPath)
            || string.IsNullOrWhiteSpace(decoderPath), "Opt-in: the cutout and point model paths are not all set.");

        var ai = new AiOptions { Enabled = true };
        ai.SubjectCutout.Enabled = true;
        ai.SubjectCutout.ModelFile = modelPath!;
        ai.SubjectCutout.PointSelection.Enabled = true;
        ai.SubjectCutout.PointSelection.EncoderFile = encoderPath!;
        ai.SubjectCutout.PointSelection.DecoderFile = decoderPath!;
        using var factory = new OnnxInferenceSessionFactory(
            Options.Create(ai), NullLogger<OnnxInferenceSessionFactory>.Instance);
        var model = new OnnxSubjectMaskModel(Options.Create(ai), factory);
        var points = new OnnxSubjectPointModel(Options.Create(ai), factory);
        Assert.Null(points.UnavailableReason());

        using var photo = new Image<Rgb24>(800, 400, new Rgb24(225, 228, 232));
        photo.Mutate(ctx => ctx
            .Fill(Color.FromRgb(200, 40, 30), new EllipsePolygon(200, 200, 110))
            .Fill(Color.FromRgb(30, 60, 200), new EllipsePolygon(600, 200, 110)));

        using var right = Tap(model, points, photo, 0.75f, 0.5f);

        Assert.NotNull(right);
        Assert.InRange(right!.Width, 200, 260);
        var centre = right[right.Width / 2, right.Height / 2];
        Assert.Equal(255, centre.A);
        Assert.True(centre.B > centre.R, "the tapped disc is the blue one");

        var photoPath = Environment.GetEnvironmentVariable("NUBARCA_SUBJECT_CUTOUT_PHOTO");
        var tap = Environment.GetEnvironmentVariable("NUBARCA_SUBJECT_CUTOUT_TAP");
        if (!string.IsNullOrWhiteSpace(photoPath) && !string.IsNullOrWhiteSpace(tap))
        {
            var xy = tap.Split(',').Select(v => float.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            using var real = Image.Load<Rgb24>(photoPath);
            using var tapped = Tap(model, points, real, xy[0], xy[1]);
            Assert.NotNull(tapped);
            tapped!.SaveAsPng(System.IO.Path.ChangeExtension(photoPath, ".tap.png"));
        }
    }

    private static Image<Rgba32>? Tap(ISubjectMaskModel model, ISubjectPointModel points, Image<Rgb24> photo, float x, float y)
    {
        var input = SubjectCutoutImaging.ToInputTensor(photo, SubjectPointModelShape.InputEdge);
        using var alpha = SubjectCutoutImaging.ToAlphaMask(
            photo, model.PredictLogits(input, SubjectPointModelShape.InputEdge), SubjectPointModelShape.InputEdge);
        var candidates = points.PredictCandidates(input, x, y, out var count);
        var chosen = SubjectCutoutImaging.SelectSubject(alpha, candidates, count, SubjectPointModelShape.CandidateEdge, x, y);
        if (chosen is null) return null;
        SubjectCutoutImaging.RestrictTo(alpha, chosen);
        return SubjectCutoutImaging.Trim(photo, alpha, out _);
    }

    private static Image<Rgba32>? Cut(ISubjectMaskModel model, Image<Rgb24> photo, int size)
        => SubjectCutoutImaging.ToCutout(
            photo, model.PredictLogits(SubjectCutoutImaging.ToInputTensor(photo, size), size), size);
}
