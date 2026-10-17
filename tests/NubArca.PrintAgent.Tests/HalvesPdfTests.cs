using System.Text;
using NubArca.PrintAgent.Adapters;

namespace NubArca.PrintAgent.Tests;

/// <summary>A 20x15 sheet as the two 10x15 pages the DNP joins, cut out without decoding it.</summary>
public sealed class HalvesPdfTests
{
    private static string Text(byte[] pdf) => Encoding.Latin1.GetString(pdf);

    [Fact]
    public void A_Lying_Sheet_Is_Two_Standing_Pages_Left_Then_Right()
    {
        var pdf = Text(HalvesPdf.FromSheet(CupsPrinterAdapterTests.SheetJpeg(2400, 1800))!);
        Assert.StartsWith("%PDF-1.4", pdf);
        Assert.Contains("/Count 2", pdf);
        Assert.Contains("/Width 2400 /Height 1800", pdf);
        Assert.Equal(2, pdf.Split("/MediaBox [0 0 288 432]").Length - 1);
        // 0.24 points a pixel: the left half, then the image moved one half left.
        Assert.Contains("q 576 0 0 432 0 0 cm /Im0 Do Q", pdf);
        Assert.Contains("q 576 0 0 432 -288 0 cm /Im0 Do Q", pdf);
        Assert.EndsWith("%%EOF\n", pdf);
    }

    [Fact]
    public void A_Standing_Sheet_Is_Its_Top_Then_Its_Bottom_Turned_Onto_Standing_Pages()
    {
        var pdf = Text(HalvesPdf.FromSheet(CupsPrinterAdapterTests.SheetJpeg(1800, 2400))!);
        Assert.Contains("q 0 1 -1 0 288 0 cm 432 0 0 576 0 -288 cm /Im0 Do Q", pdf);
        Assert.Contains("q 0 1 -1 0 288 0 cm 432 0 0 576 0 0 cm /Im0 Do Q", pdf);
    }

    [Fact]
    public void The_Cross_Reference_Points_At_Every_Object()
    {
        var bytes = HalvesPdf.FromSheet(CupsPrinterAdapterTests.SheetJpeg(2400, 1800))!;
        var pdf = Text(bytes);
        var xref = int.Parse(pdf[(pdf.LastIndexOf("startxref\n", StringComparison.Ordinal) + 10)..].Split('\n')[0]);
        Assert.StartsWith("xref\n0 8\n", pdf[xref..]);
        var entries = pdf[xref..].Split('\n').Skip(3).Take(7).Select(line => int.Parse(line[..10])).ToList();
        for (var i = 0; i < entries.Count; i++)
            Assert.StartsWith($"{i + 1} 0 obj", pdf[entries[i]..]);
    }

    [Theory]
    [InlineData(2400, 2400)]   // halves that are not 10x15
    [InlineData(1200, 1800)]   // one 10x15, not two
    public void A_Sheet_That_Is_Not_Two_10x15_Is_Refused(int width, int height) =>
        Assert.Null(HalvesPdf.FromSheet(CupsPrinterAdapterTests.SheetJpeg(width, height)));

    [Fact]
    public void Only_An_RGB_JPEG_Is_Read()
    {
        Assert.Null(HalvesPdf.FromSheet([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A]));
        Assert.Null(HalvesPdf.FromSheet([0xFF, 0xD8, 0xFF, 0xD9]));
        Assert.Equal((2400, 1800, 3), HalvesPdf.ReadSize(CupsPrinterAdapterTests.SheetJpeg(2400, 1800)));
    }
}
