using System.Globalization;
using System.Text;

namespace NubArca.PrintAgent.Adapters;

/// <summary>
/// A 20x15 sheet as the two 10x15 pages the DNP joins: one PDF of two 4x6
/// portrait pages, each showing one half of the sheet's JPEG.
///
/// The server draws the whole 20x15 — two 10x15 side by side, or one above the
/// other — so the sheet the owner composed is the sheet that is stored. The
/// halves are cut out here without decoding a pixel: both pages draw the same
/// JPEG, placed so that only their half falls on the page. A lying half (a
/// landscape 10x15) is turned onto its standing page, so CUPS never has to
/// guess which way a page goes.
/// </summary>
public static class HalvesPdf
{
    /// <summary>A 4x6 page, standing, in points.</summary>
    private const double PageWidth = 288;
    private const double PageHeight = 432;

    /// <summary>The PDF, or null when the sheet is not a baseline or progressive RGB JPEG of two 10x15 halves.</summary>
    public static byte[]? FromSheet(byte[] jpeg)
    {
        if (ReadSize(jpeg) is not var (width, height, components) || components != 3) return null;
        var sideBySide = width > height;
        var (halfWidth, halfHeight) = sideBySide ? (width / 2.0, (double)height) : (width, height / 2.0);
        // A 10x15 is two to three, either way up.
        var longEdge = Math.Max(halfWidth, halfHeight);
        var shortEdge = Math.Min(halfWidth, halfHeight);
        if (Math.Abs((longEdge / shortEdge) - 1.5) > 0.01) return null;

        var pages = new[] { Content(0), Content(1) };
        string Content(int half)
        {
            // The half lies on a page that stands: draw it on the page turned
            // a quarter, a lying 432x288 page in the standing one.
            var lying = halfWidth > halfHeight;
            var (pageW, pageH) = lying ? (PageHeight, PageWidth) : (PageWidth, PageHeight);
            var scale = pageW / halfWidth;
            var (left, top) = sideBySide ? (half * halfWidth, 0.0) : (0.0, half * halfHeight);
            var x = -left * scale;
            var y = pageH - ((height - top) * scale);
            var turn = lying ? $"0 1 -1 0 {N(PageWidth)} 0 cm " : string.Empty;
            return $"q {turn}{N(width * scale)} 0 0 {N(height * scale)} {N(x)} {N(y)} cm /Im0 Do Q";
        }

        using var output = new MemoryStream();
        var offsets = new List<long>();
        void Ascii(string text)
        {
            var bytes = Encoding.ASCII.GetBytes(text);
            output.Write(bytes);
        }
        void Begin()
        {
            offsets.Add(output.Position);
            Ascii($"{offsets.Count} 0 obj\n");
        }

        Ascii("%PDF-1.4\n");
        // Four bytes above 127: the file is binary, whatever reads it.
        output.Write([0x25, 0xE2, 0xE3, 0xCF, 0xD3, 0x0A]);
        Begin();
        Ascii("<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Begin();
        Ascii("<< /Type /Pages /Kids [4 0 R 6 0 R] /Count 2 >>\nendobj\n");
        Begin();
        Ascii($"<< /Type /XObject /Subtype /Image /Width {width} /Height {height} /ColorSpace /DeviceRGB " +
            $"/BitsPerComponent 8 /Filter /DCTDecode /Length {jpeg.Length} >>\nstream\n");
        output.Write(jpeg);
        Ascii("\nendstream\nendobj\n");
        foreach (var content in pages)
        {
            var page = offsets.Count + 1;
            Begin();
            Ascii($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {N(PageWidth)} {N(PageHeight)}] " +
                $"/Resources << /XObject << /Im0 3 0 R >> >> /Contents {page + 1} 0 R >>\nendobj\n");
            Begin();
            Ascii($"<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n");
        }
        var xref = output.Position;
        Ascii($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets) Ascii($"{offset:D10} 00000 n \n");
        Ascii($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return output.ToArray();
    }

    // "+ 0.0" turns a negative zero into a plain one: "-0" is a number to a PDF reader, but untidy.
    private static string N(double value) => (value + 0.0).ToString("0.####", CultureInfo.InvariantCulture);

    /// <summary>The frame's size and colour components, from its first SOF marker.</summary>
    public static (int Width, int Height, int Components)? ReadSize(byte[] jpeg)
    {
        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8) return null;
        var i = 2;
        while (i + 4 <= jpeg.Length)
        {
            if (jpeg[i] != 0xFF) return null;
            var marker = jpeg[i + 1];
            if (marker == 0xFF) { i++; continue; }
            if (marker is 0xD8 or 0x01 || marker is >= 0xD0 and <= 0xD7) { i += 2; continue; }
            if (marker is 0xD9 or 0xDA) return null;
            var length = (jpeg[i + 2] << 8) | jpeg[i + 3];
            if (length < 2 || i + 2 + length > jpeg.Length) return null;
            // SOF0 baseline, SOF1 extended, SOF2 progressive: what a DCTDecode reads.
            if (marker is 0xC0 or 0xC1 or 0xC2)
            {
                if (length < 8) return null;
                var height = (jpeg[i + 5] << 8) | jpeg[i + 6];
                var width = (jpeg[i + 7] << 8) | jpeg[i + 8];
                var components = jpeg[i + 9];
                return width > 0 && height > 0 ? (width, height, components) : null;
            }
            i += 2 + length;
        }
        return null;
    }
}
