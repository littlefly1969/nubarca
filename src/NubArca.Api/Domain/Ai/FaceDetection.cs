namespace NubArca.Api.Domain.Ai;

// Face Substrate v0: a single detected face inside an image, keyed at the
// BLOB level (BlobObjectId, ProfileId, FaceIndex) — exactly like BlobEmbedding.
//
// BLOB-LEVEL by design: detection geometry + landmarks are deterministic on the
// immutable blob bytes, so they are shared by every FileItem that references the
// blob (no per-owner re-detection). This is a TECHNICAL artifact only. Owner /
// Private-Vault boundaries are ALWAYS enforced when surfacing faces to a user
// (candidate/search/coverage queries join FileItems under the global vault
// filter and scope by OwnerUserId) and by the owner-level clustering layer
// (PersonGroup / FaceAssignment). There is no cross-owner identity here.
//
// A missing detection for a (blob, profile) is implicit-pending; detection
// completion (including the legitimate zero-face case) is recorded once in
// BlobAiArtifactStatus (capability face-detection). Raw face vectors are never
// stored here — see FaceEmbedding.
public class FaceDetection
{
    public Guid Id { get; set; }

    // Physical, content-addressed source blob (immutable). Blob-level, so the
    // detection is reused across every owner/FileItem that references the blob.
    public Guid BlobObjectId { get; set; }

    // The face package profile (detector + recognition model space) that produced
    // this face. Outputs are always keyed by ProfileId (no profile mixing).
    public Guid ProfileId { get; set; }

    // Optional stable key of the detector sub-model (forward-compat: in this
    // codebase one AiProfile encapsulates both detector and recognizer, so this
    // mirrors the profile key). Never a path/secret.
    public string? DetectorProfileKey { get; set; }

    // Stable 0-based index of this face within the (blob, profile) detection set,
    // assigned in a deterministic detection order. Makes re-detection idempotent
    // via the unique (BlobObjectId, ProfileId, FaceIndex) index.
    public int FaceIndex { get; set; }

    // Normalized bounding box (fractions of image width/height, [0..1]). No
    // absolute pixel geometry / storage identity is implied.
    public double BoundingBoxX { get; set; }
    public double BoundingBoxY { get; set; }
    public double BoundingBoxWidth { get; set; }
    public double BoundingBoxHeight { get; set; }

    // Detector confidence for this face ([0..1]).
    public double? DetectionScore { get; set; }

    // Optional derived quality signal (e.g. size/blur/pose heuristic). Nullable
    // until a quality model is wired.
    public double? FaceQualityScore { get; set; }

    // Normalized 5-point landmarks as JSON ([{ "x": .., "y": .. }, …], fractions
    // of image width/height). Used for recognition alignment. Null when the
    // detector produced no keypoints. Never contains storage identity.
    public string? LandmarksJson { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}
