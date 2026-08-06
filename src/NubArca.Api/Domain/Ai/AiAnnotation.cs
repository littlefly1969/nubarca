namespace NubArca.Api.Domain.Ai;

// Owner/file-scoped, model/profile-versioned AI annotation. One open-ended
// table for tags, captions, descriptions and future annotation kinds (Kind
// discriminates). `Label` holds a tag value; `Text` holds a caption/description.
// Phase 0A defines the shape only; nothing produces annotations yet.
public class AiAnnotation
{
    public Guid Id { get; set; }

    public Guid FileItemId { get; set; }

    // Explicit owner scope, denormalized for owner-scoped queries/isolation.
    public Guid OwnerUserId { get; set; }

    public Guid ProfileId { get; set; }

    // One of AiAnnotationKinds (open-ended).
    public string Kind { get; set; } = string.Empty;

    // Tag label (for Kind = tag). Nullable otherwise.
    public string? Label { get; set; }

    // Caption/description text (for Kind = caption/description). INTERNAL until
    // an owner-private DTO is designed in a later phase. Nullable otherwise.
    public string? Text { get; set; }

    public double? Confidence { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}
