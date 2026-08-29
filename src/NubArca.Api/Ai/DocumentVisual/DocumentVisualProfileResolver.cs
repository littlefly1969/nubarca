using Microsoft.Extensions.Options;
using NubArca.Api.Ai.Backends;
using NubArca.Api.Ai.Resolution;
using NubArca.Api.Domain.Ai;

namespace NubArca.Api.Ai.DocumentVisual;

/// The resolved dense visual profile and the two towers that serve it, or the
/// sanitized reason there is none.
///
/// BOTH TOWERS OR NEITHER. A profile whose image encoder loads and whose text
/// encoder does not is worse than an unavailable one: pages would be indexed
/// into a space no question can be asked in, spending hours of local inference
/// to build a corpus nothing can search.
public sealed record DocumentVisualProfileResolution(
    bool IsAvailable,
    AiProfile? Profile,
    IImageEmbedder? Pages,
    ITextEmbedder? Queries,
    string? Reason)
{
    public static DocumentVisualProfileResolution Unavailable(string reason)
        => new(false, null, null, null, reason);
}

/// WHICH visual model reads documents here — decided in one place, explicitly.
///
/// Precedence is a single configuration key and nothing else:
/// `Ai:DocumentVisual:DenseProfileKey`. There is no "latest installed model"
/// heuristic, no timestamp comparison and no capability default to fall back
/// to — the substrate rule is that model identity is explicit, and a fallback
/// is how a second profile appearing in the catalogue silently reinterprets
/// everybody's documents.
///
/// UNKNOWN CONFIGURATION FAILS CLOSED. A profile key that does not exist, a
/// profile with the wrong capability, a dimension that is not 1152, a disabled
/// profile, a model whose files are not on disk: every one of them reports the
/// visual path unavailable, and the already-valid text retrieval path continues
/// untouched. None of them is ever recorded as a verdict about somebody's
/// document.
public sealed class DocumentVisualProfileResolver
{
    private readonly IAiBackendResolver _backends;
    private readonly IAiProfileRegistry _profiles;
    private readonly IOptions<DocumentVisualOptions> _options;

    public DocumentVisualProfileResolver(
        IAiBackendResolver backends,
        IAiProfileRegistry profiles,
        IOptions<DocumentVisualOptions> options)
    {
        _backends = backends;
        _profiles = profiles;
        _options = options;
    }

    public bool Enabled => _options.Value.Enabled;

    public async Task<DocumentVisualProfileResolution> ResolveAsync(
        CancellationToken cancellationToken = default)
    {
        var options = _options.Value;
        if (!options.Enabled)
        {
            return DocumentVisualProfileResolution.Unavailable(DocumentVisualReasons.Disabled);
        }

        var key = (options.DenseProfileKey ?? string.Empty).Trim();
        if (key.Length == 0)
        {
            return DocumentVisualProfileResolution.Unavailable(DocumentVisualReasons.ModelUnavailable);
        }

        // THE PROFILE ROW, read directly, because everything downstream needs
        // its Id: a stored vector is keyed by ProfileId, and the resolution the
        // backend resolver returns deliberately carries only the stable KEY.
        var profile = await _profiles.GetProfileByKeyAsync(key, cancellationToken);
        if (profile is null || !profile.Enabled)
        {
            return DocumentVisualProfileResolution.Unavailable(DocumentVisualReasons.ModelUnavailable);
        }

        // THE CAPABILITY IS CHECKED HERE, not assumed from the key. Pointing
        // this at the photo profile would otherwise "work" — same weights, same
        // dimension — and quietly write document vectors under the photo
        // profile's identity, where a photo reindex would later delete them.
        if (!string.Equals(
                profile.Capability, AiCapabilities.DocumentVisualEmbedding, StringComparison.Ordinal))
        {
            return DocumentVisualProfileResolution.Unavailable(DocumentVisualReasons.ModelUnavailable);
        }

        // THE DIMENSION IS ASSERTED, not read. A checkpoint returning something
        // other than 1152 is a different model wearing this profile's name, and
        // the accelerator table it would write into has a fixed width.
        if (profile.Dimension != DocumentVisualProfiles.DenseDimension)
        {
            return DocumentVisualProfileResolution.Unavailable(
                DocumentVisualReasons.ModelOutputUnsupported);
        }

        var pages = await _backends.ResolveForProfileKeyAsync<IImageEmbedder>(key, cancellationToken);
        if (!pages.IsAvailable || pages.Backend is null)
        {
            return DocumentVisualProfileResolution.Unavailable(
                pages.Resolution.UnavailableReason ?? DocumentVisualReasons.ModelUnavailable);
        }

        var queries = await _backends.ResolveForProfileKeyAsync<ITextEmbedder>(key, cancellationToken);
        if (!queries.IsAvailable || queries.Backend is null)
        {
            // The paired tower is missing. BOTH OR NEITHER: indexing pages into
            // a space no question can be asked in would spend hours of local
            // inference building a corpus nothing can search.
            return DocumentVisualProfileResolution.Unavailable(
                queries.Resolution.UnavailableReason ?? DocumentVisualReasons.ModelUnavailable);
        }

        return new DocumentVisualProfileResolution(
            true, profile, pages.Backend, queries.Backend, null);
    }
}
