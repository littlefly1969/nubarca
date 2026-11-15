using System.Numerics;
using System.Buffers.Binary;
using System.Text;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace NubArca.Api.Lithophanes;

// Pure geometry, no persistence or dependency on a particular job host. Strips
// contain STL records only; a complete file is a header followed by the strips.
public static class LithophaneMesh
{
    public static void WriteHeader(Stream destination, LithophaneGrid grid)
    {
        Span<byte> header = stackalloc byte[80];
        header.Clear();
        Encoding.ASCII.GetBytes("NubArca spherical lithophane v1; millimetres", header);
        destination.Write(header);
        using var writer = new BinaryWriter(destination, Encoding.UTF8, leaveOpen: true);
        writer.Write((uint)grid.TriangleCount);
    }

    public static void WriteStrip(Stream destination, LithophaneSettings settings, Image<La16> map,
        int firstRow, int endRow, CancellationToken cancellationToken = default)
    {
        var grid = new LithophaneGrid(settings);
        if (map.Width != grid.Columns || map.Height != grid.Rows + 1
            || firstRow < 0 || endRow <= firstRow || endRow > grid.Rows)
            throw new ArgumentException("Invalid lithophane strip.");

        var longitudeSin = new double[grid.Columns];
        var longitudeCos = new double[grid.Columns];
        for (var col = 0; col < grid.Columns; col++)
        {
            var phi = 2 * Math.PI * col / grid.Columns - Math.PI;
            (longitudeSin[col], longitudeCos[col]) = Math.SinCos(phi);
        }
        var outer = new Vector3[grid.Columns + 1];
        var inner = new Vector3[grid.Columns + 1];
        var nextOuter = new Vector3[grid.Columns + 1];
        var nextInner = new Vector3[grid.Columns + 1];
        Ring(firstRow, outer, inner);
        for (var row = firstRow; row < endRow; row++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Ring(row + 1, nextOuter, nextInner);
            for (var col = 0; col < grid.Columns; col++)
            {
                if (row == 0)
                {
                    Triangle(outer[0], nextOuter[col], nextOuter[col + 1]);
                    Triangle(inner[0], nextInner[col + 1], nextInner[col]);
                }
                else
                {
                    Triangle(outer[col], nextOuter[col], nextOuter[col + 1]);
                    Triangle(outer[col], nextOuter[col + 1], outer[col + 1]);
                    Triangle(inner[col], nextInner[col + 1], nextInner[col]);
                    Triangle(inner[col], inner[col + 1], nextInner[col + 1]);
                }
                if (row + 1 == grid.Rows)
                {
                    Triangle(nextOuter[col], nextInner[col], nextInner[col + 1]);
                    Triangle(nextOuter[col], nextInner[col + 1], nextOuter[col + 1]);
                }
            }
            (outer, nextOuter) = (nextOuter, outer);
            (inner, nextInner) = (nextInner, inner);
        }

        void Ring(int row, Vector3[] outside, Vector3[] inside)
        {
            var theta = grid.Theta(row);
            var sin = Math.Sin(theta);
            var cos = Math.Cos(theta);
            for (var col = 0; col < grid.Columns; col++)
            {
                var direction = row == 0 ? Vector3.UnitZ
                    : new Vector3((float)(sin * longitudeSin[col]), (float)(-sin * longitudeCos[col]), (float)cos);
                outside[col] = direction * (float)grid.Radius;
                inside[col] = direction * (float)(grid.Radius - settings.Thickness(map[col, row]));
                if (row == grid.Rows)
                    inside[col] = new Vector3((float)(settings.OpeningMm / 2 * longitudeSin[col]),
                        (float)(-settings.OpeningMm / 2 * longitudeCos[col]), outside[col].Z);
            }
            // Exact same vertex at the seam, not sin(2π)'s rounded neighbour.
            outside[grid.Columns] = outside[0];
            inside[grid.Columns] = inside[0];
        }

        void Triangle(Vector3 a, Vector3 b, Vector3 c)
        {
            var normal = Vector3.Normalize(Vector3.Cross(b - a, c - a));
            Span<byte> record = stackalloc byte[50];
            Vertex(record, 0, normal); Vertex(record, 12, a); Vertex(record, 24, b); Vertex(record, 36, c);
            record[48] = 0; record[49] = 0;
            destination.Write(record);
        }
        static void Vertex(Span<byte> target, int offset, Vector3 v)
        {
            BinaryPrimitives.WriteSingleLittleEndian(target[offset..], v.X);
            BinaryPrimitives.WriteSingleLittleEndian(target[(offset + 4)..], v.Y);
            BinaryPrimitives.WriteSingleLittleEndian(target[(offset + 8)..], v.Z);
        }
    }
}
