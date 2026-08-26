namespace NubArca.Api.Assistant;

/// What a model is ELIGIBLE for, by trust classification alone.
///
/// Eligibility is not permission. A capability being true here says only that
/// the model's trust classification does not rule it out; whether a particular
/// feature uses it is that feature's own policy, and — for anything touching a
/// user's data — the caller's permissions decide as well:
///
///     model trust  ∩  feature policy  ∩  user permissions  =  effective capability
///
/// The intersection is what keeps a future Assistant tool from becoming an
/// authorization bypass. A local model does not get to read someone's library
/// because it is local; it gets to read exactly what the person asking may read,
/// through the same typed services every other caller uses.
public sealed record AssistantCapabilities(
    bool CanReceivePublicContext,
    bool CanReceivePrivateContext,
    bool CanUsePrivateRag,
    bool CanUseReadTools,
    bool CanProposeActions,
    bool CanUseWriteTools,
    bool CanExecuteWithoutConfirmation)
{
    /// Nothing at all — the answer for an unresolvable or refused model.
    public static AssistantCapabilities None { get; } = new(
        CanReceivePublicContext: false,
        CanReceivePrivateContext: false,
        CanUsePrivateRag: false,
        CanUseReadTools: false,
        CanProposeActions: false,
        CanUseWriteTools: false,
        CanExecuteWithoutConfirmation: false);

    /// The pairwise AND. A feature narrows what its model is eligible for; it
    /// can never widen it, because there is no operation here that turns a
    /// false into a true.
    public AssistantCapabilities Intersect(AssistantCapabilities other) => new(
        CanReceivePublicContext && other.CanReceivePublicContext,
        CanReceivePrivateContext && other.CanReceivePrivateContext,
        CanUsePrivateRag && other.CanUsePrivateRag,
        CanUseReadTools && other.CanUseReadTools,
        CanProposeActions && other.CanProposeActions,
        CanUseWriteTools && other.CanUseWriteTools,
        CanExecuteWithoutConfirmation && other.CanExecuteWithoutConfirmation);
}

/// The single place trust becomes capability.
///
/// One function, no configuration, no per-feature overrides: an installation
/// cannot widen what External is eligible for, because there is no switch to
/// widen it with.
public static class AssistantCapabilityPolicy
{
    public static AssistantCapabilities ForTrust(AssistantModelTrust trust) => trust switch
    {
        // Public product knowledge and what the user typed. Nothing else may
        // cross the boundary, and no capability that could fetch more is
        // eligible in the first place.
        AssistantModelTrust.External => new AssistantCapabilities(
            CanReceivePublicContext: true,
            CanReceivePrivateContext: false,
            CanUsePrivateRag: false,
            CanUseReadTools: false,
            CanProposeActions: false,
            CanUseWriteTools: false,
            CanExecuteWithoutConfirmation: false),

        // The operator asserts control of this endpoint, so private context and
        // read tools become ELIGIBLE — for features designed and reviewed for
        // them, of which there are currently none.
        AssistantModelTrust.LocalTrusted => new AssistantCapabilities(
            CanReceivePublicContext: true,
            CanReceivePrivateContext: true,
            CanUsePrivateRag: true,
            CanUseReadTools: true,
            CanProposeActions: true,
            // Writing is not a trust question. Nothing in NubArca changes
            // because a model suggested it, at any trust level: a proposal is
            // shown to a person, and the person acts.
            CanUseWriteTools: false,
            CanExecuteWithoutConfirmation: false),

        // Unreachable through a validated profile — AssistantModelResolver
        // refuses ManagedLocal — and answered as nothing rather than as
        // "like LocalTrusted, but more", so a future runtime has to state its
        // own policy rather than inherit one written before it existed.
        _ => AssistantCapabilities.None,
    };

    public static AssistantCapabilities For(AssistantModelProfile? profile)
        => profile is null ? AssistantCapabilities.None : ForTrust(profile.Trust);
}

/// What the HELP operation is willing to use, whatever its model is eligible for.
///
/// Public product knowledge, and nothing else. This is the half of the
/// intersection that makes "we configured a local model" and "the assistant can
/// now see my photos" different statements: a LocalTrusted Help still sends
/// only `product-help` evidence, because the operation says so.
public static class HelpOperationPolicy
{
    public static AssistantCapabilities Operation { get; } = new(
        CanReceivePublicContext: true,
        CanReceivePrivateContext: false,
        CanUsePrivateRag: false,
        CanUseReadTools: false,
        CanProposeActions: false,
        CanUseWriteTools: false,
        CanExecuteWithoutConfirmation: false);

    public static AssistantCapabilities Effective(AssistantModelProfile? profile)
        => AssistantCapabilityPolicy.For(profile).Intersect(Operation);
}
