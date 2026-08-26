namespace NubArca.Api.Domain.Rag;

/// Membership: this source is part of this domain.
///
/// The row is DATA — which sources were indexed into which domain. It is not
/// policy: whether a domain may be sent to an External model is decided by
/// RagDomainRegistry in code, so no edit to this table can widen a boundary.
///
/// `Priority` and `MetadataJson` are the DOMAIN's opinion about the source.
/// Product Help's editorial priority, feature name and aliases live here rather
/// than on RagSource, because they are claims about how this domain should rank
/// the document — a repository chunk does not acquire an `intent=how-to` merely
/// because the schema can hold one.
public class RagDomainSource
{
    public Guid Id { get; set; }

    /// See RagDomains. Stored as the key so a membership row can be written
    /// without a join, and validated against the code registry on the way in.
    public string DomainKey { get; set; } = string.Empty;

    public Guid SourceId { get; set; }

    /// 1–100, the domain's editorial judgement. Multiplies a lexical score
    /// rather than replacing it: a high-priority source still has to match.
    public int Priority { get; set; } = 50;

    /// Domain-specific classification as JSON. INTERNAL — never serialized to a
    /// DTO.
    public string? MetadataJson { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}
