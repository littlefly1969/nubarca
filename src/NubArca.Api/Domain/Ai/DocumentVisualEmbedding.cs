namespace NubArca.Api.Domain.Ai;

/// The vector(s) for one visual unit under one profile.
///
/// ONE ROW COVERS BOTH LAYOUTS, and that is deliberate. A dense profile stores a
/// single 1152-component vector; a late-interaction profile stores a SEQUENCE of
/// per-patch vectors scored by MaxSim. Two tables would duplicate the owner
/// join, the profile match and the eligibility rules that matter, to separate
/// two things that differ only in how many vectors are in the blob — so the
/// shape is declared per row instead, and every reader validates the declaration
/// against the bytes rather than trusting it.
///
/// `Layout` is closed (see DocumentVisualEmbeddingLayouts) precisely because it
/// decides how `EmbeddingBytes` is decoded. An unknown value is refused, never
/// guessed at: a multi-vector blob read as a dense one is not an error, it is a
/// wrong number.
public class DocumentVisualEmbedding
{
    public Guid Id { get; set; }

    public Guid DocumentVisualUnitId { get; set; }

    public Guid ProfileId { get; set; }

    /// `dense` | `late-interaction`.
    public string Layout { get; set; } = string.Empty;

    /// Components per vector.
    public int Dimension { get; set; }

    /// How many vectors the blob holds. Exactly 1 for `dense`; the model's token
    /// count for `late-interaction`. Stored rather than inferred from the byte
    /// length so a malformed row is DETECTABLE: length must equal
    /// VectorCount × Dimension × 4, and a row where it does not is skipped as
    /// corrupt instead of being reshaped into something plausible.
    public int VectorCount { get; set; }

    /// Canonical float32 little-endian, vectors laid out end to end — the same
    /// encoding every other NubArca embedding uses, through the same serializer.
    ///
    /// Float16 was considered for the multi-vector case and is not used: it
    /// halves the bytes and changes the MaxSim scores, and this slice has no
    /// measurement saying by how little. Section 25 of the specification asks
    /// for the measurement first, so the storage stays exact and the decision
    /// stays open.
    public byte[] EmbeddingBytes { get; set; } = Array.Empty<byte>();

    public DateTime CreatedAt { get; set; }
}
