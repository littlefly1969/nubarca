using System.Buffers.Binary;

namespace NubArca.Api.Ai;

// Stateless float32-LE vector serializer. Registered as a singleton.
public sealed class AiVectorSerializer : IAiVectorSerializer
{
    private const int BytesPerFloat = sizeof(float); // 4

    public byte[] Serialize(ReadOnlySpan<float> vector)
    {
        if (vector.IsEmpty)
        {
            throw new ArgumentException("Embedding vector must not be empty.", nameof(vector));
        }

        var bytes = new byte[vector.Length * BytesPerFloat];
        for (int i = 0; i < vector.Length; i++)
        {
            var value = vector[i];
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                throw new ArgumentException(
                    $"Embedding vector contains a non-finite value at index {i}.", nameof(vector));
            }

            BinaryPrimitives.WriteSingleLittleEndian(
                bytes.AsSpan(i * BytesPerFloat, BytesPerFloat), value);
        }

        return bytes;
    }

    public byte[] Serialize(ReadOnlySpan<float> vector, int expectedDimension)
    {
        if (vector.Length != expectedDimension)
        {
            throw new ArgumentException(
                $"Embedding vector has {vector.Length} components but the profile expects {expectedDimension}.",
                nameof(vector));
        }

        return Serialize(vector);
    }

    public float[] Deserialize(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length == 0 || bytes.Length % BytesPerFloat != 0)
        {
            throw new ArgumentException(
                $"Embedding byte length {bytes.Length} is not a positive multiple of {BytesPerFloat}.",
                nameof(bytes));
        }

        var vector = new float[bytes.Length / BytesPerFloat];
        for (int i = 0; i < vector.Length; i++)
        {
            vector[i] = BinaryPrimitives.ReadSingleLittleEndian(
                bytes.AsSpan(i * BytesPerFloat, BytesPerFloat));
        }

        return vector;
    }

    public float[] Deserialize(byte[] bytes, int expectedDimension)
    {
        var vector = Deserialize(bytes);
        if (vector.Length != expectedDimension)
        {
            throw new ArgumentException(
                $"Decoded vector has {vector.Length} components but {expectedDimension} were expected.",
                nameof(bytes));
        }

        return vector;
    }

    public int GetDimension(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length % BytesPerFloat != 0)
        {
            throw new ArgumentException(
                $"Embedding byte length {bytes.Length} is not a multiple of {BytesPerFloat}.",
                nameof(bytes));
        }

        return bytes.Length / BytesPerFloat;
    }

    public float[] Normalize(ReadOnlySpan<float> vector)
    {
        if (vector.IsEmpty)
        {
            throw new ArgumentException("Embedding vector must not be empty.", nameof(vector));
        }

        double sumSquares = 0;
        var copy = new float[vector.Length];
        for (int i = 0; i < vector.Length; i++)
        {
            var value = vector[i];
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                throw new ArgumentException(
                    $"Embedding vector contains a non-finite value at index {i}.", nameof(vector));
            }

            copy[i] = value;
            sumSquares += (double)value * value;
        }

        var norm = Math.Sqrt(sumSquares);
        if (norm <= double.Epsilon)
        {
            // A zero vector cannot be normalized; return it unchanged.
            return copy;
        }

        for (int i = 0; i < copy.Length; i++)
        {
            copy[i] = (float)(copy[i] / norm);
        }

        return copy;
    }
}
