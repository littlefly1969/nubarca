namespace NubArca.Api.Domain.Ai;

// Owner/profile-scoped person/cluster group. Clustering is ALWAYS scoped to a
// single owner AND a single face-embedding profile (model space) — there is no
// cross-owner grouping and no global person graph. A face-embedding v1->v2
// reindex builds v2 groups alongside intact v1 groups (different ProfileId).
public class PersonGroup
{
    public Guid Id { get; set; }

    public Guid OwnerUserId { get; set; }

    // The face-embedding profile (model space) this grouping belongs to.
    public Guid ProfileId { get; set; }

    // Owner-assigned display name (e.g. "Alice"). Nullable until named.
    public string? DisplayName { get; set; }

    // Opaque cluster label/key from the clustering run. Nullable.
    public string? ClusterKey { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}
