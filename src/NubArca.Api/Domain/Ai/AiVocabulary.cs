namespace NubArca.Api.Domain.Ai;

// Provider keys. "none" disables a capability; the rest are reserved for later
// phases (deterministic test backend, local ONNX, external hosted API).
public static class AiProviders
{
    public const string None = "none";
    public const string Deterministic = "deterministic";
    public const string Onnx = "onnx";
    public const string External = "external";
}

// Capability identifiers. Stored as short strings on AiModel/AiProfile/
// BlobAiArtifactStatus/AiIndexDiagnostic. One per logical AI task.
public static class AiCapabilities
{
    public const string ImageEmbedding = "image-embedding";
    public const string DocumentExtraction = "document-extraction";
    public const string DocumentEmbedding = "document-embedding";
    public const string FaceDetection = "face-detection";
    public const string FaceEmbedding = "face-embedding";
    public const string FaceClustering = "face-clustering";
    public const string Tagging = "tagging";
    public const string Captioning = "captioning";
}

// Coarse input modality a model/profile operates on.
public static class AiModalities
{
    public const string Image = "image";
    public const string Text = "text";
    public const string Face = "face";
    public const string Document = "document";
    public const string Multimodal = "multimodal";
}

// Vector distance metric for embedding profiles. Stored nullable (only set for
// embedding capabilities).
public static class AiDistanceMetrics
{
    public const string Cosine = "cosine";
    public const string L2 = "l2";
}

// Terminal per-target outcomes recorded in BlobAiArtifactStatus.
//
// IMPORTANT — there is deliberately NO "pending" value: a MISSING
// (BlobObjectId, ProfileId, Capability) row IS implicit pending. Rows are
// written only when a target reaches a terminal state, so creating a profile
// never materialises millions of pending rows.
//
//  - Completed: artifact produced for the profile.
//  - Skipped:   the CONTENT is permanently/intentionally not processable for
//               the profile (unsupported format, corrupt media, no extractable
//               text, capability not applicable). Never used for an unavailable
//               provider/environment condition.
//  - Failed:    a real processing error on a processable target (retryable).
public static class AiArtifactStatuses
{
    public const string Completed = "completed";
    public const string Skipped = "skipped";
    public const string Failed = "failed";
}

// What an AiIndexDiagnostic row is about. Lets one generic table cover blob,
// document-chunk, face-detection, owner-scoped clustering, annotation, and
// provider-availability diagnostics.
public static class AiDiagnosticTargetKinds
{
    public const string Blob = "blob";
    public const string DocumentChunk = "document-chunk";
    public const string FaceDetection = "face-detection";
    public const string Clustering = "clustering";
    public const string Annotation = "annotation";
    public const string Provider = "provider";
}

// Kinds of AiAnnotation. Open-ended; new kinds can be added without schema
// change.
public static class AiAnnotationKinds
{
    public const string Tag = "tag";
    public const string Caption = "caption";
    public const string Description = "description";
}
