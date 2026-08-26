using Microsoft.Extensions.Options;
using NubArca.Api.Help;

namespace NubArca.Api.Assistant;

/// Turns operator configuration into a validated model profile, or into a
/// sanitized reason there is none.
///
/// Everything that could be got wrong about a model endpoint is decided HERE,
/// once, at the edge:
///
///   - the protocol must be one NubArca implements;
///   - the trust classification must be stated, and must be one NubArca honours;
///   - `ManagedLocal` is refused, because NubArca ships no managed isolated
///     runtime and an installation must not be able to claim one;
///   - an External endpoint must be HTTPS and must have a key, because the key
///     travels in an Authorization header and the bytes leave the boundary;
///   - a LocalTrusted endpoint may be HTTP and may be authless, because that is
///     what an operator's own llama.cpp/Ollama/vLLM server usually is.
///
/// And one thing is deliberately NOT decided here: what the URL looks like never
/// changes the trust classification, in either direction.
public sealed class AssistantModelResolver
{
    /// The profile key a legacy `ExternalHelp__*` configuration is adapted into.
    /// Never operator-chosen, so it cannot collide with a configured name.
    public const string LegacyExternalHelpKey = "legacy-external-help";

    private readonly AssistantModelResolution _helpModel;
    private readonly AssistantHelpOptions _helpBounds;

    public AssistantModelResolver(
        IOptions<AssistantOptions> assistant,
        IOptions<ExternalHelpOptions> legacy,
        ILogger<AssistantModelResolver> log)
    {
        var options = assistant.Value;
        // The new configuration WINS when it is present at all, so an operator
        // who has started migrating never gets a silent mix of the two.
        if (IsConfigured(options))
        {
            _helpBounds = options.Help;
            _helpModel = ResolveHelpModel(options);
        }
        else
        {
            _helpBounds = LegacyBounds(legacy.Value);
            _helpModel = ResolveLegacy(legacy.Value);
        }

        // Safe operational facts only: which profile, which classification.
        // Never the URL, never the key, never the model id.
        if (_helpModel.Profile is { } profile)
        {
            log.LogInformation(
                "assistant: help model profile={Profile} protocol={Protocol} trust={Trust}",
                profile.Key, profile.Protocol, profile.Trust);
        }
    }

    /// The model the Help feature uses, or why it has none.
    public AssistantModelResolution HelpModel => _helpModel;

    /// Bounds and corpus location for the Help feature.
    public AssistantHelpOptions HelpBounds => _helpBounds;

    private static bool IsConfigured(AssistantOptions options)
        => options.Enabled
           || options.Models.Count > 0
           || !string.IsNullOrWhiteSpace(options.HelpModel);

    private static AssistantModelResolution ResolveHelpModel(AssistantOptions options)
    {
        if (!options.Enabled) return AssistantModelResolution.Unusable(AssistantFailureReasons.Disabled);
        if (string.IsNullOrWhiteSpace(options.HelpModel))
        {
            return AssistantModelResolution.Unusable(AssistantFailureReasons.NotConfigured);
        }
        if (!options.Models.TryGetValue(options.HelpModel, out var model) || model is null)
        {
            return AssistantModelResolution.Unusable(AssistantFailureReasons.NotConfigured);
        }
        return Validate(options.HelpModel, model);
    }

    /// Public so configuration tests drive the REAL decision rather than a
    /// paraphrase of it.
    public static AssistantModelResolution Validate(string key, AssistantModelOptions model)
    {
        if (ParseName<AssistantModelProtocol>(model.Protocol) is not { } protocol)
        {
            return AssistantModelResolution.Unusable(AssistantFailureReasons.NotConfigured);
        }

        // FAIL CLOSED on trust. Unknown, empty, misspelled, numeric — all
        // invalid, and none of them silently becomes Local. A typo in the one
        // field that decides whether private data may ever be sent must not
        // resolve to the permissive answer.
        if (ParseName<AssistantModelTrust>(model.Trust) is not { } trust)
        {
            return AssistantModelResolution.Unusable(AssistantFailureReasons.NotConfigured);
        }

        // NubArca does not run a managed isolated model runtime. Accepting this
        // would let an installation present an isolation guarantee that nothing
        // in the product implements.
        if (trust == AssistantModelTrust.ManagedLocal)
        {
            return AssistantModelResolution.Unusable(AssistantFailureReasons.NotConfigured);
        }

        if (string.IsNullOrWhiteSpace(model.BaseUrl) || string.IsNullOrWhiteSpace(model.Model))
        {
            return AssistantModelResolution.Unusable(AssistantFailureReasons.NotConfigured);
        }
        if (!Uri.TryCreate(model.BaseUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            return AssistantModelResolution.Unusable(AssistantFailureReasons.NotConfigured);
        }

        if (trust == AssistantModelTrust.External)
        {
            // The key rides in an Authorization header on every request, and the
            // request crosses the boundary anyway: plaintext would put both the
            // key and the question on the wire.
            if (uri.Scheme != Uri.UriSchemeHttps)
            {
                return AssistantModelResolution.Unusable(AssistantFailureReasons.NotConfigured);
            }
            if (string.IsNullOrWhiteSpace(model.ApiKey))
            {
                return AssistantModelResolution.Unusable(AssistantFailureReasons.NotConfigured);
            }
        }
        // LocalTrusted: HTTP is allowed and a key is optional. A trusted LAN or
        // container-network endpoint frequently terminates no TLS and wants no
        // auth, and refusing that would push operators towards declaring a real
        // external provider "local" to make it work — the opposite of the point.
        // A key that IS configured is still treated as a secret.

        return AssistantModelResolution.Usable(new AssistantModelProfile(
            Key: key,
            Protocol: protocol,
            Trust: trust,
            BaseUrl: model.BaseUrl,
            ApiKey: model.ApiKey,
            Model: model.Model,
            Label: string.IsNullOrWhiteSpace(model.Label) ? DefaultLabel(trust) : model.Label,
            TimeoutSeconds: model.TimeoutSeconds,
            MaxOutputTokens: model.MaxOutputTokens));
    }

    /// Parse by NAME only.
    ///
    /// `Enum.TryParse` also accepts the underlying number, which would make
    /// `Trust=1` mean LocalTrusted — a value nobody would write on purpose and
    /// exactly the one an accident produces. Surrounding whitespace IS accepted,
    /// because an environment variable carrying a trailing space is an accident
    /// with no second meaning, and case is ignored so `external` works.
    private static TEnum? ParseName<TEnum>(string? value) where TEnum : struct, Enum
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        foreach (var name in Enum.GetNames<TEnum>())
        {
            if (string.Equals(name, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                return Enum.Parse<TEnum>(name);
            }
        }
        return null;
    }

    private static string DefaultLabel(AssistantModelTrust trust) => trust switch
    {
        AssistantModelTrust.External => "External AI",
        _ => "Local AI",
    };

    // ---- legacy ExternalHelp compatibility ---------------------------------
    //
    // An installation configured before named profiles existed keeps working,
    // adapted into exactly ONE profile. It is always External: a configuration
    // shape that predates the trust axis cannot assert a trust classification,
    // and the safe reading of "an operator pointed this at a provider" is that
    // the provider is a provider. This is a deprecation path, not the
    // configuration model to write documentation around.

    private static AssistantModelResolution ResolveLegacy(ExternalHelpOptions legacy)
    {
        if (!legacy.Enabled)
        {
            return AssistantModelResolution.Unusable(AssistantFailureReasons.Disabled);
        }
        return Validate(LegacyExternalHelpKey, new AssistantModelOptions
        {
            Protocol = nameof(AssistantModelProtocol.OpenAiCompatible),
            Trust = nameof(AssistantModelTrust.External),
            BaseUrl = legacy.BaseUrl,
            ApiKey = legacy.ApiKey,
            Model = legacy.Model,
            Label = legacy.ProviderLabel,
            TimeoutSeconds = legacy.TimeoutSeconds,
            MaxOutputTokens = legacy.MaxOutputTokens,
        });
    }

    private static AssistantHelpOptions LegacyBounds(ExternalHelpOptions legacy) => new()
    {
        MaxQuestionCharacters = legacy.MaxQuestionCharacters,
        MaxHistoryTurns = legacy.MaxHistoryTurns,
        MaxHistoryCharacters = legacy.MaxHistoryCharacters,
        MaxEvidenceChunks = legacy.MaxContextExcerpts,
        MaxEvidenceCharacters = legacy.MaxContextCharacters,
        CorpusPath = legacy.CorpusPath,
    };
}
