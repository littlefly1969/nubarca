namespace NubArca.Api.Party;

// Owner-scoped lifecycle + public validation of a party's PUBLIC CAPABILITIES.
// All owner methods collapse missing / foreign albums to null/false (the HTTP
// layer maps to a generic 404). Only token HASHES are persisted; the raw token
// is derived on demand and returned only to owner-authorized callers.
//
// It also holds THE SEAM (see ResolvePublicAsync): the one walk from a token to
// the party and its main album. Everything downstream keeps working on the
// (ownerUserId, albumId) pair it already handled correctly.
public interface IPartyLinkService
{
    // The COMPATIBILITY ENTRY POINT for "Album -> Party Mode", and the only
    // creator of parties in this slice.
    //
    // Establishes the party this album is the `main` media source of (found or
    // created), publishes it if it is still a Draft, and mints or reuses the
    // public capability. Show-on-TV is deliberately untouched: a party does not
    // require a television. If an active link already exists it is REUSED (view
    // token stays stable) and only its sub-switches are updated; otherwise a
    // fresh link with new view + upload tokens is created.
    //
    // `uploadEnabled` sets the upload sub-switch (null = keep an
    // existing link's value, or default true for a new link). `requireApproval`
    // sets the upload-approval mode (null = keep an existing link's value, or
    // default false for a new link) without rotating tokens.
    // `requireMessageApproval` does the same for guest MESSAGES, independently
    // of the upload mode. Returns null when the album is missing/foreign.
    Task<PartyEnableResult?> EnableAsync(
        Guid ownerUserId, Guid albumId, Guid createdByUserId,
        bool? uploadEnabled = null,
        bool? requireApproval = null,
        bool? requireMessageApproval = null,
        CancellationToken cancellationToken = default);

    // Disables party mode on the owner's album: revokes every active link
    // immediately. Idempotent. Returns false when the album is missing/foreign
    // (true otherwise, even if no link was active). ShowOnTv is left unchanged.
    Task<bool> DisableAsync(
        Guid ownerUserId, Guid albumId,
        CancellationToken cancellationToken = default);

    // Owner-facing status for an album (null when missing/foreign). PartyUrl is
    // populated (derived) whenever an active party link exists.
    Task<AlbumPartyStatusDto?> GetOwnerStatusAsync(
        Guid ownerUserId, Guid albumId,
        CancellationToken cancellationToken = default);

    // For the owner's paired TV / owner UI: the derived party URLs (view + upload)
    // for an album that has an active party link, else absent. UploadUrl is null
    // when the upload sub-switch is off. Batch form avoids N+1.
    //
    // This HANDS OUT a public URL, so it answers empty for an owner whose role
    // no longer carries `party.access` — the same rule the public resolvers
    // apply from the other end.
    Task<IReadOnlyDictionary<Guid, PartyLinkUrls>> GetActivePartyUrlsAsync(
        Guid ownerUserId, IReadOnlyCollection<Guid> albumIds,
        CancellationToken cancellationToken = default);

    // Updates the slideshow timing / per-participant quotas on the album's
    // ACTIVE link. Deliberately separate from EnableAsync: these four values
    // must be changeable without minting a link, rotating the view/upload
    // tokens, toggling party or upload, changing approval mode, or resetting a
    // single participant counter. Returns false when there is no active link or
    // the album is not the caller's. Out-of-range values are rejected upstream.
    Task<bool> UpdateSlideshowSettingsAsync(
        Guid ownerUserId,
        Guid albumId,
        int? photoSlideSeconds,
        int? maxVideoSlideSeconds,
        int? maxPhotoUploadsPerParticipant,
        int? maxVideoUploadsPerParticipant,
        int? maxMessagesPerParticipant,
        CancellationToken cancellationToken = default);

    Task<bool> UpdateGameSettingsAsync(
        Guid ownerUserId, Guid albumId, bool gameEnabled,
        int minChallengeIntervalSeconds, int maxChallengeIntervalSeconds,
        int votesPerGuest, int? maxChallengesPerSession,
        CancellationToken cancellationToken = default);

    // Reproduces the public VIEW token for a link, which is what lets an
    // owner-authorized surface name a guest or television URL without the raw
    // token ever having been stored. The CALLER is responsible for having
    // established that this link is the caller's — this method authorizes
    // nothing on its own.
    string DeriveViewToken(Guid linkId);

    // THE PUBLIC SEAM. Validates a public VIEW token and walks
    //     token -> PartyAlbumLink -> Party -> PartyMediaSource(main) -> Album
    // returning the party, its owner, its MAIN album, the resolving link and
    // what the owner's role permits the party to offer. Null when the
    // capability is not live, the party has closed guest access, it has no
    // main album, that album is no longer the owner's, or the owner's role no
    // longer carries `party.access` — every case a generic 404 upstream.
    // Resolves a link BY ID, for a caller that has already proved its right to
    // that link some other way — today, a television holding a display grant.
    // It runs the same party/status/expiry/capability policy as the token
    // paths, because a display must not become a way around any of it; the
    // only thing it skips is the token, which a display deliberately does not
    // have. Null when the link, its party or its phase no longer allows it.
    Task<PartyAccess?> ResolveDisplayAsync(
        Guid partyAlbumLinkId, CancellationToken cancellationToken = default);

    Task<PartyAccess?> ResolvePublicAsync(
        string token, CancellationToken cancellationToken = default);

    // The same seam for a public UPLOAD token: it matches the separate
    // upload-token hash and additionally requires the upload sub-switch to be
    // on. A view token can never satisfy this (different hash), and vice versa.
    // The returned grant also carries the link's approval mode and per-guest
    // quotas, so a contribution path needs no second query.
    Task<PartyAccess?> ResolveUploadAsync(
        string uploadToken, CancellationToken cancellationToken = default);
}
