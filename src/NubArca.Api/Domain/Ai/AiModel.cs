namespace NubArca.Api.Domain.Ai;

// Registry of an AI model/provider/version. Identity is the stable Key (e.g.
// "deterministic-v1", "clip-vit-b32-onnx"); a unique index prevents duplicates.
// Phase 0A only ever stores rows via an explicit seed/admin path — there is no
// inference yet.
public class AiModel
{
    public Guid Id { get; set; }

    // Stable, human-meaningful unique identity for the model.
    public string Key { get; set; } = string.Empty;

    // Provider key (see AiProviders). Determines which backend serves it later.
    public string Provider { get; set; } = AiProviders.None;

    // Primary capability this model serves (see AiCapabilities).
    public string Capability { get; set; } = string.Empty;

    // Input modality (see AiModalities).
    public string Modality { get; set; } = string.Empty;

    public int Version { get; set; } = 1;

    // Embedding dimension + metric, set only for embedding models.
    public int? Dimension { get; set; }
    public string? DistanceMetric { get; set; }

    public bool Enabled { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}
