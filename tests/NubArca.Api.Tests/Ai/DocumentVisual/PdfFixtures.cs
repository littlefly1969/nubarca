using System.Text;

namespace NubArca.Api.Tests.Ai.DocumentVisual;

/// REAL PDFs, built here rather than committed.
///
/// The visual tests need documents PDFium will actually rasterise, which a
/// hand-written `%PDF-1.7 … %%EOF` stub is not: it satisfies a format probe and
/// produces no pages. Committing binary fixtures instead would put opaque blobs
/// in the repository that nobody can read, diff or adjust — and adjusting them
/// is exactly what a bounds test does.
///
/// So the bytes are assembled from a cross-reference table whose offsets are
/// measured as the file is written. Base-14 Helvetica means no embedded font
/// and no external asset.
internal static class PdfFixtures
{
    /// A `pages`-page document, each page carrying its own visible marker so a
    /// rendered page is distinguishable from its neighbours by ink alone.
    public static byte[] Pages(int pages, string prefix = "Page")
        => Build(Enumerable.Range(1, pages).Select(i => $"{prefix} {i}").ToArray());

    /// A document whose pages carry the given lines — one page per line.
    public static byte[] Build(IReadOnlyList<string> pageTexts)
    {
        if (pageTexts.Count == 0) throw new ArgumentException("At least one page.", nameof(pageTexts));

        using var buffer = new MemoryStream();
        var offsets = new List<long>();

        void Write(string text)
        {
            var bytes = Encoding.ASCII.GetBytes(text);
            buffer.Write(bytes, 0, bytes.Length);
        }

        void BeginObject(int number)
        {
            // Object numbers are 1-based and the xref's slot 0 is the free head,
            // so the recorded offset list is indexed by number - 1.
            while (offsets.Count < number) offsets.Add(0);
            offsets[number - 1] = buffer.Position;
            Write($"{number} 0 obj\n");
        }

        Write("%PDF-1.4\n");

        var fontNumber = 3 + (pageTexts.Count * 2);

        BeginObject(1);
        Write("<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        var kids = string.Join(" ", Enumerable.Range(0, pageTexts.Count).Select(i => $"{3 + (i * 2)} 0 R"));
        BeginObject(2);
        Write($"<< /Type /Pages /Kids [{kids}] /Count {pageTexts.Count} >>\nendobj\n");

        for (var i = 0; i < pageTexts.Count; i++)
        {
            var pageNumber = 3 + (i * 2);
            var contentNumber = pageNumber + 1;

            BeginObject(pageNumber);
            Write(
                $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
                + $"/Resources << /Font << /F1 {fontNumber} 0 R >> >> "
                + $"/Contents {contentNumber} 0 R >>\nendobj\n");

            // Large type, repeated down the page: a rasterised page with visible
            // ink, so an "is anything drawn" assertion means something.
            var lines = new StringBuilder();
            lines.Append("BT /F1 36 Tf 72 700 Td 44 TL\n");
            for (var line = 0; line < 12; line++)
            {
                lines.Append($"({Escape(pageTexts[i])} line {line}) Tj T*\n");
            }
            lines.Append("ET\n");
            var content = lines.ToString();

            BeginObject(contentNumber);
            Write($"<< /Length {content.Length} >>\nstream\n{content}endstream\nendobj\n");
        }

        BeginObject(fontNumber);
        Write("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");

        var startXref = buffer.Position;
        Write($"xref\n0 {fontNumber + 1}\n");
        Write("0000000000 65535 f \n");
        for (var i = 0; i < fontNumber; i++)
        {
            Write($"{offsets[i]:D10} 00000 n \n");
        }
        Write($"trailer\n<< /Size {fontNumber + 1} /Root 1 0 R >>\nstartxref\n{startXref}\n%%EOF\n");

        return buffer.ToArray();
    }

    private static string Escape(string text)
        => text.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");
}
