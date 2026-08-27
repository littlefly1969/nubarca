namespace NubArca.Api.Domain.Rag;

/// Membership: this domain uses this source content, at this revision.
///
/// The row is DATA — which sources were indexed into which domain, and which
/// snapshot each domain believes it is describing. It is not policy: whether a
/// domain may be sent to an External model is decided by RagDomainRegistry in
/// code, so no edit to this table can widen a boundary.
///
/// `Revision` lives HERE rather than on RagSource, and that is the whole
/// release lifecycle. Content identity is what the bytes are; membership is
/// which snapshot a domain is using them at. Two domains sharing an unchanged
/// file can therefore be at two different revisions during a sequential upgrade
/// without either of them rewriting the other's chunks — which is the deadlock
/// the previous model could not get out of.
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

    /// The snapshot THIS DOMAIN is using this source at. A domain that cannot
    /// say which revision it describes cannot be checked against the running
    /// build; a domain holding memberships from two revisions is an interrupted
    /// reindex and is refused until it converges.
    public string Revision { get; set; } = string.Empty;

    /// 1–100, the domain's editorial judgement. Multiplies a lexical score
    /// rather than replacing it: a high-priority source still has to match.
    public int Priority { get; set; } = 50;

    /// Domain-specific classification as JSON. INTERNAL — never serialized to a
    /// DTO.
    public string? MetadataJson { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}
