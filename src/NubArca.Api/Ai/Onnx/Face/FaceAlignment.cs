using System.Numerics;

namespace NubArca.Api.Ai.Onnx.Face;

// Pure 5-point face alignment geometry, separated from ImageSharp/ONNX so the
// least-squares similarity estimate is unit-testable. ArcFace recognition expects
// a face warped to a canonical 112×112 layout using the standard InsightFace
// reference landmarks (left eye, right eye, nose, left mouth, right mouth).
//
// We estimate a SIMILARITY transform (uniform scale + rotation + translation,
// 4 DOF) from the detected landmarks to the reference via ordinary least squares
// over the 5 point pairs — equivalent to Umeyama for the similarity case and
// numerically simple. The result is a System.Numerics.Matrix3x2 mapping SOURCE
// (oriented-image pixel) coordinates → DESTINATION (aligned-crop) coordinates,
// suitable for a forward ImageSharp affine transform.
public static class FaceAlignment
{
    public const int ArcFaceReferenceSize = 112;

    // InsightFace ArcFace 112×112 reference landmarks.
    private static readonly float[] Reference =
    {
        38.2946f, 51.6963f,
        73.5318f, 51.5014f,
        56.0252f, 71.7366f,
        41.5493f, 92.3655f,
        70.7299f, 92.2041f,
    };

    // Estimate the similarity transform mapping the 5 detected landmarks
    // (srcLandmarks: x0,y0,x1,y1,… in oriented-image pixels) onto the reference
    // layout scaled to outSize. Returns false if the system is degenerate.
    public static bool TryEstimateSimilarity(
        IReadOnlyList<float> srcLandmarks, int outSize, out Matrix3x2 matrix)
    {
        matrix = Matrix3x2.Identity;
        if (srcLandmarks.Count < 10 || outSize <= 0)
        {
            return false;
        }

        var refScale = outSize / (float)ArcFaceReferenceSize;

        // Unknowns u = [a, b, tx, ty] with the similarity model
        //   x' = a*x - b*y + tx
        //   y' = b*x + a*y + ty
        // Each point contributes two rows; solve the 4×4 normal equations
        // (AtA) u = (Atb).
        var ata = new double[4, 4];
        var atb = new double[4];

        for (var i = 0; i < 5; i++)
        {
            double x = srcLandmarks[i * 2];
            double y = srcLandmarks[i * 2 + 1];
            double xr = Reference[i * 2] * refScale;
            double yr = Reference[i * 2 + 1] * refScale;

            // Row for x': coefficients on [a, b, tx, ty] = [x, -y, 1, 0], target xr.
            AccumulateRow(ata, atb, x, -y, 1, 0, xr);
            // Row for y': coefficients [y, x, 0, 1], target yr.
            AccumulateRow(ata, atb, y, x, 0, 1, yr);
        }

        if (!SolveSymmetric4(ata, atb, out var sol))
        {
            return false;
        }

        var a = (float)sol[0];
        var b = (float)sol[1];
        var tx = (float)sol[2];
        var ty = (float)sol[3];

        // System.Numerics point transform: x' = x*M11 + y*M21 + M31, etc.
        matrix = new Matrix3x2(a, b, -b, a, tx, ty);
        return true;
    }

    private static void AccumulateRow(
        double[,] ata, double[] atb, double c0, double c1, double c2, double c3, double target)
    {
        Span<double> row = stackalloc double[] { c0, c1, c2, c3 };
        for (var r = 0; r < 4; r++)
        {
            for (var c = 0; c < 4; c++)
            {
                ata[r, c] += row[r] * row[c];
            }

            atb[r] += row[r] * target;
        }
    }

    // Gaussian elimination with partial pivoting for a 4×4 system.
    private static bool SolveSymmetric4(double[,] a, double[] b, out double[] x)
    {
        x = new double[4];
        var m = (double[,])a.Clone();
        var v = (double[])b.Clone();

        for (var col = 0; col < 4; col++)
        {
            var pivot = col;
            var best = Math.Abs(m[col, col]);
            for (var r = col + 1; r < 4; r++)
            {
                var cand = Math.Abs(m[r, col]);
                if (cand > best)
                {
                    best = cand;
                    pivot = r;
                }
            }

            if (best < 1e-9)
            {
                return false;
            }

            if (pivot != col)
            {
                for (var c = 0; c < 4; c++)
                {
                    (m[col, c], m[pivot, c]) = (m[pivot, c], m[col, c]);
                }

                (v[col], v[pivot]) = (v[pivot], v[col]);
            }

            for (var r = 0; r < 4; r++)
            {
                if (r == col)
                {
                    continue;
                }

                var factor = m[r, col] / m[col, col];
                for (var c = col; c < 4; c++)
                {
                    m[r, c] -= factor * m[col, c];
                }

                v[r] -= factor * v[col];
            }
        }

        for (var i = 0; i < 4; i++)
        {
            x[i] = v[i] / m[i, i];
        }

        return true;
    }
}
