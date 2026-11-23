namespace NubArca.Api.Print;

// Null at a call site is the historical caption below the photograph.
public sealed record PhotoTextLayout(
    string Mode = "below", double X = 0.5, double Y = 0.5,
    double Width = 0.65, double Height = 0.35, string Tail = "left")
{
    public bool IsValid() => Mode is "below" or "overlay" or "bubble"
        && Between(X, 0, 1) && Between(Y, 0, 1)
        && Between(Width, 0.2, 0.95) && Between(Height, 0.15, 0.8)
        && Tail is "left" or "right";
    private static bool Between(double value, double min, double max) =>
        double.IsFinite(value) && value >= min && value <= max;
}
