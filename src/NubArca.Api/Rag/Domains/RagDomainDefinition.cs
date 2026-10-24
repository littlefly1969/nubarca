namespace NubArca.Api.Rag.Domains;

/// Who a domain's knowledge belongs to.
///
/// `System` knowledge would be the same for every user of an installation;
/// no domain is System today. `Owner` knowledge belongs to one person and can only ever be retrieved on their behalf. The
/// distinction is a SCHEMA fact rather than a filter: an owner-scoped domain
/// requires an owner id to retrieve at all, so "forgot the WHERE clause" is not
/// a way to read someone else's documents.
public enum RagDomainScope
{
    /// Installation-wide knowledge, identical for every caller.
    System,

    /// Owner-private knowledge: `user-documents`.
    Owner,
}

/// How far a domain's evidence is allowed to travel.
///
/// This is stated per domain rather than derived, because every derivation
/// available is wrong in the direction that leaks. A file's path does not know
/// whether it is public. And a document being public in fact is not the same as a domain being approved to
/// leave the trust boundary.
public enum RagPrivacyClass
{
    /// Published product material. Safe to send to a model outside the trust
    /// boundary, because it is already outside it.
    Public,

    /// Knowledge about this installation's own system. Not secret, not
    /// published, and not something an External model gets to read.
    SystemInternal,

    /// One person's own content.
    OwnerPrivate,
}

/// One domain, and the complete policy that governs it.
///
/// A definition is a CODE constant, not a database row — see RagDomainRegistry
/// for why. Every field here is a statement someone had to write down; nothing
/// is inferred at runtime from a path, a URL or a hostname.
public sealed record RagDomainDefinition(
    string Key,
    RagDomainScope Scope,
    RagPrivacyClass PrivacyClass,
    bool RequiresOwner,
    bool ExternalGenerationAllowed)
{
    /// The domain key as the retrieval contract's typed key.
    public RagDomainKey DomainKey { get; } = new(Key);
}
