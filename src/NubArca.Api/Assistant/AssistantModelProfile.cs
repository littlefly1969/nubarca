namespace NubArca.Api.Assistant;

/// A model endpoint that has PASSED validation.
///
/// Nothing constructs one of these except AssistantModelResolver, so every
/// consumer downstream may assume the invariants: a known protocol, an explicit
/// trust classification, transport appropriate to that classification, and the
/// credentials that classification requires. "Is this configuration safe" is
/// answered once, at the edge, rather than re-asked by everything that uses it.
public sealed record AssistantModelProfile(
    string Key,
    AssistantModelProtocol Protocol,
    AssistantModelTrust Trust,
    string BaseUrl,
    string ApiKey,
    string Model,
    string Label,
    int TimeoutSeconds,
    int MaxOutputTokens)
{
    public int EffectiveTimeoutSeconds => Math.Clamp(TimeoutSeconds, 1, 120);
    public int EffectiveMaxOutputTokens => Math.Clamp(MaxOutputTokens, 1, 4000);

    /// The one bounded fact about trust that is safe to show a browser. A user
    /// needs to know whether their words leave the installation; they do not
    /// need the URL, the model id or the key.
    public string Boundary => Trust switch
    {
        AssistantModelTrust.External => "external",
        _ => "localTrusted",
    };
}

/// Either a usable profile, or the sanitized reason there is none.
///
/// Configuration problems are reported the same way provider problems are: an
/// optional feature that cannot work is a state, not an application error, and
/// the browser must be able to say so without learning what is misconfigured.
public sealed record AssistantModelResolution(AssistantModelProfile? Profile, string? Reason)
{
    public static AssistantModelResolution Usable(AssistantModelProfile profile)
        => new(profile, null);

    public static AssistantModelResolution Unusable(string reason) => new(null, reason);

    public bool IsUsable => Profile is not null;
}
