namespace NubArca.Api.Domain.Ai;

// Unified versioned profile. EVERY AI output (embeddings, extracted text,
// chunks, faces, person groups, annotations, per-blob status) is keyed by a
// ProfileId, so changing a model/dimension = create a new profile and reindex
// under it while old outputs keep their own profile. Subsumes both embedding
// and extraction profiles (Dimension/DistanceMetric are simply null for
// extraction capabilities).
public class AiProfile
{
    public Guid Id { get; set; }

    // Stable, unique key (e.g. "photo-visual-v1", "doc-text-v1").
    public string Key { get; set; } = string.Empty;

    // The model this profile binds to.
    public Guid AiModelId { get; set; }

    // Capability + modality this profile produces (see AiCapabilities / AiModalities).
    public string Capability { get; set; } = string.Empty;
    public string Modality { get; set; } = string.Empty;

    // Embedding dimension + metric, set only for embedding profiles.
    public int? Dimension { get; set; }
    public string? DistanceMetric { get; set; }

    // At most one default profile per capability (enforced by a partial unique
    // index). The default is the active profile the system reads/writes for
    // that capability.
    public bool IsDefault { get; set; }

    // Whether this profile may be used at all.
    public bool Enabled { get; set; }

    // Opaque hash of the profile's config/preprocessing version, so a config
    // change can be detected and reindexed. Nullable.
    public string? ConfigHash { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}
