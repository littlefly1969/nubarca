using System.Buffers.Binary;
using NubArca.Api.Domain.Print;
using NubArca.Api.Print;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Metadata.Profiles.Cicp;
using SixLabors.ImageSharp.Metadata.Profiles.Icc;
using SixLabors.ImageSharp.PixelFormats;

namespace NubArca.Api.Tests.Print;

/// <summary>
/// A photograph in a wide colour space prints in its own colours: its pixels
/// are brought into sRGB from the profile it carries before they go on a sheet.
///
/// The expected values are computed independently of the code under test, from
/// the published D65 RGB-to-RGB matrices (Display P3 → sRGB and Adobe RGB →
/// sRGB) rather than from the D50 colorants the profiles carry.
/// </summary>
public sealed class PrintColourSpaceTests
{
    // Colorants as Apple's "Display P3" and Adobe's "Adobe RGB (1998)" profiles store them (D50).
    internal static readonly double[][] DisplayP3 = [[0.5151, 0.2412, -0.0011], [0.2920, 0.6922, 0.0419], [0.1571, 0.0666, 0.7841]];
    private static readonly double[][] AdobeRgb = [[0.6097, 0.3111, 0.0195], [0.2053, 0.6257, 0.0609], [0.1492, 0.0632, 0.7446]];
    private static readonly double[][] Srgb = [[0.4361, 0.2225, 0.0139], [0.3851, 0.7169, 0.0971], [0.1431, 0.0606, 0.7141]];

    private static readonly Rgb24 Sample = new(180, 90, 90);

    [Fact]
    public void Display_P3_Is_Converted_To_Srgb()
    {
        using var image = Uniform(Sample);
        image.Metadata.IccProfile = new IccProfile(Profile(DisplayP3, SrgbParametricCurve()));

        Assert.True(PrintColourSpace.ToSrgb(image, image.Metadata));
        AssertNear(new Rgb24(193, 83, 87), image[1, 1], 1);
    }

    [Fact]
    public void Adobe_Rgb_Is_Converted_To_Srgb()
    {
        using var image = Uniform(Sample);
        image.Metadata.IccProfile = new IccProfile(Profile(AdobeRgb, GammaCurve(563 / 256.0)));

        Assert.True(PrintColourSpace.ToSrgb(image, image.Metadata));
        AssertNear(new Rgb24(205, 90, 90), image[1, 1], 1);
    }

    [Fact]
    public void A_Grey_Stays_Grey()
    {
        using var image = Uniform(new Rgb24(128, 128, 128));
        image.Metadata.IccProfile = new IccProfile(Profile(DisplayP3, SrgbParametricCurve()));

        PrintColourSpace.ToSrgb(image, image.Metadata);
        AssertNear(new Rgb24(128, 128, 128), image[1, 1], 1);
    }

    [Fact]
    public void An_Srgb_Profile_Changes_Nothing()
    {
        using var image = Uniform(Sample);
        image.Metadata.IccProfile = new IccProfile(Profile(Srgb, SampledSrgbCurve(1024)));

        Assert.False(PrintColourSpace.ToSrgb(image, image.Metadata));
        Assert.Equal(Rgba(Sample), image[1, 1]);
    }

    [Fact]
    public void No_Profile_Changes_Nothing()
    {
        using var image = Uniform(Sample);
        Assert.False(PrintColourSpace.ToSrgb(image, image.Metadata));
        Assert.Equal(Rgba(Sample), image[1, 1]);
    }

    [Fact]
    public void A_Profile_It_Cannot_Follow_Changes_Nothing()
    {
        // A grey profile on an RGB image, and a profile cut short: neither says
        // anything about these pixels that can be trusted.
        using var grey = Uniform(Sample);
        var greyProfile = Profile(DisplayP3, SrgbParametricCurve());
        BinaryPrimitives.WriteUInt32BigEndian(greyProfile.AsSpan(16), 0x47524159); // 'GRAY'
        grey.Metadata.IccProfile = new IccProfile(greyProfile);
        Assert.False(PrintColourSpace.ToSrgb(grey, grey.Metadata));

        using var truncated = Uniform(Sample);
        truncated.Metadata.IccProfile = new IccProfile(Profile(DisplayP3, SrgbParametricCurve())[..150]);
        Assert.False(PrintColourSpace.ToSrgb(truncated, truncated.Metadata));
        Assert.Equal(Rgba(Sample), truncated[1, 1]);
    }

    /// <summary>
    /// An 8×8 PNG of <see cref="Sample"/> exactly as FFmpeg writes a decoded
    /// frame tagged Display P3 (FFmpeg 6.1, `-c:v png`, RGB24): cICP 0c 0d 00 01 —
    /// primaries 12 (Display P3), transfer 13 (sRGB), matrix 0 (RGB), full range —
    /// plus the cHRM and gAMA it adds beside it, and no ICC profile.
    /// </summary>
    private const string FfmpegDisplayP3Png =
        "iVBORw0KGgoAAAANSUhEUgAAAAgAAAAICAIAAABLbSncAAAACXBIWXMAAAABAAAAAQBPJcTWAAAABGNJQ1AMDQABbgPj7wAAACBjSFJNAAB6JgAAgIQAAQmgAAB9AAAAZ4QAAQ2IAAA6mAAAF3Dx+MpXAAAABGdBTUEAALGPC/xhBQAAABFJREFUeJxj2BIVhRUxDC0JAEFVWgE0V45CAAAAAElFTkSuQmCC";

    [Fact]
    public void An_Ffmpeg_Display_P3_Frame_Is_Converted()
    {
        using var image = Image.Load<Rgba32>(Convert.FromBase64String(FfmpegDisplayP3Png));
        Assert.Equal(Rgba(Sample), image[1, 1]);
        Assert.NotNull(image.Metadata.CicpProfile);
        Assert.Equal(CicpColorPrimaries.SmpteEg432_1, image.Metadata.CicpProfile!.ColorPrimaries);
        Assert.Equal(CicpTransferCharacteristics.Iec61966_2_1, image.Metadata.CicpProfile.TransferCharacteristics);
        Assert.Equal(CicpMatrixCoefficients.Identity, image.Metadata.CicpProfile.MatrixCoefficients);
        Assert.True(image.Metadata.CicpProfile.FullRange);
        Assert.Null(image.Metadata.IccProfile);

        Assert.True(PrintColourSpace.ToSrgb(image, image.Metadata));
        AssertNear(new Rgb24(193, 83, 87), image[1, 1], 1);
    }

    [Fact]
    public void A_Cicp_Srgb_Or_Hdr_Frame_Changes_Nothing()
    {
        using var srgb = Uniform(Sample);
        srgb.Metadata.CicpProfile = new CicpProfile(1, 13, 0, true);
        Assert.False(PrintColourSpace.ToSrgb(srgb, srgb.Metadata));

        using var pq = Uniform(Sample);
        pq.Metadata.CicpProfile = new CicpProfile(9, 16, 0, true);
        Assert.False(PrintColourSpace.ToSrgb(pq, pq.Metadata));
        Assert.Equal(Rgba(Sample), pq[1, 1]);
    }

    [Fact]
    public void A_Cicp_Tag_It_Cannot_Follow_Changes_Nothing()
    {
        // Limited (video) range would need its levels remapped first, and an
        // unspecified transfer is not sRGB: both are left exactly as they are.
        foreach (var cicp in new[] { new CicpProfile(12, 13, 0, false), new CicpProfile(12, 2, 0, true) })
        {
            using var image = Uniform(Sample);
            image.Metadata.CicpProfile = cicp;
            Assert.False(PrintColourSpace.ToSrgb(image, image.Metadata));
            Assert.Equal(Rgba(Sample), image[1, 1]);
        }
    }

    [Fact]
    public void A_Cicp_Tag_Outranks_The_Icc_Profile()
    {
        // Both present: the cICP decides, whatever the ICC says.
        using var p3 = Uniform(Sample);
        p3.Metadata.CicpProfile = new CicpProfile(12, 13, 0, true);
        p3.Metadata.IccProfile = new IccProfile(Profile(AdobeRgb, GammaCurve(563 / 256.0)));
        Assert.True(PrintColourSpace.ToSrgb(p3, p3.Metadata));
        AssertNear(new Rgb24(193, 83, 87), p3[1, 1], 1);

        // A cICP it declines (HDR) is not overruled by the ICC beside it.
        using var pq = Uniform(Sample);
        pq.Metadata.CicpProfile = new CicpProfile(9, 16, 0, true);
        pq.Metadata.IccProfile = new IccProfile(Profile(DisplayP3, SrgbParametricCurve()));
        Assert.False(PrintColourSpace.ToSrgb(pq, pq.Metadata));
        Assert.Equal(Rgba(Sample), pq[1, 1]);
    }

    [Fact]
    public void An_Oversized_Curve_Count_Is_Refused_Before_Anything_Is_Allocated()
    {
        // 0x80000000 entries: 12 + count * 2 wraps to 12 in 32 bits.
        var curve = new byte[14];
        WriteSignature(curve, 0, "curv");
        BinaryPrimitives.WriteUInt32BigEndian(curve.AsSpan(8), 0x80000000);
        using var image = Uniform(Sample);
        image.Metadata.IccProfile = new IccProfile(Profile(DisplayP3, curve));

        Assert.False(PrintColourSpace.ToSrgb(image, image.Metadata));
        Assert.Equal(Rgba(Sample), image[1, 1]);
    }

    [Fact]
    public async Task An_Owner_Print_Of_A_Display_P3_Jpeg_Is_Printed_In_Srgb()
    {
        var p3 = await Jpeg(Sample, Profile(DisplayP3, SrgbParametricCurve()));
        var plain = await Jpeg(Sample, null);
        var renderer = new PrintArtifactRenderer();

        using var printed = Image.Load<Rgb24>(await renderer.RenderPhoto10x15Async(p3, default));
        using var untouched = Image.Load<Rgb24>(await renderer.RenderPhoto10x15Async(plain, default));

        var centre = (printed.Width / 2, printed.Height / 2);
        AssertNear(new Rgb24(193, 83, 87), printed[centre.Item1, centre.Item2], 3);
        AssertNear(Sample, untouched[centre.Item1, centre.Item2], 3);
    }

    [Fact]
    public async Task A_Party_Sheet_Of_An_Adobe_Rgb_Jpeg_Is_Printed_In_Srgb()
    {
        var adobe = await Jpeg(Sample, Profile(AdobeRgb, GammaCurve(563 / 256.0)), 1000, 1400);
        var composition = new PartyPrintComposition(PartyPrintProducts.Photo, PartyPrintTheme.Pure,
            [new PartyPrintPhoto(adobe, 0, 0, 1, 1)], "Festa", null);

        using var sheet = Image.Load<Rgb24>(await new PartyPrintComposer().RenderAsync(composition, default));

        AssertNear(new Rgb24(205, 90, 90), sheet[sheet.Width / 2, (int)(sheet.Height * 0.35)], 3);
    }

    // --- Fixtures --------------------------------------------------------------

    private static Rgba32 Rgba(Rgb24 c) => new(c.R, c.G, c.B, 255);

    private static Image<Rgba32> Uniform(Rgb24 colour) => new(4, 4, Rgba(colour));

    private static async Task<byte[]> Jpeg(Rgb24 colour, byte[]? icc, int width = 600, int height = 400)
    {
        using var image = new Image<Rgba32>(width, height, Rgba(colour));
        if (icc is not null) image.Metadata.IccProfile = new IccProfile(icc);
        using var output = new MemoryStream();
        await image.SaveAsJpegAsync(output, new JpegEncoder { Quality = 100, ColorType = JpegEncodingColor.YCbCrRatio444 });
        return output.ToArray();
    }

    private static void AssertNear(Rgb24 expected, Rgba32 actual, int tolerance) =>
        AssertNear(expected, new Rgb24(actual.R, actual.G, actual.B), tolerance);

    private static void AssertNear(Rgb24 expected, Rgb24 actual, int tolerance) =>
        Assert.True(
            Math.Abs(expected.R - actual.R) <= tolerance
            && Math.Abs(expected.G - actual.G) <= tolerance
            && Math.Abs(expected.B - actual.B) <= tolerance,
            $"expected {expected} ± {tolerance}, got {actual}");

    /// <summary>'para' type 3 with the sRGB constants, as Apple's Display P3 profile stores its curves.</summary>
    internal static byte[] SrgbParametricCurve()
    {
        var tag = new byte[12 + (5 * 4)];
        WriteSignature(tag, 0, "para");
        BinaryPrimitives.WriteUInt16BigEndian(tag.AsSpan(8), 3);
        double[] p = [2.4, 1 / 1.055, 0.055 / 1.055, 1 / 12.92, 0.04045];
        for (var i = 0; i < p.Length; i++) WriteS15Fixed16(tag, 12 + (i * 4), p[i]);
        return tag;
    }

    /// <summary>'curv' with one u8Fixed8 gamma, as Adobe RGB (1998) stores 563/256.</summary>
    private static byte[] GammaCurve(double gamma)
    {
        var tag = new byte[14];
        WriteSignature(tag, 0, "curv");
        BinaryPrimitives.WriteUInt32BigEndian(tag.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt16BigEndian(tag.AsSpan(12), (ushort)Math.Round(gamma * 256));
        return tag;
    }

    /// <summary>'curv' as a sampled table, as the classic sRGB IEC61966-2.1 profile stores it.</summary>
    private static byte[] SampledSrgbCurve(int entries)
    {
        var tag = new byte[12 + (entries * 2)];
        WriteSignature(tag, 0, "curv");
        BinaryPrimitives.WriteUInt32BigEndian(tag.AsSpan(8), (uint)entries);
        for (var i = 0; i < entries; i++)
        {
            var v = i / (double)(entries - 1);
            var linear = v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
            BinaryPrimitives.WriteUInt16BigEndian(tag.AsSpan(12 + (i * 2)), (ushort)Math.Round(linear * 65535));
        }
        return tag;
    }

    /// <summary>A display RGB → XYZ matrix/TRC profile with one curve shared by the three channels.</summary>
    internal static byte[] Profile(double[][] colorants, byte[] curve)
    {
        var tags = new List<(string Signature, byte[] Data)>();
        foreach (var (name, xyz) in new[] { ("rXYZ", colorants[0]), ("gXYZ", colorants[1]), ("bXYZ", colorants[2]) })
        {
            var tag = new byte[20];
            WriteSignature(tag, 0, "XYZ ");
            for (var i = 0; i < 3; i++) WriteS15Fixed16(tag, 8 + (i * 4), xyz[i]);
            tags.Add((name, tag));
        }
        var white = new byte[20];
        WriteSignature(white, 0, "XYZ ");
        WriteS15Fixed16(white, 8, 0.9642);
        WriteS15Fixed16(white, 12, 1.0);
        WriteS15Fixed16(white, 16, 0.8249);
        tags.Add(("wtpt", white));
        tags.Add(("rTRC", curve));
        tags.Add(("gTRC", curve));
        tags.Add(("bTRC", curve));

        var tableEnd = 128 + 4 + (tags.Count * 12);
        var offsets = new List<int>();
        var cursor = tableEnd;
        foreach (var (_, data) in tags)
        {
            cursor = (cursor + 3) & ~3;
            offsets.Add(cursor);
            cursor += data.Length;
        }
        var profile = new byte[(cursor + 3) & ~3];
        BinaryPrimitives.WriteUInt32BigEndian(profile.AsSpan(0), (uint)profile.Length);
        BinaryPrimitives.WriteUInt32BigEndian(profile.AsSpan(8), 0x02100000);
        WriteSignature(profile, 12, "mntr");
        WriteSignature(profile, 16, "RGB ");
        WriteSignature(profile, 20, "XYZ ");
        WriteSignature(profile, 36, "acsp");
        WriteS15Fixed16(profile, 68, 0.9642);
        WriteS15Fixed16(profile, 72, 1.0);
        WriteS15Fixed16(profile, 76, 0.8249);
        BinaryPrimitives.WriteUInt32BigEndian(profile.AsSpan(128), (uint)tags.Count);
        for (var i = 0; i < tags.Count; i++)
        {
            WriteSignature(profile, 132 + (i * 12), tags[i].Signature);
            BinaryPrimitives.WriteUInt32BigEndian(profile.AsSpan(136 + (i * 12)), (uint)offsets[i]);
            BinaryPrimitives.WriteUInt32BigEndian(profile.AsSpan(140 + (i * 12)), (uint)tags[i].Data.Length);
            tags[i].Data.CopyTo(profile, offsets[i]);
        }
        return profile;
    }

    private static void WriteSignature(byte[] target, int at, string signature)
    {
        for (var i = 0; i < 4; i++) target[at + i] = (byte)signature[i];
    }

    private static void WriteS15Fixed16(byte[] target, int at, double value) =>
        BinaryPrimitives.WriteInt32BigEndian(target.AsSpan(at), (int)Math.Round(value * 65536));
}
