using NubArca.Api.Domain;

namespace NubArca.Api.Plates.Redaction;

// Owner-private, PRIVACY-ONLY face redaction metadata service. Detects and
// persists face regions for a PlateImage so redaction is not recomputed every
// request. This is NOT identity: it creates NO FaceDetection/FaceEmbedding/
// FaceCluster/Person/PersonFaceAssignment rows, uses no People embeddings, and
// produces no cross-owner data. Boxes are never exposed through any DTO/API.
public interface IPlateFaceRedactionService
{
    // True when server-side privacy redaction can be produced (feature enabled +
    // a runnable detector). False → callers must return a safe "not configured"
    // error and NEVER serve the unredacted image.
    bool IsAvailable { get; }

    // The current redaction model profile key (config label). Boxes/cache keyed
    // by this; changing it invalidates cached media and re-detects boxes.
    string ProfileKey { get; }

    // Safe owner-only summary for the detail DTO. Never runs the detector — it
    // reports availability + the count of boxes ALREADY persisted for the
    // current profile (0 until the owner first requests a redacted rendition).
    Task<PlateRedactionInfo> GetInfoAsync(
        Guid ownerUserId, Guid plateImageId, CancellationToken cancellationToken = default);

    // Ensures redaction boxes exist for the current profile, running the
    // detector (on the lazily-loaded source) only when they are missing.
    // Returns the boxes plus whether they were just (re)generated (so the caller
    // can invalidate any stale cached media). Throws
    // PlateFaceRedactionUnavailableException when redaction is unavailable.
    Task<PlateFaceRedactionEnsureResult> EnsureBoxesAsync(
        Guid ownerUserId,
        Guid plateImageId,
        Func<CancellationToken, Task<PlateRedactionImageInput?>> sourceFactory,
        CancellationToken cancellationToken = default);
}

public sealed record PlateFaceRedactionEnsureResult(
    IReadOnlyList<PlateFaceRedactionBox> Boxes,
    bool Regenerated);
