using NubArca.Api.Ai.SubjectCutout;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace NubArca.Api.Tests.Ai.SubjectCutout;

public sealed class SubjectCutoutImagingTests
{
    [Fact]
    public void Input_tensor_is_the_photo_stretched_to_the_square_and_imagenet_normalized()
    {
        using var photo = new Image<Rgb24>(40, 20, new Rgb24(255, 0, 128));

        var tensor = SubjectCutoutImaging.ToInputTensor(photo, 32);

        Assert.Equal(3 * 32 * 32, tensor.Length);
        var plane = 32 * 32;
        Assert.Equal((1f - 0.485f) / 0.229f, tensor[0], 3);
        Assert.Equal((0f - 0.456f) / 0.224f, tensor[plane], 3);
        Assert.Equal((128f / 255f - 0.406f) / 0.225f, tensor[2 * plane + plane - 1], 3);
    }

    [Fact]
    public void Cutout_keeps_the_subject_opaque_clears_the_background_and_trims_to_the_subject()
    {
        using var photo = new Image<Rgb24>(200, 100, new Rgb24(10, 20, 30));
        // Subject: the middle half of the model's square, in both directions.
        var logits = Square(64, (x, y) => x is >= 16 and < 48 && y is >= 16 and < 48 ? 12f : -12f);

        using var cutout = SubjectCutoutImaging.ToCutout(photo, logits, 64);

        Assert.NotNull(cutout);
        // Subject spans x 50..150, y 25..75 of the photo; 1% of the long edge
        // (2 px) of padding on each side, give or take the mask's resampling.
        Assert.InRange(cutout!.Width, 100, 108);
        Assert.InRange(cutout.Height, 50, 58);
        Assert.Equal(0, cutout[0, 0].A);
        var centre = cutout[cutout.Width / 2, cutout.Height / 2];
        Assert.Equal(new Rgba32(10, 20, 30, 255), centre);
    }

    [Fact]
    public void An_empty_mask_is_no_subject()
    {
        using var photo = new Image<Rgb24>(50, 50);

        Assert.Null(SubjectCutoutImaging.ToCutout(photo, Square(32, (_, _) => -20f), 32));
    }

    [Fact]
    public void A_speck_is_no_subject()
    {
        using var photo = new Image<Rgb24>(64, 64);
        // One pixel of 4096: well under the minimum subject share.
        var logits = Square(64, (x, y) => x == 3 && y == 3 ? 20f : -20f);

        Assert.Null(SubjectCutoutImaging.ToCutout(photo, logits, 64));
    }

    [Fact]
    public void A_mask_of_the_wrong_size_is_refused()
    {
        using var photo = new Image<Rgb24>(10, 10);

        Assert.Throws<ArgumentException>(() => SubjectCutoutImaging.ToCutout(photo, new float[10], 32));
    }

    [Theory]
    [InlineData(-30f, 0)]
    [InlineData(0f, 128)]
    [InlineData(30f, 255)]
    public void Logits_become_alpha_through_the_sigmoid(float logit, byte alpha)
    {
        Assert.Equal(alpha, SubjectCutoutImaging.ToAlpha(logit));
    }

    [Fact]
    public void A_tap_keeps_the_largest_candidate_that_lies_on_a_subject()
    {
        // Foreground: two people, x 0..39 and x 60..99 of a 100x100 photo.
        using var photo = new Image<Rgb24>(100, 100);
        using var alpha = SubjectCutoutImaging.ToAlphaMask(
            photo, Square(100, (x, _) => x < 40 || x >= 60 ? 12f : -12f), 100);
        // Around a tap on the left person: the shirt, the whole person, and the
        // whole photo (mostly background).
        var candidates = Candidates(20,
            (x, y) => x < 8 && y is >= 5 and < 15,
            (x, _) => x < 8,
            (_, _) => true);

        var chosen = SubjectCutoutImaging.SelectSubject(alpha, candidates, 3, 20, 0.1f, 0.5f);

        Assert.NotNull(chosen);
        Assert.True(chosen![50 * 100 + 10]);   // the left person
        Assert.True(chosen[90 * 100 + 30]);    // all of it, not just the shirt
        Assert.False(chosen[50 * 100 + 80]);   // never the right person
    }

    [Fact]
    public void A_candidate_spanning_two_separate_subjects_is_kept_to_the_touched_one()
    {
        // Two discs that do not touch, and a candidate that covers both.
        using var photo = new Image<Rgb24>(100, 100);
        using var alpha = SubjectCutoutImaging.ToAlphaMask(
            photo, Square(100, (x, _) => x < 40 || x >= 60 ? 12f : -12f), 100);
        var candidates = Candidates(20, (x, _) => x < 8 || x >= 12);

        var chosen = SubjectCutoutImaging.SelectSubject(alpha, candidates, 1, 20, 0.8f, 0.5f);

        Assert.NotNull(chosen);
        Assert.True(chosen![50 * 100 + 80]);
        Assert.False(chosen[50 * 100 + 10]);
    }

    [Fact]
    public void A_tap_on_the_background_selects_nothing()
    {
        using var photo = new Image<Rgb24>(100, 100);
        using var alpha = SubjectCutoutImaging.ToAlphaMask(
            photo, Square(100, (x, _) => x < 40 ? 12f : -12f), 100);
        var candidates = Candidates(20, (x, _) => x >= 10, (x, _) => x >= 12);

        Assert.Null(SubjectCutoutImaging.SelectSubject(alpha, candidates, 2, 20, 0.7f, 0.5f));
    }

    [Fact]
    public void Restricting_keeps_the_chosen_subject_and_its_edge_and_clears_the_other()
    {
        using var photo = new Image<Rgb24>(100, 100);
        using var alpha = SubjectCutoutImaging.ToAlphaMask(
            photo, Square(100, (x, _) => x < 40 || x >= 60 ? 12f : -12f), 100);
        // The point model's selection stops a pixel short of the person's edge.
        var selected = new bool[100 * 100];
        for (var y = 0; y < 100; y++)
        for (var x = 0; x < 38; x++)
            selected[y * 100 + x] = true;

        SubjectCutoutImaging.RestrictTo(alpha, selected);

        Assert.Equal(255, alpha[10, 50].PackedValue);
        // The cutout model's own edge survives the coarser selection.
        Assert.True(alpha[39, 50].PackedValue > 150);
        Assert.Equal(0, alpha[80, 50].PackedValue);
        using var cutout = SubjectCutoutImaging.Trim(photo, alpha, out var region);
        Assert.InRange(cutout!.Width, 40, 44);
        Assert.Equal(new Rectangle(0, 0, cutout.Width, 100), region);
    }

    [Fact]
    public void Candidates_of_the_wrong_shape_are_refused()
    {
        using var alpha = new Image<L8>(10, 10);

        Assert.Throws<ArgumentException>(() => SubjectCutoutImaging.SelectSubject(alpha, new float[10], 1, 20, 0.5f, 0.5f));
        Assert.Throws<ArgumentException>(() => SubjectCutoutImaging.RestrictTo(alpha, new bool[5]));
    }

    internal static float[] Candidates(int edge, params Func<int, int, bool>[] masks)
        => masks.SelectMany(m => Square(edge, (x, y) => m(x, y) ? 12f : -12f)).ToArray();

    internal static float[] Square(int size, Func<int, int, float> value)
    {
        var data = new float[size * size];
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
            data[y * size + x] = value(x, y);
        return data;
    }
}
