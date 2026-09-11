using System.Text.Json;
using NubArca.Api.Domain;

namespace NubArca.Api.Party;

/// <summary>
/// One slot as the OWNER edits it: what it says, whether it is on, which
/// surfaces it belongs to, and the one photograph it may carry.
///
/// <para>Every kind is always returned, whether or not the host has written it
/// yet — a slot they have never touched comes back at version 0 with the
/// product's own default visibility. That keeps the SERVER the source of truth
/// for what "the menu belongs before and during" means, and leaves the editor a
/// renderer rather than a second opinion.</para>
///
/// <para><c>MediaFileItemId</c> is the reference as stored. <c>MediaUrl</c> is
/// the owner's own derived preview of it, present only while the file still
/// qualifies as a Party reference — so a slot whose photograph went to Trash
/// says so by carrying an id and no picture, rather than by losing the id.</para>
/// </summary>
public sealed record PartyGuestContentDto(
    string Kind,
    bool Enabled,
    bool VisibleBefore,
    bool VisibleLive,
    bool VisibleAfter,
    JsonElement Content,
    int Version,
    Guid? MediaFileItemId = null,
    string? MediaUrl = null,
    /// <summary>
    /// How that photograph participates in the guest surface: "inline" or
    /// "poster". A slot the host has never written, and every row that predates
    /// the column, is "inline" — which is what P4 rendered.
    /// </summary>
    string MediaPresentation = PartyGuestContentMediaPresentations.Inline);

/// <summary>
/// One slot as a GUEST receives it: the same words, and the photograph as an
/// address on the guest's own token rather than as the owner's file id.
///
/// <para>A separate projection, not a filtered copy of the owner's: the guest
/// has no use for the file id and no way to tell whether it is still servable,
/// so the server says both at once — <c>MediaUrl</c> is present exactly when
/// there is a picture to show.</para>
/// </summary>
public sealed record PartyGuestContentViewDto(
    string Kind,
    bool Enabled,
    bool VisibleBefore,
    bool VisibleLive,
    bool VisibleAfter,
    JsonElement Content,
    int Version,
    string? MediaUrl,
    /// <summary>
    /// How to present <c>MediaUrl</c>. It selects a RENDERING and confers no
    /// authority: both values resolve the same reference through the same rule
    /// and are served by the same relation-scoped route.
    /// </summary>
    string MediaPresentation = PartyGuestContentMediaPresentations.Inline);

/// <summary>What the owner writes into one slot.</summary>
public sealed record PartyGuestContentWrite(
    bool Enabled,
    bool VisibleBefore,
    bool VisibleLive,
    bool VisibleAfter,
    JsonElement? Content,
    int Version,
    Guid? MediaFileItemId = null,
    string MediaPresentation = PartyGuestContentMediaPresentations.Inline);

public enum PartyGuestContentOutcome
{
    Ok,

    /// <summary>Unknown party, or not this owner's. Always a generic 404 upstream.</summary>
    NotFound,

    /// <summary>A kind the product does not define. Never stored, never guessed at.</summary>
    UnknownKind,

    /// <summary>Wrong shape for this kind, or past one of its stated limits.</summary>
    InvalidPayload,

    /// <summary>
    /// The photograph is not one this owner may put on a party: missing,
    /// somebody else's, in Trash, in the Private Vault, or not an image. ONE
    /// outcome for all of them, so the answer never says whether a file exists.
    /// </summary>
    InvalidMedia,

    /// <summary>
    /// A presentation the product does not define, or "poster" with no
    /// photograph to present. A poster slot IS its picture, so one without a
    /// reference is not a state the guest surface could render.
    /// </summary>
    InvalidPresentation,

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
    /// would lose somebody's menu because the date moved. Changing the
    /// photograph is an edit of the slot like any other.</para>
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
    Task<IReadOnlyList<PartyGuestContentViewDto>> ForGuestAsync(
        Guid partyId,
        Guid ownerUserId,
        PartyGuestPhase phase,
        string token,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The file behind one slot's photograph, IF a guest standing in this phase
    /// may see it: the slot exists, is enabled and visible here, holds a
    /// reference, and that reference is still eligible. Null otherwise, and the
    /// caller answers every null with the same 404.
    ///
    /// <para>This is the whole authority a party token has over an owner's file:
    /// it reaches exactly the files a visible slot references, and none of the
    /// owner's others however well their ids are guessed.</para>
    /// </summary>
    Task<Guid?> GuestMediaFileAsync(
        Guid partyId,
        Guid ownerUserId,
        PartyGuestPhase phase,
        string kind,
        CancellationToken cancellationToken = default);
}
