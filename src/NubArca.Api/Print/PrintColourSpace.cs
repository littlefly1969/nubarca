using System.Buffers.Binary;
using System.Numerics;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Metadata;
using SixLabors.ImageSharp.Metadata.Profiles.Cicp;
using SixLabors.ImageSharp.Metadata.Profiles.Icc;
using SixLabors.ImageSharp.PixelFormats;

namespace NubArca.Api.Print;

/// <summary>
/// Brings a photograph's pixels into sRGB before they go on a sheet.
///
/// A sheet leaves the server with no colour profile, so the printer driver reads
/// its numbers as sRGB. A photograph in a wider space — Display P3 from a recent
/// phone, Adobe RGB from a camera — carries the same numbers for more saturated
/// colours; drawn as they are, it prints dull and shifted. Its own profile says
/// what the numbers mean, and this converts them (relative colorimetric, out of
/// gamut clipped) from that to sRGB.
///
/// It understands what photographs actually carry: an RGB matrix/TRC ICC
/// profile (JPEG, PNG, and Apple's Display P3), and a full-range PNG cICP tag
/// (what FFmpeg writes for a decoded HEIC frame), which outranks an ICC profile
/// beside it. Anything else — sRGB itself, LUT-based or non-RGB profiles,
/// limited-range or unspecified cICP, HDR transfer functions — is left exactly
/// as it was: never worse than before.
/// </summary>
public static class PrintColourSpace
{
    // sRGB's colorants adapted to the ICC D50 white, as in the IEC 61966-2.1
    // profile: the columns are red, green and blue in XYZ.
    private static readonly Matrix3 SrgbToXyzD50 = new(
        0.4360747, 0.3850649, 0.1430804,
        0.2225045, 0.7168786, 0.0606169,
        0.0139322, 0.0971045, 0.7141733);

    private static readonly Matrix3 XyzD50ToSrgb = SrgbToXyzD50.Inverse();

    // Bradford adaptation from D65 to D50, for the cICP primaries (defined at D65).
    private static readonly Matrix3 BradfordD65ToD50 = new(
        1.0478112, 0.0228866, -0.0501270,
        0.0295424, 0.9904844, -0.0170491,
        -0.0092345, 0.0150436, 0.7521316);

    private const int EncodeSteps = 16384;
    private static readonly byte[] SrgbEncode = BuildSrgbEncode();
    private static readonly float[] SrgbDecode = BuildDecode(SrgbToLinear);

    /// <summary>
    /// Converts <paramref name="image"/> in place to sRGB, reading what its
    /// numbers mean from <paramref name="source"/> — the metadata of the
    /// photograph it was cut from. Returns whether anything changed.
    /// </summary>
    public static bool ToSrgb(Image<Rgba32> image, ImageMetadata source)
    {
        // A cICP tag outranks an ICC profile (the PNG spec, cICP chunk): when one is
        // present it alone says what the numbers mean, and one this cannot
        // follow leaves the pixels alone rather than falling back to the ICC.
        var conversion = source.CicpProfile is { } cicp ? FromCicp(cicp) : FromIcc(source.IccProfile);
        if (conversion is not { } c || c.IsSrgb) return false;

        var (r, g, b) = c.Decode;
        var m = c.ToSrgb;
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    ref var p = ref row[x];
                    double lr = r[p.R], lg = g[p.G], lb = b[p.B];
                    p.R = Encode((m.M11 * lr) + (m.M12 * lg) + (m.M13 * lb));
                    p.G = Encode((m.M21 * lr) + (m.M22 * lg) + (m.M23 * lb));
                    p.B = Encode((m.M31 * lr) + (m.M32 * lg) + (m.M33 * lb));
                }
            }
        });
        return true;
    }

    private static byte Encode(double linear) =>
        SrgbEncode[(int)Math.Round(Math.Clamp(linear, 0, 1) * (EncodeSteps - 1))];

    // --- What the numbers mean ----------------------------------------------

    private readonly record struct Conversion((float[] R, float[] G, float[] B) Decode, Matrix3 ToSrgb)
    {
        /// <summary>sRGB already: converting would only round the pixels.</summary>
        public bool IsSrgb =>
            ToSrgb.IsNearIdentity(0.002)
            && SameCurve(Decode.R, SrgbDecode) && SameCurve(Decode.G, SrgbDecode) && SameCurve(Decode.B, SrgbDecode);

        private static bool SameCurve(float[] a, float[] b)
        {
            for (var i = 0; i < a.Length; i++)
                if (Math.Abs(SrgbToEncoded(a[i]) - SrgbToEncoded(b[i])) > 0.5 / 255) return false;
            return true;
        }
    }

    private static Conversion? FromIcc(IccProfile? profile)
    {
        if (profile is null) return null;
        try
        {
            // ImageSharp parses a profile but keeps its tags to itself, so the
            // few this needs are read from the profile's own bytes (ICC.1).
            var bytes = profile.ToByteArray();
            if (bytes.Length < 132
                || BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16)) != 0x52474220 // 'RGB '
                || BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20)) != 0x58595A20) // 'XYZ '
                return null;

            if (Column(bytes, 0x7258595A) is not { } rc // rXYZ
                || Column(bytes, 0x6758595A) is not { } gc // gXYZ
                || Column(bytes, 0x6258595A) is not { } bc) return null; // bXYZ
            if (Curve(bytes, 0x72545243) is not { } rt // rTRC
                || Curve(bytes, 0x67545243) is not { } gt // gTRC
                || Curve(bytes, 0x62545243) is not { } bt) return null; // bTRC

            var toXyz = new Matrix3(
                rc.X, gc.X, bc.X,
                rc.Y, gc.Y, bc.Y,
                rc.Z, gc.Z, bc.Z);
            return new Conversion((rt, gt, bt), XyzD50ToSrgb * toXyz);
        }
        catch (Exception ex) when (ex is InvalidIccProfileException or ArgumentException
            or ArgumentOutOfRangeException or InvalidOperationException)
        {
            // A profile that cannot be read says nothing we can trust.
            return null;
        }
    }

    /// <summary>The tag stored under <paramref name="signature"/>, from its type signature on.</summary>
    private static ReadOnlySpan<byte> Tag(byte[] profile, uint signature)
    {
        var span = profile.AsSpan();
        var count = BinaryPrimitives.ReadUInt32BigEndian(span[128..]);
        for (long i = 0; i < count && 132 + ((i + 1) * 12) <= span.Length; i++)
        {
            var row = span.Slice(132 + (int)(i * 12), 12);
            if (BinaryPrimitives.ReadUInt32BigEndian(row) != signature) continue;
            var offset = BinaryPrimitives.ReadUInt32BigEndian(row[4..]);
            var size = BinaryPrimitives.ReadUInt32BigEndian(row[8..]);
            if (size < 12 || offset > span.Length || size > span.Length - offset) return default;
            return span.Slice((int)offset, (int)size);
        }
        return default;
    }

    private static double S15Fixed16(ReadOnlySpan<byte> at) => BinaryPrimitives.ReadInt32BigEndian(at) / 65536.0;

    /// <summary>A colorant: one 'XYZ ' value.</summary>
    private static (double X, double Y, double Z)? Column(byte[] profile, uint signature)
    {
        var tag = Tag(profile, signature);
        if (tag.Length < 20 || BinaryPrimitives.ReadUInt32BigEndian(tag) != 0x58595A20) return null;
        return (S15Fixed16(tag[8..]), S15Fixed16(tag[12..]), S15Fixed16(tag[16..]));
    }

    /// <summary>A tone curve ('curv' or 'para') as 256 linear values, or null for one this cannot follow.</summary>
    private static float[]? Curve(byte[] profile, uint signature)
    {
        var tag = Tag(profile, signature);
        if (tag.Length < 12) return null;
        switch (BinaryPrimitives.ReadUInt32BigEndian(tag))
        {
            case 0x63757276: // 'curv'
            {
                // The count comes from an uploaded file: bounded before any
                // arithmetic or allocation, and in 64 bits so it cannot wrap.
                long count = BinaryPrimitives.ReadUInt32BigEndian(tag[8..]);
                if (count == 0) return BuildDecode(v => v);
                if (count > MaxCurveEntries || 12 + (count * 2) > tag.Length) return null;
                if (count == 1)
                {
                    var gamma = BinaryPrimitives.ReadUInt16BigEndian(tag[12..]) / 256.0; // u8Fixed8
                    return BuildDecode(v => Math.Pow(v, gamma));
                }
                var table = new float[count];
                for (var i = 0; i < count; i++)
                    table[i] = BinaryPrimitives.ReadUInt16BigEndian(tag[(12 + (i * 2))..]) / 65535f;
                return BuildDecode(v => Sampled(table, v));
            }
            case 0x70617261: // 'para'
            {
                var type = BinaryPrimitives.ReadUInt16BigEndian(tag[8..]);
                var needed = type switch { 0 => 1, 1 => 3, 2 => 4, 3 => 5, 4 => 7, _ => 0 };
                if (needed == 0 || 12 + (needed * 4) > tag.Length) return null;
                var p = new double[7];
                for (var k = 0; k < needed; k++) p[k] = S15Fixed16(tag[(12 + (k * 4))..]);
                double g = p[0], a = p[1], b = p[2], c = p[3], d = p[4], e = p[5], f = p[6];
                Func<double, double> curve = type switch
                {
                    0 => x => Math.Pow(x, g),
                    1 => x => x >= -b / a ? Math.Pow((a * x) + b, g) : 0,
                    2 => x => x >= -b / a ? Math.Pow((a * x) + b, g) + c : c,
                    3 => x => x >= d ? Math.Pow((a * x) + b, g) : c * x,
                    _ => x => x >= d ? Math.Pow((a * x) + b, g) + e : (c * x) + f,
                };
                return BuildDecode(curve);
            }
            default:
                return null;
        }
    }

    private static double Sampled(float[] table, double v)
    {
        var at = v * (table.Length - 1);
        var i = (int)Math.Floor(at);
        if (i >= table.Length - 1) return table[^1];
        return table[i] + ((table[i + 1] - table[i]) * (at - i));
    }

    /// <summary>The longest sampled curve followed: 16-bit input resolution, far beyond any real profile.</summary>
    private const int MaxCurveEntries = 65536;

    private static Conversion? FromCicp(CicpProfile cicp)
    {
        // Full-range RGB only: limited (video) range needs its levels remapped
        // first, which this does not do, so it is left exactly as it is.
        if (cicp.FullRange != true) return null;
        Matrix3? toXyzD65 = cicp.ColorPrimaries switch
        {
            CicpColorPrimaries.ItuRBt709_6 => FromPrimaries(0.640, 0.330, 0.300, 0.600, 0.150, 0.060),
            CicpColorPrimaries.SmpteEg432_1 => FromPrimaries(0.680, 0.320, 0.265, 0.690, 0.150, 0.060),
            CicpColorPrimaries.ItuRBt2020_2 => FromPrimaries(0.708, 0.292, 0.170, 0.797, 0.131, 0.046),
            _ => null,
        };
        Func<double, double>? decode = cicp.TransferCharacteristics switch
        {
            CicpTransferCharacteristics.Iec61966_2_1 => SrgbToLinear,
            CicpTransferCharacteristics.ItuRBt709_6 or CicpTransferCharacteristics.ItuRBt601_7
                or CicpTransferCharacteristics.ItuRBt2020_2_10bit or CicpTransferCharacteristics.ItuRBt2020_2_12bit
                => Bt709ToLinear,
            _ => null, // Unspecified, PQ, HLG and the rest: not known, so not touched
        };
        if (toXyzD65 is not { } m || decode is null) return null;
        var curve = BuildDecode(decode);
        return new Conversion((curve, curve, curve), XyzD50ToSrgb * BradfordD65ToD50 * m);
    }

    /// <summary>RGB → XYZ for chromaticities at the D65 white.</summary>
    private static Matrix3 FromPrimaries(double rx, double ry, double gx, double gy, double bx, double by)
    {
        static (double X, double Y, double Z) Xyz(double x, double y) => (x / y, 1, (1 - x - y) / y);
        var (r, g, b) = (Xyz(rx, ry), Xyz(gx, gy), Xyz(bx, by));
        var primaries = new Matrix3(r.X, g.X, b.X, r.Y, g.Y, b.Y, r.Z, g.Z, b.Z);
        var white = Xyz(0.3127, 0.3290);
        var s = primaries.Inverse().Apply(white.X, white.Y, white.Z);
        return new Matrix3(
            r.X * s.A, g.X * s.B, b.X * s.C,
            r.Y * s.A, g.Y * s.B, b.Y * s.C,
            r.Z * s.A, g.Z * s.B, b.Z * s.C);
    }

    // --- Curves ---------------------------------------------------------------

    private static double SrgbToLinear(double v) => v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);

    private static double SrgbToEncoded(double l) => l <= 0.0031308 ? 12.92 * l : (1.055 * Math.Pow(l, 1 / 2.4)) - 0.055;

    private static double Bt709ToLinear(double v) => v < 0.081 ? v / 4.5 : Math.Pow((v + 0.099) / 1.099, 1 / 0.45);

    private static float[] BuildDecode(Func<double, double> curve)
    {
        var table = new float[256];
        for (var i = 0; i < table.Length; i++) table[i] = (float)Math.Clamp(curve(i / 255.0), 0, 1);
        return table;
    }

    private static byte[] BuildSrgbEncode()
    {
        var table = new byte[EncodeSteps];
        for (var i = 0; i < table.Length; i++)
            table[i] = (byte)Math.Round(Math.Clamp(SrgbToEncoded(i / (double)(EncodeSteps - 1)), 0, 1) * 255);
        return table;
    }

    /// <summary>A 3×3 matrix in double precision, row-major — just what the conversion needs.</summary>
    private readonly record struct Matrix3(
        double M11, double M12, double M13,
        double M21, double M22, double M23,
        double M31, double M32, double M33)
    {
        public static Matrix3 operator *(Matrix3 a, Matrix3 b) => new(
            (a.M11 * b.M11) + (a.M12 * b.M21) + (a.M13 * b.M31),
            (a.M11 * b.M12) + (a.M12 * b.M22) + (a.M13 * b.M32),
            (a.M11 * b.M13) + (a.M12 * b.M23) + (a.M13 * b.M33),
            (a.M21 * b.M11) + (a.M22 * b.M21) + (a.M23 * b.M31),
            (a.M21 * b.M12) + (a.M22 * b.M22) + (a.M23 * b.M32),
            (a.M21 * b.M13) + (a.M22 * b.M23) + (a.M23 * b.M33),
            (a.M31 * b.M11) + (a.M32 * b.M21) + (a.M33 * b.M31),
            (a.M31 * b.M12) + (a.M32 * b.M22) + (a.M33 * b.M32),
            (a.M31 * b.M13) + (a.M32 * b.M23) + (a.M33 * b.M33));

        public (double A, double B, double C) Apply(double x, double y, double z) => (
            (M11 * x) + (M12 * y) + (M13 * z),
            (M21 * x) + (M22 * y) + (M23 * z),
            (M31 * x) + (M32 * y) + (M33 * z));

        public Matrix3 Inverse()
        {
            var det = (M11 * ((M22 * M33) - (M23 * M32)))
                - (M12 * ((M21 * M33) - (M23 * M31)))
                + (M13 * ((M21 * M32) - (M22 * M31)));
            if (Math.Abs(det) < 1e-12) throw new InvalidOperationException("Singular colour matrix.");
            var k = 1 / det;
            return new(
                k * ((M22 * M33) - (M23 * M32)), k * ((M13 * M32) - (M12 * M33)), k * ((M12 * M23) - (M13 * M22)),
                k * ((M23 * M31) - (M21 * M33)), k * ((M11 * M33) - (M13 * M31)), k * ((M13 * M21) - (M11 * M23)),
                k * ((M21 * M32) - (M22 * M31)), k * ((M12 * M31) - (M11 * M32)), k * ((M11 * M22) - (M12 * M21)));
        }

        public bool IsNearIdentity(double tolerance) =>
            Math.Abs(M11 - 1) < tolerance && Math.Abs(M22 - 1) < tolerance && Math.Abs(M33 - 1) < tolerance
            && Math.Abs(M12) < tolerance && Math.Abs(M13) < tolerance && Math.Abs(M21) < tolerance
            && Math.Abs(M23) < tolerance && Math.Abs(M31) < tolerance && Math.Abs(M32) < tolerance;
    }
}
