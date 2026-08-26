using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NubArca.Api.Data;
using NubArca.Api.Domain.Ai;
using NubArca.Api.Rag;

namespace NubArca.Api.Ai.TextEmbeddings;

/// The active text-embedding profile, or a sanitized reason there is none.
public sealed record TextEmbeddingResolution(
    AiProfile? Profile, ITextEmbeddingProvider? Provider, string? Reason)
{
    public static TextEmbeddingResolution Unavailable(string reason) => new(null, null, reason);

    public bool IsAvailable => Profile is not null && Provider is not null;
}

/// Resolves which local model embeds text, the same way the photo substrate
/// resolves which model embeds images: an EXPLICIT profile key from
/// configuration, never "the newest one" and never "the only one installed".
///
/// Implicit selection is what makes a model upgrade silently reinterpret
/// existing vectors. Here a different model means a different profile, and a
/// different profile means its own embeddings — so the worst outcome of a
/// misconfiguration is that semantic retrieval reports itself unavailable and
/// the lexical path answers, rather than that cosine distances quietly start
/// comparing two spaces.
public sealed class TextEmbeddingResolver
{
    private readonly AppDbContext _db;
    private readonly IEnumerable<ITextEmbeddingProvider> _providers;
    private readonly IOptions<RagOptions> _options;

    public TextEmbeddingResolver(
        AppDbContext db,
        IEnumerable<ITextEmbeddingProvider> providers,
        IOptions<RagOptions> options)
    {
        _db = db;
        _providers = providers;
        _options = options;
    }

    public async Task<TextEmbeddingResolution> ResolveAsync(CancellationToken cancellationToken = default)
    {
        var options = _options.Value;
        if (!options.SemanticEnabled)
        {
            return TextEmbeddingResolution.Unavailable(RagFailureReasons.EmbeddingDisabled);
        }

        var key = options.TextEmbeddingProfileKey;
        if (string.IsNullOrWhiteSpace(key))
        {
            return TextEmbeddingResolution.Unavailable(RagFailureReasons.EmbeddingProfileUnavailable);
        }

        return await ResolveProfileAsync(key, cancellationToken);
    }

    /// Resolve a NAMED profile regardless of the `SemanticEnabled` switch, for
    /// the CLI: an operator validating a model before turning the feature on
    /// should not have to turn the feature on to validate it.
    public async Task<TextEmbeddingResolution> ResolveProfileAsync(
        string profileKey, CancellationToken cancellationToken = default)
    {
        var profile = await _db.AiProfiles.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Key == profileKey, cancellationToken);

        if (profile is null
            || !profile.Enabled
            || profile.Capability != AiCapabilities.TextEmbedding
            || profile.Modality != AiModalities.Text)
        {
            return TextEmbeddingResolution.Unavailable(RagFailureReasons.EmbeddingProfileUnavailable);
        }

        if (profile.Dimension is not > 0)
        {
            return TextEmbeddingResolution.Unavailable(RagFailureReasons.EmbeddingDimensionUnsupported);
        }

        var model = await _db.AiModels.AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == profile.AiModelId, cancellationToken);
        if (model is null || !model.Enabled)
        {
            return TextEmbeddingResolution.Unavailable(RagFailureReasons.EmbeddingProfileUnavailable);
        }

        var provider = _providers.FirstOrDefault(p =>
            string.Equals(p.Provider, model.Provider, StringComparison.Ordinal));
        if (provider is null)
        {
            return TextEmbeddingResolution.Unavailable(RagFailureReasons.EmbeddingProfileUnavailable);
        }

        var readiness = provider.CheckReadiness(profile);
        return readiness.IsReady
            ? new TextEmbeddingResolution(profile, provider, null)
            : TextEmbeddingResolution.Unavailable(
                readiness.Reason ?? RagFailureReasons.EmbeddingModelUnavailable);
    }
}
