using System.Buffers.Binary;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace NubArca.Api.Print;

/// <summary>
/// A print artifact made of several files: the two 10x15 of a joined 20x15
/// (<c>4x6x2</c>, <c>2x6x4</c>), each its own JPEG, rendered and encoded on its
/// own. They are stored as ONE object so the job keeps one owner of its bytes
/// (<c>PrintJob.ArtifactStorageKey</c>) and every storage rule stays as it is;
/// the agent downloads each part separately, as the file it is, and hands both
/// to one CUPS job.
///
/// Layout: the magic, the number of parts, each part's length (32-bit big
/// endian), then the parts back to back. The header comes first so a claim can
/// say each part's size by reading only it.
/// </summary>
public static class PrintArtifactParts
{
    public const string ContentType = "application/vnd.nubarca.print-parts";

    /// <summary>The content type of every part: the printer's own, a JPEG.</summary>
    public const string PartContentType = "image/jpeg";

    private static ReadOnlySpan<byte> Magic => "NBPARTS1"u8;
    private const int MaxParts = 8;

    public static byte[] Pack(IReadOnlyList<byte[]> parts)
    {
        if (parts.Count is < 1 or > MaxParts) throw new ArgumentOutOfRangeException(nameof(parts));
        var header = HeaderLength(parts.Count);
        var output = new byte[header + parts.Sum(p => (long)p.Length)];
        Magic.CopyTo(output);
        output[Magic.Length] = (byte)parts.Count;
        var offset = header;
        for (var i = 0; i < parts.Count; i++)
        {
            BinaryPrimitives.WriteInt32BigEndian(output.AsSpan(Magic.Length + 1 + (4 * i)), parts[i].Length);
            parts[i].CopyTo(output, offset);
            offset += parts[i].Length;
        }
        return output;
    }

    /// <summary>Each part's length, read from the header alone; null when the stream is not a parts artifact.</summary>
    public static async Task<IReadOnlyList<int>?> ReadLengthsAsync(Stream stream, CancellationToken cancellationToken)
    {
        var start = new byte[Magic.Length + 1];
        if (!await ReadExactlyAsync(stream, start, cancellationToken) || !start.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            return null;
        var count = start[Magic.Length];
        if (count is < 1 or > MaxParts) return null;
        var lengths = new byte[4 * count];
        if (!await ReadExactlyAsync(stream, lengths, cancellationToken)) return null;
        var result = new int[count];
        for (var i = 0; i < count; i++)
        {
            result[i] = BinaryPrimitives.ReadInt32BigEndian(lengths.AsSpan(4 * i));
            if (result[i] <= 0) return null;
        }
        return result;
    }

    /// <summary>Where part <paramref name="index"/> starts in the artifact, and how long it is; null when there is no such part.</summary>
    public static (long Offset, int Length)? Locate(IReadOnlyList<int> lengths, int index)
    {
        if (index < 0 || index >= lengths.Count) return null;
        long offset = HeaderLength(lengths.Count);
        for (var i = 0; i < index; i++) offset += lengths[i];
        return (offset, lengths[index]);
    }

    /// <summary>Every part's bytes, for the owner's view of a simulated print.</summary>
    public static async Task<IReadOnlyList<byte[]>?> ReadAllAsync(Stream stream, CancellationToken cancellationToken)
    {
        var lengths = await ReadLengthsAsync(stream, cancellationToken);
        if (lengths is null) return null;
        var parts = new List<byte[]>(lengths.Count);
        foreach (var length in lengths)
        {
            var part = new byte[length];
            if (!await ReadExactlyAsync(stream, part, cancellationToken)) return null;
            parts.Add(part);
        }
        return parts;
    }

    /// <summary>
    /// The parts as the sheet they come out of, for the owner's view of a
    /// simulated print only — never sent to a printer: standing 10x15 side by
    /// side, lying ones one above the other.
    /// </summary>
    public static async Task<byte[]> JoinForViewAsync(IReadOnlyList<byte[]> parts, CancellationToken cancellationToken)
    {
        var images = parts.Select(p => Image.Load<Rgb24>(p)).ToList();
        try
        {
            var sideBySide = images[0].Height >= images[0].Width;
            var width = sideBySide ? images.Sum(i => i.Width) : images.Max(i => i.Width);
            var height = sideBySide ? images.Max(i => i.Height) : images.Sum(i => i.Height);
            using var sheet = new Image<Rgb24>(width, height, new Rgb24(0xFF, 0xFF, 0xFF));
            var at = 0;
            foreach (var image in images)
            {
                var point = sideBySide ? new Point(at, 0) : new Point(0, at);
                sheet.Mutate(x => x.DrawImage(image, point, 1f));
                at += sideBySide ? image.Width : image.Height;
            }
            using var output = new MemoryStream();
            await sheet.SaveAsJpegAsync(output, new JpegEncoder { Quality = 90 }, cancellationToken);
            return output.ToArray();
        }
        finally
        {
            foreach (var image in images) image.Dispose();
        }
    }

    private static int HeaderLength(int count) => Magic.Length + 1 + (4 * count);

    private static async Task<bool> ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        try
        {
            await stream.ReadExactlyAsync(buffer, cancellationToken);
            return true;
        }
        catch (EndOfStreamException)
        {
            return false;
        }
    }
}
