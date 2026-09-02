using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace NubArca.Api.Print;

public sealed class PrintArtifactRenderer
{
    public const int LandscapeWidth = 1800;
    public const int LandscapeHeight = 1200;

    private static readonly IReadOnlyDictionary<char, string> Glyphs = new Dictionary<char, string>
    {
        ['A']="01110100011000111111100011000110001", ['B']="11110100011000111110100011000111110",
        ['C']="01111100001000010000100001000001111", ['D']="11110100011000110001100011000111110",
        ['E']="11111100001000011110100001000011111", ['F']="11111100001000011110100001000010000",
        ['G']="01111100001000010111100011000101111", ['H']="10001100011000111111100011000110001",
        ['I']="11111001000010000100001000010011111", ['J']="00111000100001000010100101001001100",
        ['K']="10001100101010011000101001001010001", ['L']="10000100001000010000100001000011111",
        ['M']="10001110111010110101100011000110001", ['N']="10001110011010110011100011000110001",
        ['O']="01110100011000110001100011000101110", ['P']="11110100011000111110100001000010000",
        ['Q']="01110100011000110001101011001001101", ['R']="11110100011000111110101001001010001",
        ['S']="01111100001000001110000010000111110", ['T']="11111001000010000100001000010000100",
        ['U']="10001100011000110001100011000101110", ['V']="10001100011000110001100010101000100",
        ['W']="10001100011000110101101011101110001", ['X']="10001100010101000100010101000110001",
        ['Y']="10001100010101000100001000010000100", ['Z']="11111000010001000100010001000011111",
        ['0']="01110100011001110101110011000101110", ['1']="00100011000010000100001000010001110",
        ['2']="01110100010000100010001000100011111", ['3']="11110000010000101110000010000111110",
        ['4']="00010001100101010010111110001000010", ['5']="11111100001000011110000010000111110",
        ['6']="01110100001000011110100011000101110", ['7']="11111000010001000100010000100001000",
        ['8']="01110100011000101110100011000101110", ['9']="01110100011000101111000010000101110",
        ['-']="00000000000000011111000000000000000", [':']="00000001000000000000001000000000000",
        ['.']="00000000000000000000000000011000110", ['/']="00001000100001000100010001000010000",
        [' ']="00000000000000000000000000000000000",
    };

    public async Task<byte[]> RenderDiagnosticAsync(
        string stationName, string printerModel, DateTime now, string format,
        string shortCode, CancellationToken cancellationToken)
    {
        using var image = new Image<Rgb24>(LandscapeWidth, LandscapeHeight, new Rgb24(248, 250, 252));
        DrawBand(image, 0, 0, LandscapeWidth, 210, new Rgb24(8, 46, 73));
        DrawText(image, "NUBARCA PRINT STATION", 100, 70, 14, new Rgb24(255, 255, 255));
        DrawText(image, stationName.ToUpperInvariant(), 110, 330, 11, new Rgb24(8, 46, 73));
        DrawText(image, $"PRINTER {printerModel}".ToUpperInvariant(), 110, 500, 8, new Rgb24(30, 64, 87));
        DrawText(image, $"DATE {now:yyyy-MM-dd HH:mm} UTC".ToUpperInvariant(), 110, 635, 7, new Rgb24(30, 64, 87));
        DrawText(image, $"FORMAT {format}".ToUpperInvariant(), 110, 760, 7, new Rgb24(30, 64, 87));
        DrawText(image, $"JOB {shortCode}".ToUpperInvariant(), 110, 885, 9, new Rgb24(8, 104, 147));
        using var output = new MemoryStream();
        await image.SaveAsPngAsync(output, cancellationToken);
        return output.ToArray();
    }

    public async Task<byte[]> RenderPhoto10x15Async(
        ReadOnlyMemory<byte> source, CancellationToken cancellationToken)
    {
        using var image = Image.Load(source.Span);
        image.Mutate(x => x.AutoOrient());
        var portrait = image.Height > image.Width;
        var width = portrait ? LandscapeHeight : LandscapeWidth;
        var height = portrait ? LandscapeWidth : LandscapeHeight;
        image.Mutate(x => x.Resize(new ResizeOptions
        {
            Size = new Size(width, height),
            Mode = ResizeMode.Pad,
            PadColor = Color.White,
            Position = AnchorPositionMode.Center,
            Sampler = KnownResamplers.Lanczos3,
        }));
        using var output = new MemoryStream();
        await image.SaveAsJpegAsync(output, new JpegEncoder { Quality = 92 }, cancellationToken);
        return output.ToArray();
    }

    private static void DrawText(Image<Rgb24> image, string text, int x, int y, int scale, Rgb24 color)
    {
        var cursor = x;
        foreach (var raw in text)
        {
            var ch = char.ToUpperInvariant(raw);
            var glyph = Glyphs.GetValueOrDefault(ch, Glyphs[' ']);
            for (var row = 0; row < 7; row++)
            for (var column = 0; column < 5; column++)
            {
                if (glyph[row * 5 + column] != '1') continue;
                DrawBand(image, cursor + column * scale, y + row * scale, scale, scale, color);
            }
            cursor += 6 * scale;
            if (cursor >= image.Width - 6 * scale) break;
        }
    }

    private static void DrawBand(Image<Rgb24> image, int x, int y, int width, int height, Rgb24 color)
    {
        var maxX = Math.Min(image.Width, x + width);
        var maxY = Math.Min(image.Height, y + height);
        image.ProcessPixelRows(accessor =>
        {
            for (var py = Math.Max(0, y); py < maxY; py++)
            {
                var row = accessor.GetRowSpan(py);
                for (var px = Math.Max(0, x); px < maxX; px++) row[px] = color;
            }
        });
    }
}
