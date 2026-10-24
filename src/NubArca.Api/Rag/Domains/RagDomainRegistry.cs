namespace NubArca.Api.Rag.Domains;

/// The domain keys NubArca knows, as constants rather than as strings spelled
/// at every call site.
public static class RagDomains
{
    /// One person's own library documents. Retrievable only on behalf of the
    /// owner, and never grounded on by a model outside the trust boundary.
    public const string UserDocuments = "user-documents";
}

/// Resolves a domain key to its policy.
///
/// `GetRequired` throws for an unknown key and `TryGet` answers false: there is
/// no "default domain" and no permissive fallback, because a typo that resolved
/// to something would resolve to a domain the caller was not thinking about.
public interface IRagDomainRegistry
{
    RagDomainDefinition GetRequired(string domainKey);

    bool TryGet(string domainKey, out RagDomainDefinition domain);

    IReadOnlyList<RagDomainDefinition> List();
}

/// THE AUTHORITY on what a domain is allowed to do.
///
/// It is a compiled table with no configuration surface and no database read,
/// and that is the entire point. PRIVACY is not data: if a domain's privacy
/// class were a column, then one UPDATE, one careless admin endpoint or one
/// restored backup could widen it. There is no statement to update here, only a
/// commit to review.
public sealed class RagDomainRegistry : IRagDomainRegistry
{
    /// One person's own documents. Every field is the restrictive value, and
    /// none of them is reachable from configuration. `RequiresOwner` is the one that matters most: retrieval
    /// refuses a query with no owner before it reads anything, so "forgot the
    /// WHERE clause" cannot become "read somebody else's documents" — the
    /// request is rejected rather than answered broadly.
    ///
    /// `ExternalGenerationAllowed: false` is not a default that happens to be
    /// off. A person's own documents may not be sent outside the trust boundary
    /// to be summarized, and there is no configuration key, database column or
    /// client parameter that says otherwise.
    public static RagDomainDefinition UserDocuments { get; } = new(
        Key: RagDomains.UserDocuments,
        Scope: RagDomainScope.Owner,
        PrivacyClass: RagPrivacyClass.OwnerPrivate,
        RequiresOwner: true,
        ExternalGenerationAllowed: false);

    private static readonly IReadOnlyList<RagDomainDefinition> All =
        new[] { UserDocuments };

    private static readonly IReadOnlyDictionary<string, RagDomainDefinition> ByKey =
        All.ToDictionary(d => d.Key, StringComparer.Ordinal);

    /// A singleton is safe and useful: the table is immutable, so a test, the
    /// CLI and the web host all read the same policy without a container.
    public static RagDomainRegistry Instance { get; } = new();

    public RagDomainDefinition GetRequired(string domainKey)
        => TryGet(domainKey, out var domain)
            ? domain
            : throw new RagDomainUnknownException(domainKey);

    public bool TryGet(string domainKey, out RagDomainDefinition domain)
    {
        if (!string.IsNullOrWhiteSpace(domainKey) && ByKey.TryGetValue(domainKey, out var found))
        {
            domain = found;
            return true;
        }
        // `default!` rather than a null-yielding out: callers that ignore the
        // bool get a NullReferenceException at the point of misuse rather than a
        // silently permissive object.
        domain = default!;
        return false;
    }

    public IReadOnlyList<RagDomainDefinition> List() => All;
}

/// An unknown domain is a programming error, never a fallback. The message
/// carries the key because every key in the product is a compile-time constant
/// — there is no user input on this path to leak.
public sealed class RagDomainUnknownException(string domainKey)
    : Exception($"Unknown RAG domain '{domainKey}'.")
{
    public string DomainKey { get; } = domainKey;
}
