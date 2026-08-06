namespace NubArca.Api.Ai.Onnx;

// Pure post-processing of a raw ONNX output into a validated, L2-normalized
// embedding. Kept separate from the runtime backend so it is unit-testable
// without ONNX weights: dimension validation, NaN/Infinity rejection, and
// cosine-friendly normalization.
internal static class OnnxImageEmbeddings
{
    public static float[] Finalize(ReadOnlySpan<float> raw, int expectedDimension)
    {
        if (expectedDimension <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedDimension));
        }

        if (raw.Length != expectedDimension)
        {
            // A mismatch means the model/output-tensor/config disagree (e.g. an
            // unpooled [seq, dim] output). The harness surfaces this as a failure
            // so the operator can fix the export/config — never a silent reshape.
            throw new ArgumentException(
                $"ONNX output has {raw.Length} values but the profile expects {expectedDimension}.", nameof(raw));
        }

        double sumSquares = 0;
        var vector = new float[raw.Length];
        for (var i = 0; i < raw.Length; i++)
        {
            var value = raw[i];
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                throw new ArgumentException($"ONNX output contains a non-finite value at index {i}.", nameof(raw));
            }

            vector[i] = value;
            sumSquares += (double)value * value;
        }

        var norm = Math.Sqrt(sumSquares);
        if (norm > double.Epsilon)
        {
            for (var i = 0; i < vector.Length; i++)
            {
                vector[i] = (float)(vector[i] / norm);
            }
        }
        // A zero vector (norm 0) is returned as-is; it cannot be normalized.

        return vector;
    }
}
