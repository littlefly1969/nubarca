using System.Text.Json;
using NubArca.Api.Domain;

namespace NubArca.Api.Party;

/// <summary>
/// One slot as the OWNER edits it: what it says, whether it is on, and which
/// surfaces it belongs to.
///
/// <para>Every kind is always returned, whether or not the host has written it
/// yet — a slot they have never touched comes back at version 0 with the
/// product's own default visibility. That keeps the SERVER the source of truth
/// for what "the menu belongs before and during" means, and leaves the editor a
/// renderer rather than a second opinion.</para>
/// </summary>
public sealed record PartyGuestContentDto(
    string Kind,
    bool Enabled,
    bool VisibleBefore,
    bool VisibleLive,
    bool VisibleAfter,
    JsonElement Content,
    int Version);

/// <summary>What the owner writes into one slot.</summary>
public sealed record PartyGuestContentWrite(
    bool Enabled,
    bool VisibleBefore,
    bool VisibleLive,
    bool VisibleAfter,
    JsonElement? Content,
    int Version);

public enum PartyGuestContentOutcome
{
    Ok,

    /// <summary>Unknown party, or not this owner's. Always a generic 404 upstream.</summary>
    NotFound,

    /// <summary>A kind the product does not define. Never stored, never guessed at.</summary>
    UnknownKind,

    /// <summary>Wrong shape for this kind, or past one of its stated limits.</summary>
    InvalidPayload,

    /// <summary>Somebody else edited this slot since the caller read it.</summary>
    VersionConflict,
}

public sealed record PartyGuestContentResult(
    PartyGuestContentOutcome Outcome, PartyGuestContentDto? Content = null);

/// <summary>
/// The owner's typed guest content. Six named shapes, validated server-side,
/// and deliberately nothing that could grow into a CMS.
/// </summary>
public interface IPartyGuestContentService
{
    /// <summary>
    /// Every kind for this party, in the order the guest surface renders them.
    /// Null when the party is missing or is not the caller's.
    /// </summary>
    Task<IReadOnlyList<PartyGuestContentDto>?> ListAsync(
        Guid ownerUserId, Guid partyId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes one slot, version-checked. A slot that does not exist yet is
    /// created by stating version 0; a conflict returns the CURRENT slot so the
    /// editor refreshes rather than overwrites.
    ///
    /// <para>The version is the SLOT's own, never the party's: editing the menu
    /// and renaming the party are unrelated decisions, and making them contend
    /// would lose somebody's menu because the date moved.</para>
    /// </summary>
    Task<PartyGuestContentResult> UpsertAsync(
        Guid ownerUserId,
        Guid partyId,
        string kind,
        PartyGuestContentWrite write,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// What a GUEST should see right now: the enabled slots visible in this
    /// phase, in product order. Read-only, and it never reveals a slot the host
    /// has turned off or scoped to another surface.
    /// </summary>
    Task<IReadOnlyList<PartyGuestContentDto>> ForGuestAsync(
        Guid partyId, PartyGuestPhase phase, CancellationToken cancellationToken = default);
}
