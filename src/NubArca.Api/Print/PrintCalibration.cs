using System.Numerics;
using NubArca.Api.Domain.Print;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;

namespace NubArca.Api.Print;

/// <summary>
/// How one printer's output is compensated, set by its owner from the Print
/// stations page.
///
/// A dye-sublimation printer and its driver have their own tone response: the
/// same sheet can come out darker on one than on another. This is applied to the
/// WHOLE sheet the server renders for that printer — paper and photographs
/// alike — so it corrects the printer, not the picture: the guest's preview is
/// the composition, and it does not change. Every factor is 1 when neutral.
/// </summary>
public sealed record PrintCalibration(double Brightness, double Contrast, double Gamma, double Saturation)
{
    public static readonly PrintCalibration Neutral = new(1, 1, 1, 1);

    public const double MinBrightness = 0.7, MaxBrightness = 1.3;
    public const double MinContrast = 0.7, MaxContrast = 1.3;
    /// <summary>Above 1 the midtones print lighter; black and white stay where they are.</summary>
    public const double MinGamma = 0.6, MaxGamma = 1.6;
    public const double MinSaturation = 0.5, MaxSaturation = 1.5;

    public bool IsNeutral => this == Neutral;

    public bool IsValid =>
        Within(Brightness, MinBrightness, MaxBrightness)
        && Within(Contrast, MinContrast, MaxContrast)
        && Within(Gamma, MinGamma, MaxGamma)
        && Within(Saturation, MinSaturation, MaxSaturation);

    public static PrintCalibration Of(PrinterDevice device) => new(
        device.CalibrationBrightness, device.CalibrationContrast,
        device.CalibrationGamma, device.CalibrationSaturation);

    /// <summary>Midtones first, then light, contrast and colour — the order a printer profile adjusts in.</summary>
    public void ApplyTo(Image image)
    {
        if (IsNeutral) return;
        image.Mutate(x =>
        {
            if (Gamma != 1)
            {
                var exponent = (float)(1 / Gamma);
                x.ProcessPixelRowsAsVector4(row =>
                {
                    for (var i = 0; i < row.Length; i++)
                    {
                        var v = row[i];
                        row[i] = new Vector4(
                            MathF.Pow(v.X, exponent), MathF.Pow(v.Y, exponent), MathF.Pow(v.Z, exponent), v.W);
                    }
                });
            }
            if (Brightness != 1) x.Brightness((float)Brightness);
            if (Contrast != 1) x.Contrast((float)Contrast);
            if (Saturation != 1) x.Saturate((float)Saturation);
        });
    }

    private static bool Within(double value, double min, double max) =>
        double.IsFinite(value) && value >= min && value <= max;
}
