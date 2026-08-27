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
        => trust switch
        {
            // TWO conditions, deliberately redundant. `ExternalGenerationAllowed`
            // is the domain author's explicit decision; `Public` is the privacy
            // classification. A future domain that sets one and forgets the other
            // fails closed rather than shipping the more permissive reading.
            //
            // OwnerPrivate is neither, so a person's own documents can never
            // satisfy this branch — including with an optimistic
            // `ExternalGenerationAllowed: true` written into a definition,
            // because the privacy class still has to be Public.
            AssistantModelTrust.External =>
                domain.ExternalGenerationAllowed && domain.PrivacyClass == RagPrivacyClass.Public,

            // The operator asserts control of this endpoint. That covers
            // knowledge about their own installation, and — since Slice 3 — the
            // OWNER'S OWN documents, which is the entire point of a local model:
            // a person's private text may be read by something running on their
            // own hardware and by nothing else.
            //
            // Eligibility is not authorization. Passing here means "a
            // LocalTrusted model may see owner-private evidence"; it does NOT
            // mean this caller may see THIS owner's. That is Refuse's job below,
            // and the two are separate on purpose — a check that answered both
            // questions at once would have no way to say which one failed.
            AssistantModelTrust.LocalTrusted =>
                domain.PrivacyClass is RagPrivacyClass.Public
                    or RagPrivacyClass.SystemInternal
                    or RagPrivacyClass.OwnerPrivate,

            // ManagedLocal and anything added later: nothing, until whoever adds
            // it says what it may do.
            _ => false,
        };

    /// The gate. Returns null when the evidence may be used, or a sanitized
    /// reason code when it may not.
    ///
    /// `ownerUserId` is the AUTHENTICATED caller, derived server-side. It is
    /// required for an owner-scoped domain and must match every piece of
    /// evidence: retrieval already restricted the corpus to that owner, and this
    /// re-checks the result, because "the query was scoped correctly" and "the
    /// evidence belongs to this person" are two statements and only the second
    /// one is the thing that must be true.
    public static string? Refuse(
        AssistantModelTrust trust,
        RagDomainDefinition domain,
        IReadOnlyList<RagEvidence> evidence,
        Guid? ownerUserId = null)
    {
        if (!MayGroundOn(trust, domain))
        {
            return RagFailureReasons.DomainNotAllowed;
        }

        // AN OWNER-SCOPED DOMAIN WITH NO OWNER IS REFUSED, not answered broadly.
        // This is the check that survives a future caller who builds their own
        // query and forgets: there is no owner to compare against, so there is
        // no evidence that can pass.
        if (domain.RequiresOwner && (ownerUserId is not { } caller || caller == Guid.Empty))
        {
            return RagFailureReasons.OwnerRequired;
        }

        foreach (var item in evidence)
        {
            // Evidence from a domain other than the one that was asked for never
            // reaches a prompt, whatever its own policy says. The requested
            // domain is what the operation was reviewed against.
            if (!string.Equals(item.Domain.Value, domain.Key, StringComparison.Ordinal))
            {
                return RagFailureReasons.DomainNotAllowed;
            }

            if (!domain.RequiresOwner) continue;

            // Unstamped is refused as firmly as wrong. A piece of owner-private
            // evidence that cannot say whose it is has no claim to be in this
            // person's answer, and treating "null" as "probably fine" is exactly
            // how a system domain's chunk would slip into a private prompt.
            if (item.OwnerUserId != ownerUserId)
            {
                return RagFailureReasons.OwnerRequired;
            }
        }

        return null;
    }
}
