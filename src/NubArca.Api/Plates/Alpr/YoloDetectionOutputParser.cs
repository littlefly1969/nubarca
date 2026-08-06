namespace NubArca.Api.Plates.Alpr;

// Pure, dependency-free parser for a YOLO-family plate detector's raw output
// tensor. NO ONNX types here so it is fully unit-testable on synthetic tensors.
//
// Supported contract (documented in docs/model-deployment/plates.md): a single
// output with F = 4 + NumClasses channels per candidate in either layout
//   [1, F, N]  (channels-first, e.g. YOLOv8 export) or
//   [1, N, F]  (candidates-first, e.g. some YOLOv5 exports)
// where each candidate is [cx, cy, w, h, class_scores...] in the DETECTOR INPUT
// pixel space (no separate objectness channel). Detection confidence is the max
// class score. Boxes are letterbox-unmapped to the original image and returned
// NORMALIZED to [0..1]. An output whose shape cannot be matched to F throws
// PlateModelOutputException → the caller maps it to a safe
// plate_detector_output_unsupported error (never a raw tensor dump).
public static class YoloDetectionOutputParser
{
    public sealed record ParsedBox(double X, double Y, double Width, double Height, double Score);

    // Letterbox geometry: the source was resized by `scale` and centered with
    // (padX, padY) borders inside an (inputWidth x inputHeight) canvas.
    public readonly record struct Letterbox(
        double Scale, double PadX, double PadY,
        int InputWidth, int InputHeight, int OriginalWidth, int OriginalHeight);

    public static IReadOnlyList<ParsedBox> Parse(
        float[] data,
        int[] dims,
        int numClasses,
        double confidenceThreshold,
        double nmsThreshold,
        Letterbox letterbox)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(dims);
        if (numClasses < 1)
        {
            numClasses = 1;
        }
        var fields = 4 + numClasses;

        var (count, fieldStride, candidateStride, channelsFirst) = ResolveLayout(dims, fields, data.Length);

        var raw = new List<ParsedBox>(Math.Min(count, 1024));
        for (var i = 0; i < count; i++)
        {
            // Element (field f, candidate i) index in the flat buffer.
            int Idx(int f) => channelsFirst ? f * fieldStride + i : i * candidateStride + f;

            var cx = data[Idx(0)];
            var cy = data[Idx(1)];
            var w = data[Idx(2)];
            var h = data[Idx(3)];

            var best = 0.0;
            for (var c = 0; c < numClasses; c++)
            {
                var s = data[Idx(4 + c)];
                if (s > best)
                {
                    best = s;
                }
            }
            if (best < confidenceThreshold)
            {
                continue;
            }

            // Center form (input space) → corner form (input space).
            var x1 = cx - w / 2.0;
            var y1 = cy - h / 2.0;
            var x2 = cx + w / 2.0;
            var y2 = cy + h / 2.0;

            // Undo letterbox: subtract pad, divide by scale → original pixel space.
            x1 = (x1 - letterbox.PadX) / letterbox.Scale;
            y1 = (y1 - letterbox.PadY) / letterbox.Scale;
            x2 = (x2 - letterbox.PadX) / letterbox.Scale;
            y2 = (y2 - letterbox.PadY) / letterbox.Scale;

            // Normalize + clamp to [0..1].
            var nx1 = Clamp01(x1 / letterbox.OriginalWidth);
            var ny1 = Clamp01(y1 / letterbox.OriginalHeight);
            var nx2 = Clamp01(x2 / letterbox.OriginalWidth);
            var ny2 = Clamp01(y2 / letterbox.OriginalHeight);
            var nw = Math.Max(0.0, nx2 - nx1);
            var nh = Math.Max(0.0, ny2 - ny1);
            if (nw <= 0 || nh <= 0)
            {
                continue;
            }

            raw.Add(new ParsedBox(nx1, ny1, nw, nh, best));
        }

        return NonMaxSuppression(raw, nmsThreshold);
    }

    private static (int Count, int FieldStride, int CandidateStride, bool ChannelsFirst) ResolveLayout(
        int[] dims, int fields, int dataLength)
    {
        // Accept [F, N] / [N, F] with an optional leading batch dim of 1.
        var shape = dims.Where(d => d != 1).ToArray();
        if (shape.Length == 0)
        {
            shape = dims;
        }
        int d0, d1;
        if (shape.Length == 2)
        {
            d0 = shape[0];
            d1 = shape[1];
        }
        else if (dims.Length >= 2)
        {
            d0 = dims[^2];
            d1 = dims[^1];
        }
        else
        {
            throw new PlateModelOutputException("detector output rank is not 2D-compatible");
        }

        if (d0 == fields && d0 != d1)
        {
            // [F, N] channels-first: field stride = N, candidate index adds 1.
            return (d1, d1, 0, true);
        }
        if (d1 == fields)
        {
            // [N, F] candidates-first: candidate stride = F, field index adds 1.
            return (d0, 0, d1, false);
        }
        if (d0 == fields)
        {
            return (d1, d1, 0, true);
        }
        throw new PlateModelOutputException(
            $"detector output shape does not match expected field count {fields}");
    }

    private static IReadOnlyList<ParsedBox> NonMaxSuppression(List<ParsedBox> boxes, double iouThreshold)
    {
        var ordered = boxes.OrderByDescending(b => b.Score).ToList();
        var kept = new List<ParsedBox>(ordered.Count);
        var suppressed = new bool[ordered.Count];
        for (var i = 0; i < ordered.Count; i++)
        {
            if (suppressed[i])
            {
                continue;
            }
            kept.Add(ordered[i]);
            for (var j = i + 1; j < ordered.Count; j++)
            {
                if (!suppressed[j] && Iou(ordered[i], ordered[j]) > iouThreshold)
                {
                    suppressed[j] = true;
                }
            }
        }
        return kept;
    }

    private static double Iou(ParsedBox a, ParsedBox b)
    {
        var ax2 = a.X + a.Width;
        var ay2 = a.Y + a.Height;
        var bx2 = b.X + b.Width;
        var by2 = b.Y + b.Height;
        var ix1 = Math.Max(a.X, b.X);
        var iy1 = Math.Max(a.Y, b.Y);
        var ix2 = Math.Min(ax2, bx2);
        var iy2 = Math.Min(ay2, by2);
        var iw = Math.Max(0.0, ix2 - ix1);
        var ih = Math.Max(0.0, iy2 - iy1);
        var inter = iw * ih;
        var union = a.Width * a.Height + b.Width * b.Height - inter;
        return union <= 0 ? 0 : inter / union;
    }

    private static double Clamp01(double v) => Math.Clamp(v, 0.0, 1.0);
}

// Thrown when a model's raw output cannot be interpreted under the supported
// contract. Carries only a short sanitized reason (never a tensor dump).
public sealed class PlateModelOutputException : Exception
{
    public PlateModelOutputException(string reason) : base(reason)
    {
    }
}
