using NubArca.Api.Rag;
using NubArca.Api.Rag.Domains;

namespace NubArca.Api.Assistant;

/// Where model trust meets domain policy.
///
/// Slice 1 established that a model's TRUST decides what it is eligible for and
/// a FEATURE's operation policy decides what it actually gets. A retrieval
/// domain is the third term:
///
///     model trust  ∩  domain policy  ∩  operation policy  ∩  caller permissions
///
/// This class owns the second intersection, and it is checked BEFORE a prompt is
/// built rather than while it is built. The difference matters: a gate that runs
/// during prompt assembly can be bypassed by a future caller that assembles its
/// own prompt, and a gate that runs before it is a statement about the evidence
/// itself.
///
/// It also checks the evidence, not only the request. A caller asking for
/// `product-help` and receiving a chunk stamped `nubarca-repository` is a bug in
/// retrieval, and this is where that bug stops being a leak: every item has to
/// carry the domain that was asked for, and that domain has to be allowed.
public static class AssistantRagPolicy
{
    /// May a model at this trust level be grounded on this domain at all?
    public static bool MayGroundOn(AssistantModelTrust trust, RagDomainDefinition domain)
    {
        // No owner-private domain is activated in this slice, and this is the
        // line that keeps "the enum has the value" from becoming "the feature
        // has the capability". A domain that needs an owner needs an
        // authorization design that does not exist yet.
        if (domain.PrivacyClass == RagPrivacyClass.OwnerPrivate
            || domain.Scope == RagDomainScope.Owner
            || domain.RequiresOwner)
        {
            return false;
        }

        return trust switch
        {
            // TWO conditions, deliberately redundant. `ExternalGenerationAllowed`
            // is the domain author's explicit decision; `Public` is the privacy
            // classification. A future domain that sets one and forgets the other
            // fails closed rather than shipping the more permissive reading.
            AssistantModelTrust.External =>
                domain.ExternalGenerationAllowed && domain.PrivacyClass == RagPrivacyClass.Public,

            // The operator asserts control of this endpoint, so knowledge about
            // their own installation may be used to answer their own questions.
            AssistantModelTrust.LocalTrusted =>
                domain.PrivacyClass is RagPrivacyClass.Public or RagPrivacyClass.SystemInternal,

            // ManagedLocal and anything added later: nothing, until whoever adds
            // it says what it may do.
            _ => false,
        };
    }

    /// The gate. Returns null when the evidence may be used, or a sanitized
    /// reason code when it may not.
    public static string? Refuse(
        AssistantModelTrust trust,
        RagDomainDefinition domain,
        IReadOnlyList<RagEvidence> evidence)
    {
        if (!MayGroundOn(trust, domain))
        {
            return RagFailureReasons.DomainNotAllowed;
        }

        // Evidence from a domain other than the one that was asked for never
        // reaches a prompt, whatever its own policy says. The requested domain
        // is what the operation was reviewed against.
        foreach (var item in evidence)
        {
            if (!string.Equals(item.Domain.Value, domain.Key, StringComparison.Ordinal))
            {
                return RagFailureReasons.DomainNotAllowed;
            }
        }

        return null;
    }
}
