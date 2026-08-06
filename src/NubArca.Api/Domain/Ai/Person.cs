namespace NubArca.Api.Domain.Ai;

// Owner-level named person. People are ALWAYS scoped to a single owner — there is
// no global person identity and no cross-owner sharing. A person is created when
// the owner names a suggested face cluster (or manually). Names live here; the
// algorithmic grouping lives in FaceCluster.
public class Person
{
    public Guid Id { get; set; }

    public Guid OwnerUserId { get; set; }

    // Owner-assigned display name. Nullable only transiently; the People UI names
    // on creation. Rename collisions are resolved in the service (suffix), not by
    // a hard unique constraint.
    public string? DisplayName { get; set; }

    // Soft hide from the People list (never a hard delete of derived data).
    public bool IsArchived { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}
