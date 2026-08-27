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

/// Resolves semantic configuration PER DOMAIN, with one deliberate asymmetry.
///
/// One global switch stopped being defensible once it was measured. Against
/// `multilingual-e5-small`, Product Help's MRR goes from 0.938 to 0.969 while the
/// repository's Recall@5 goes from 0.800 down to 0.700 — semantic similarity
/// between two paragraphs of prose is a much stronger signal than between two
/// blocks of C#. `Rag__SemanticEnabled` forced one answer onto both.
///
/// THE ASYMMETRY: a domain may inherit the installation default only if its
/// knowledge is not OwnerPrivate. An owner-private corpus is somebody's own
/// documents, and "we turned semantic on for Help eighteen months ago" is not
/// consent to run a model over them. Inheriting would make the safe-by-default
/// property depend on which settings an operator happened to have; requiring
/// `Rag__Domains__user-documents__SemanticEnabled=true` makes it a decision
/// someone made about that corpus.
///
/// The rule is derived from the domain's PRIVACY CLASS rather than from its key,
/// so the next owner-private domain gets it without anybody remembering to add
/// it to a list — which is the failure mode a list has.
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
        if (!_domains.TryGet(domain.Value, out var definition))
        {
            return RagSemanticSettings.Disabled;
        }

        var options = _options.Value;
        options.Domains.TryGetValue(domain.Value, out var configured);

        var mayInherit = definition.PrivacyClass != RagPrivacyClass.OwnerPrivate;

        var enabled = configured?.SemanticEnabled
                      ?? (mayInherit && options.SemanticEnabled);
        if (!enabled) return RagSemanticSettings.Disabled;

        // The profile follows the same rule as the switch, and for the same
        // reason: an owner-private domain that inherited the key would be
        // embedding a person's documents into whichever coordinate system Help
        // happens to use.
        var key = Trimmed(configured?.TextEmbeddingProfileKey)
                  ?? (mayInherit ? Trimmed(options.TextEmbeddingProfileKey) : null);

        // Enabled with no profile is not enabled. The caller reports
        // `rag_embedding_profile_unavailable` rather than guessing which model
        // was meant.
        return key is null ? RagSemanticSettings.Disabled : new RagSemanticSettings(true, key);
    }

    private static string? Trimmed(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
