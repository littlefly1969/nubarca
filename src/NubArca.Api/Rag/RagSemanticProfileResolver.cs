using Microsoft.Extensions.Options;
using NubArca.Api.Rag.Domains;

namespace NubArca.Api.Rag;

/// What ONE domain has been configured to do about semantic retrieval.
///
/// `ProfileKey` is null whenever `Enabled` is false, so a caller cannot embed
/// against a profile a domain never asked for by reading one field and not the
/// other.
public sealed record RagSemanticSettings(bool Enabled, string? ProfileKey)
{
    public static readonly RagSemanticSettings Disabled = new(false, null);
}

/// Which embedding profile a domain uses, and whether it uses one at all.
public interface IRagSemanticProfileResolver
{
    RagSemanticSettings Resolve(RagDomainKey domain);
}

/// Resolves semantic configuration PER DOMAIN.
///
/// There is no installation-wide default to inherit: the only domain is an
/// owner-private corpus, and running a model over somebody's own documents
/// requires `Rag__Domains__user-documents__SemanticEnabled=true` and a profile
/// key for that domain, by name.
public sealed class RagSemanticProfileResolver : IRagSemanticProfileResolver
{
    private readonly IRagDomainRegistry _domains;
    private readonly IOptions<RagOptions> _options;

    public RagSemanticProfileResolver(IRagDomainRegistry domains, IOptions<RagOptions> options)
    {
        _domains = domains;
        _options = options;
    }

    public RagSemanticSettings Resolve(RagDomainKey domain)
    {
        // An unknown domain gets nothing. There is no default domain anywhere
        // else in the substrate and there is not one here.
        if (!_domains.TryGet(domain.Value, out _))
        {
            return RagSemanticSettings.Disabled;
        }

        _options.Value.Domains.TryGetValue(domain.Value, out var configured);
        if (configured?.SemanticEnabled != true) return RagSemanticSettings.Disabled;

        // Enabled with no profile is not enabled. The caller reports
        // `text_embedding_profile_unavailable` rather than guessing which model
        // was meant.
        var key = Trimmed(configured.TextEmbeddingProfileKey);
        return key is null ? RagSemanticSettings.Disabled : new RagSemanticSettings(true, key);
    }

    private static string? Trimmed(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
