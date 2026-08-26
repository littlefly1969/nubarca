namespace NubArca.Api.Rag.Domains;

/// The domain keys NubArca knows, as constants rather than as strings spelled
/// at every call site.
public static class RagDomains
{
    /// Curated, published product help. The only production-facing domain, and
    /// the only one an External model may be grounded on.
    public const string ProductHelp = "product-help";

    /// NubArca's own approved tracked source, at one revision. Development,
    /// diagnostics and retrieval evaluation — never an External model.
    public const string NubArcaRepository = "nubarca-repository";
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
/// and that is the entire point. Indexing state — which sources exist, which
/// revision was indexed, how many chunks — is data and lives in PostgreSQL.
/// PRIVACY is not data: if `nubarca-repository`'s `SystemInternal` were a
/// column, then one UPDATE, one careless admin endpoint or one restored backup
/// from a fork could turn it into `Public` and the repository would start
/// flowing to a hosted provider. There is no statement to update here, only a
/// commit to review.
///
/// `nubarca-repository` is deliberately NOT External-safe even though NubArca
/// is public on GitHub today. Public hosting is a fact about this month, not a
/// property of the domain: the same code path has to stay correct when an
/// installation carries local patches, when a fork is private, and when the
/// next system-internal domain is added by someone who read this file and
/// assumed the rule was "whatever is on GitHub".
public sealed class RagDomainRegistry : IRagDomainRegistry
{
    public static RagDomainDefinition ProductHelp { get; } = new(
        Key: RagDomains.ProductHelp,
        Scope: RagDomainScope.System,
        PrivacyClass: RagPrivacyClass.Public,
        RequiresOwner: false,
        // The one true `ExternalGenerationAllowed` in the product. It is true
        // because the corpus is an explicit manifest of published documentation
        // — not because the files happen to be readable on the internet.
        ExternalGenerationAllowed: true);

    public static RagDomainDefinition NubArcaRepository { get; } = new(
        Key: RagDomains.NubArcaRepository,
        Scope: RagDomainScope.System,
        PrivacyClass: RagPrivacyClass.SystemInternal,
        RequiresOwner: false,
        ExternalGenerationAllowed: false);

    private static readonly IReadOnlyList<RagDomainDefinition> All =
        new[] { ProductHelp, NubArcaRepository };

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
