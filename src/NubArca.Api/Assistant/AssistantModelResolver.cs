using Microsoft.Extensions.Options;

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
    private readonly AssistantModelResolution _privateKnowledgeModel;
    private readonly AssistantBoundsOptions _bounds;

    public AssistantModelResolver(
        IOptions<AssistantOptions> assistant,
        ILogger<AssistantModelResolver> log)
    {
        var options = assistant.Value;
        _bounds = options.Bounds;
        _privateKnowledgeModel = ResolvePrivateKnowledgeModel(options);

        // Safe operational facts only: which profile, which classification.
        // Never the URL, never the key, never the model id.
        if (_privateKnowledgeModel.Profile is { } profile)
        {
            log.LogInformation(
                "assistant: private knowledge model profile={Profile} protocol={Protocol} trust={Trust}",
                profile.Key, profile.Protocol, profile.Trust);
        }
    }

    /// The model that may read the caller's OWN DOCUMENTS, or why there is none.
    ///
    /// Guaranteed `LocalTrusted` when usable. Every caller can therefore treat
    /// "usable" as "may be given private evidence" without repeating the trust
    /// check — and, more importantly, without any of them being able to skip it.
    public AssistantModelResolution PrivateKnowledgeModel => _privateKnowledgeModel;

    /// Bounds on what a question to the Assistant may carry.
    public AssistantBoundsOptions Bounds => _bounds;

    /// The private-knowledge model: a named profile that must ALSO be
    /// LocalTrusted.
    ///
    /// Two separate refusals, and the second one is the point of this method.
    /// Everything Validate checks — protocol, trust parsed by name, no
    /// ManagedLocal, a usable URL — applies here too. On top of it, a model that
    /// resolves perfectly well and happens to be External is refused for THIS
    /// use, because the question a person asks about their own documents is
    /// itself private and the evidence certainly is.
    ///
    /// Refused at CONFIGURATION time rather than at request time, so an
    /// installation that pointed this at a provider has a feature that is off
    /// rather than a feature that leaks. There is no fallback of any kind:
    /// unusable means no private answers.
    private static AssistantModelResolution ResolvePrivateKnowledgeModel(AssistantOptions options)
    {
        if (!options.Enabled) return AssistantModelResolution.Unusable(AssistantFailureReasons.Disabled);
        if (string.IsNullOrWhiteSpace(options.PrivateKnowledgeModel))
        {
            return AssistantModelResolution.Unusable(AssistantFailureReasons.NotConfigured);
        }
        if (!options.Models.TryGetValue(options.PrivateKnowledgeModel, out var model) || model is null)
        {
            return AssistantModelResolution.Unusable(AssistantFailureReasons.NotConfigured);
        }

        var resolved = Validate(options.PrivateKnowledgeModel, model);
        if (resolved.Profile is not { } profile)
        {
            return resolved;
        }

        return profile.Trust == AssistantModelTrust.LocalTrusted
            ? resolved
            : AssistantModelResolution.Unusable(AssistantFailureReasons.PrivateModelNotLocal);
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
}
