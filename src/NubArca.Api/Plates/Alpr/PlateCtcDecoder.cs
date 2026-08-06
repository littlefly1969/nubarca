namespace NubArca.Api.Plates.Alpr;

// Pure, dependency-free greedy CTC decoder for a plate-OCR model's raw output.
// NO ONNX types here so it is fully unit-testable on synthetic tensors.
//
// Supported contract (documented in docs/model-deployment/plates.md): a single
// output of shape [1, T, C] or [T, C] where T is the timestep count and
// C = alphabet.Length + 1, with the CTC BLANK class at index 0 and alphabet
// symbol k at index k+1. Values may be logits or probabilities; a softmax is
// applied per timestep so confidence is well-defined. Greedy decode = argmax per
// timestep, collapse consecutive repeats, drop blanks. Confidence is the mean of
// the softmax probability of each EMITTED (kept, non-blank) symbol. An output
// whose shape cannot be matched throws PlateModelOutputException → the caller
// maps it to a safe plate_ocr_output_unsupported error.
public static class PlateCtcDecoder
{
    public sealed record Decoded(string Text, double Confidence);

    public const int BlankIndex = 0;

    public static Decoded Decode(float[] data, int[] dims, string alphabet)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(dims);
        ArgumentException.ThrowIfNullOrEmpty(alphabet);

        var classes = alphabet.Length + 1; // +1 for the CTC blank at index 0
        var (timesteps, classStride) = ResolveShape(dims, classes, data.Length);

        var chars = new List<char>(timesteps);
        var probs = new List<double>(timesteps);
        var prevIndex = -1;

        var row = new double[classes];
        for (var t = 0; t < timesteps; t++)
        {
            // Softmax over the class dim for a comparable confidence.
            var baseOffset = t * classStride;
            var max = double.NegativeInfinity;
            for (var c = 0; c < classes; c++)
            {
                var v = data[baseOffset + c];
                if (v > max)
                {
                    max = v;
                }
            }
            var sum = 0.0;
            for (var c = 0; c < classes; c++)
            {
                var e = Math.Exp(data[baseOffset + c] - max);
                row[c] = e;
                sum += e;
            }

            var bestIndex = 0;
            var bestProb = 0.0;
            for (var c = 0; c < classes; c++)
            {
                var p = row[c] / sum;
                if (p > bestProb)
                {
                    bestProb = p;
                    bestIndex = c;
                }
            }

            // CTC collapse: skip blanks and consecutive duplicates.
            if (bestIndex != BlankIndex && bestIndex != prevIndex)
            {
                var symbol = bestIndex - 1; // index k+1 → alphabet[k]
                if (symbol >= 0 && symbol < alphabet.Length)
                {
                    chars.Add(alphabet[symbol]);
                    probs.Add(bestProb);
                }
            }
            prevIndex = bestIndex;
        }

        var text = new string(chars.ToArray());
        var confidence = probs.Count == 0 ? 0.0 : probs.Average();
        return new Decoded(text, confidence);
    }

    private static (int Timesteps, int ClassStride) ResolveShape(int[] dims, int classes, int dataLength)
    {
        var shape = dims.Where(d => d != 1).ToArray();
        if (shape.Length == 0)
        {
            shape = dims;
        }
        if (shape.Length == 2)
        {
            if (shape[1] == classes)
            {
                return (shape[0], classes); // [T, C]
            }
            if (shape[0] == classes)
            {
                // [C, T] — transpose stride is not contiguous; unsupported here.
                throw new PlateModelOutputException("ocr output is [C, T]; expected [T, C]");
            }
        }
        // Fall back to inferring T from the flat length when the last dim matches.
        if (dims.Length >= 1 && dims[^1] == classes && dataLength % classes == 0)
        {
            return (dataLength / classes, classes);
        }
        throw new PlateModelOutputException(
            $"ocr output shape does not match expected class count {classes}");
    }
}
