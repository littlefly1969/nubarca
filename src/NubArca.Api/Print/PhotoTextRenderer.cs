using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace NubArca.Api.Print;

public static class PhotoTextRenderer
{
    // Geometry mirrors photoTextRect and the browser's speech polygon.
    public static void Draw(Image<Rgba32> sheet, Rectangle frame, string? text,
        PhotoTextLayout? layout, string? fontId = null, string? assetRoot = null)
    {
        if (string.IsNullOrEmpty(text) || layout is null || layout.Mode == "below") return;
        if (!layout.IsValid()) throw new ArgumentException("Invalid text layout", nameof(layout));
        var width = (float)(frame.Width * layout.Width);
        var height = (float)(frame.Height * layout.Height);
        var left = frame.X + (float)((frame.Width - width) * layout.X);
        var top = frame.Y + (float)((frame.Height - height) * layout.Y);
        var bubble = layout.Mode == "bubble";
        if (bubble)
        {
            PointF P(double x, double y) => new(left + (float)x * width, top + (float)y * height);
            PointF[] points = layout.Tail == "left"
                ? [P(.01,.01),P(.99,.01),P(.99,.84),P(.32,.84),P(.16,.98),P(.20,.84),P(.01,.84)]
                : [P(.01,.01),P(.99,.01),P(.99,.84),P(.80,.84),P(.84,.98),P(.68,.84),P(.01,.84)];
            var shape = new Polygon(new LinearLineSegment(points));
            sheet.Mutate(x => x.Fill(Color.White, shape).Draw(Color.FromRgb(23,23,23), Math.Max(1, width * .003f), shape));
        }
        var insetX = width * .08f;
        var insetY = height * .08f;
        var availableWidth = width - 2 * insetX;
        var availableHeight = height * (bubble ? .68f : .84f);
        var family = TextFonts.Family(fontId, assetRoot);
        var size = width * .075f;
        RichTextOptions Options(float px) => new(family.CreateFont(px))
        {
            WrappingLength = availableWidth, TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        var options = Options(size);
        var bounds = TextMeasurer.MeasureBounds(text, options);
        while (size > 1 && (bounds.Width > availableWidth || bounds.Height > availableHeight))
        {
            size *= .9f; options = Options(size); bounds = TextMeasurer.MeasureBounds(text, options);
        }
        options.Origin = new PointF(left + width / 2, top + insetY + (availableHeight - bounds.Height) / 2 - bounds.Y);
        if (!bubble)
        {
            var origin = options.Origin;
            options.Origin = new PointF(origin.X, origin.Y + Math.Max(1, size * .04f));
            sheet.Mutate(x => x.DrawText(options, text, Color.Black));
            options.Origin = origin;
        }
        sheet.Mutate(x => x.DrawText(options, text, bubble ? Color.FromRgb(23,23,23) : Color.White));
    }
}
