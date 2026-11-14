using NubArca.Api.Print;
using SixLabors.ImageSharp.PixelFormats;

namespace NubArca.Api.Lithophanes;

public sealed record LithophaneSettings
{
    public double DiameterMm { get; init; } = 80;
    public double MinThicknessMm { get; init; } = 0.8;
    public double MaxThicknessMm { get; init; } = 2.8;
    public double OpeningMm { get; init; } = 20;
    public double ResolutionMm { get; init; } = 0.3;
    public double PictureWidthDegrees { get; init; } = 160;
    public double PictureHeightDegrees { get; init; } = 110;
    public double CenterX { get; init; } = 0.5;
    public double CenterY { get; init; } = 0.5;
    public double Zoom { get; init; } = 1;
    public double Brightness { get; init; }
    public double Contrast { get; init; } = 1;
    public double Gamma { get; init; } = 1;
    // The wall outside the photograph. Only its inner surface moves: the
    // outside stays a perfect sphere. Absent in projects saved before it
    // existed, which keeps their background at the photo's thinnest wall.
    public double? BackgroundThicknessMm { get; init; }

    public double BackgroundWallMm => BackgroundThicknessMm ?? MinThicknessMm;
    // Width of the smooth ramp between the photograph's edge and the background.
    public const double BlendMm = 2;

    public const int MaxTriangles = 2_000_000;

    public bool IsValid() =>
        Between(DiameterMm, 40, 200) && Between(MinThicknessMm, 0.4, 2)
        && (BackgroundThicknessMm is null || Between(BackgroundThicknessMm.Value, 0.4, 2))
        && Between(MaxThicknessMm, MinThicknessMm + 0.1, 5)
        && Between(OpeningMm, 4, DiameterMm * 0.65)
        && Between(ResolutionMm, 0.15, 1)
        && Between(PictureWidthDegrees, 40, 300) && Between(PictureHeightDegrees, 30, 140)
        && PhotoPlacementGeometry.IsStructurallyValid(new(CenterX, CenterY, Zoom))
        && Between(Brightness, -0.5, 0.5) && Between(Contrast, 0.25, 3) && Between(Gamma, 0.3, 3)
        && new LithophaneGrid(this).TriangleCount <= MaxTriangles;

    private static bool Between(double v, double min, double max) => double.IsFinite(v) && v >= min && v <= max;

    public double Thickness(byte luminance) => MinThicknessMm + (1 - luminance / 255d) * (MaxThicknessMm - MinThicknessMm);

    // Photo wall and background wall mixed by coverage; a fully covered vertex
    // is exactly the photograph's own thickness.
    public double Thickness(La16 pixel) => pixel.A == 255 ? Thickness(pixel.L)
        : pixel.A / 255d * Thickness(pixel.L) + (1 - pixel.A / 255d) * BackgroundWallMm;
}

// The equator is an exact ring and the longitude count is divisible by four:
// even a coarse mesh has exactly the requested external bounding diameter.
public sealed class LithophaneGrid
{
    public LithophaneGrid(LithophaneSettings settings)
    {
        Radius = settings.DiameterMm / 2;
        Columns = 4 * (int)Math.Ceiling(Math.PI * settings.DiameterMm / settings.ResolutionMm / 4);
        TopRows = (int)Math.Ceiling(Math.PI * Radius / 2 / settings.ResolutionMm);
        var innerRadius = Radius - settings.MinThicknessMm;
        var bottomZ = -Math.Sqrt(innerRadius * innerRadius - settings.OpeningMm * settings.OpeningMm / 4);
        BottomAngle = Math.Acos(bottomZ / Radius);
        Rows = TopRows + (int)Math.Ceiling((BottomAngle - Math.PI / 2) * Radius / settings.ResolutionMm);
    }

    public double Radius { get; }
    public int Columns { get; }
    public int TopRows { get; }
    public int Rows { get; }
    public double BottomAngle { get; }
    public int TriangleCount => checked(4 * Columns * Rows);
    public long StlBytes => 84L + 50L * TriangleCount;
    public double Theta(int row) => row <= TopRows
        ? Math.PI / 2 * row / TopRows
        : Math.PI / 2 + (BottomAngle - Math.PI / 2) * (row - TopRows) / (Rows - TopRows);
}
