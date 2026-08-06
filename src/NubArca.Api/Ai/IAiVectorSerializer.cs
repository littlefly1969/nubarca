namespace NubArca.Api.Ai;

// Deterministic encoder/decoder between float vectors and the Phase 0A
// `byte[] EmbeddingBytes` columns. Stable float32 little-endian packing so the
// same vector always yields the same bytes (and the same bytes decode back).
//
// This is an INTERNAL utility: raw vectors/bytes are never exposed through any
// API/CLI DTO.
public interface IAiVectorSerializer
{
    // Pack a vector to bytes. Rejects empty vectors and NaN/Infinity values.
    byte[] Serialize(ReadOnlySpan<float> vector);

    // Pack a vector, additionally asserting it has exactly `expectedDimension`
    // components. Throws on mismatch.
    byte[] Serialize(ReadOnlySpan<float> vector, int expectedDimension);

    // Unpack bytes to a vector. Throws if the byte length is not a multiple of 4.
    float[] Deserialize(byte[] bytes);

    // Unpack and assert the decoded vector has exactly `expectedDimension`
    // components. Throws on mismatch.
    float[] Deserialize(byte[] bytes, int expectedDimension);

    // Number of float components encoded in `bytes`.
    int GetDimension(byte[] bytes);

    // Return an L2-normalized copy (cosine-friendly). A zero vector is returned
    // unchanged (cannot be normalized). Rejects NaN/Infinity.
    float[] Normalize(ReadOnlySpan<float> vector);
}
