namespace NubArca.Api.Ai.Onnx.Face;

// Pure decoder for SCRFD / RetinaFace-style anchor-free face detectors, kept
// separate from the ONNX runtime so the anchor decode + NMS math is unit-testable
// without model weights. Mirrors InsightFace's scrfd.py reference decode:
//   - three FPN strides (8, 16, 32), each with a score, a bbox-distance, and
//     (for keypoint models) a 5-point landmark branch;
//   - anchor centers on a stride grid, repeated per anchor;
//   - distance2bbox / distance2kps decode into detector-INPUT pixel coordinates;
//   - greedy NMS by IoU.
//
// Robustness: branches are classified by their channel count (1 = score,
// 4 = bbox, 10 = 5-point kps) and grouped to strides by descending row count, so
// the decoder does not depend on the (numeric, export-dependent) ONNX output
// NAMES or their ordering — only on the well-defined SCRFD output shapes, which
// it verifies and reports a diagnostic for when they differ.
public static class ScrfdDecoder
{
    // A raw ONNX output: flat row-major data + its tensor shape.
    public readonly record struct RawOutput(float[] Data, IReadOnlyList<int> Shape);

    // A decoded face in detector-INPUT pixel space (before the letterbox is
    // undone). Landmarks is length 2*LandmarkCount (x0,y0,x1,y1,…) or null.
    public sealed record ScrfdFace(float X1, float Y1, float X2, float Y2, float Score, float[]? Landmarks);

    // Canonical SCRFD FPN strides.
    private static readonly int[] FeatStrideFpn = { 8, 16, 32 };

    public static IReadOnlyList<ScrfdFace> Decode(
        IReadOnlyList<RawOutput> outputs,
        int inputWidth,
        int inputHeight,
        float scoreThreshold,
        float nmsThreshold,
        out string? diagnostic)
    {
        diagnostic = null;

        // Classify each output by its per-anchor channel count.
        var scoreOuts = new List<(int N, float[] Data)>();
        var bboxOuts = new List<(int N, float[] Data)>();
        var kpsOuts = new List<(int N, float[] Data)>();
        foreach (var o in outputs)
        {
            if (o.Shape.Count == 0 || o.Data.Length == 0)
            {
                continue;
            }

            var channel = o.Shape[^1];
            if (channel <= 0)
            {
                continue;
            }

            var n = o.Data.Length / channel;
            switch (channel)
            {
                case 1: scoreOuts.Add((n, o.Data)); break;
                case 4: bboxOuts.Add((n, o.Data)); break;
                case 10: kpsOuts.Add((n, o.Data)); break;
                default: break; // ignore unexpected branch
            }
        }

        var fmc = scoreOuts.Count;
        if (fmc == 0 || bboxOuts.Count != fmc || fmc > FeatStrideFpn.Length)
        {
            diagnostic = "detector-output-shape-unexpected";
            return Array.Empty<ScrfdFace>();
        }

        var useKps = kpsOuts.Count == fmc;
        if (kpsOuts.Count != 0 && kpsOuts.Count != fmc)
        {
            // Partial kps branches → cannot rely on landmarks; decode without them.
            diagnostic = "detector-landmark-branch-mismatch";
            useKps = false;
        }

        // Group score/bbox/kps by stride: largest row count == smallest stride.
        scoreOuts.Sort((a, b) => b.N.CompareTo(a.N));
        bboxOuts.Sort((a, b) => b.N.CompareTo(a.N));
        if (useKps)
        {
            kpsOuts.Sort((a, b) => b.N.CompareTo(a.N));
        }

        var faces = new List<ScrfdFace>();
        for (var i = 0; i < fmc; i++)
        {
            var stride = FeatStrideFpn[i];
            var scores = scoreOuts[i].Data;
            var bbox = bboxOuts[i].Data;
            var kps = useKps ? kpsOuts[i].Data : null;

            var gridW = inputWidth / stride;
            var gridH = inputHeight / stride;
            var cells = gridW * gridH;
            if (cells <= 0)
            {
                continue;
            }

            var n = scoreOuts[i].N;
            var numAnchors = Math.Max(1, n / cells);

            for (var r = 0; r < gridH; r++)
            {
                for (var c = 0; c < gridW; c++)
                {
                    for (var a = 0; a < numAnchors; a++)
                    {
                        var j = ((r * gridW) + c) * numAnchors + a;
                        if (j >= n || j >= scores.Length)
                        {
                            continue;
                        }

                        var score = scores[j];
                        if (score < scoreThreshold)
                        {
                            continue;
                        }

                        float cx = c * stride;
                        float cy = r * stride;

                        var l = bbox[j * 4 + 0] * stride;
                        var t = bbox[j * 4 + 1] * stride;
                        var rr = bbox[j * 4 + 2] * stride;
                        var bb = bbox[j * 4 + 3] * stride;

                        float[]? landmarks = null;
                        if (kps is not null)
                        {
                            landmarks = new float[10];
                            for (var k = 0; k < 5; k++)
                            {
                                landmarks[k * 2] = cx + kps[j * 10 + k * 2] * stride;
                                landmarks[k * 2 + 1] = cy + kps[j * 10 + k * 2 + 1] * stride;
                            }
                        }

                        faces.Add(new ScrfdFace(cx - l, cy - t, cx + rr, cy + bb, score, landmarks));
                    }
                }
            }
        }

        return NonMaxSuppression(faces, nmsThreshold);
    }

    // Greedy IoU non-maximum suppression, highest score first.
    private static List<ScrfdFace> NonMaxSuppression(List<ScrfdFace> faces, float nmsThreshold)
    {
        faces.Sort((a, b) => b.Score.CompareTo(a.Score));
        var kept = new List<ScrfdFace>();
        var suppressed = new bool[faces.Count];

        for (var i = 0; i < faces.Count; i++)
        {
            if (suppressed[i])
            {
                continue;
            }

            var a = faces[i];
            kept.Add(a);
            for (var k = i + 1; k < faces.Count; k++)
            {
                if (suppressed[k])
                {
                    continue;
                }

                if (Iou(a, faces[k]) > nmsThreshold)
                {
                    suppressed[k] = true;
                }
            }
        }

        return kept;
    }

    private static float Iou(ScrfdFace a, ScrfdFace b)
    {
        var ix1 = Math.Max(a.X1, b.X1);
        var iy1 = Math.Max(a.Y1, b.Y1);
        var ix2 = Math.Min(a.X2, b.X2);
        var iy2 = Math.Min(a.Y2, b.Y2);
        var iw = Math.Max(0f, ix2 - ix1);
        var ih = Math.Max(0f, iy2 - iy1);
        var inter = iw * ih;
        var areaA = Math.Max(0f, a.X2 - a.X1) * Math.Max(0f, a.Y2 - a.Y1);
        var areaB = Math.Max(0f, b.X2 - b.X1) * Math.Max(0f, b.Y2 - b.Y1);
        var union = areaA + areaB - inter;
        return union <= 0f ? 0f : inter / union;
    }
}
