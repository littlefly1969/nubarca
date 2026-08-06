namespace NubArca.Api.Party;

// Owner-scoped lifecycle + public validation of party album links. All owner
// methods collapse missing / foreign albums to null/false (the HTTP layer maps
// to a generic 404). Only token HASHES are persisted; the raw token is derived
// on demand and returned only to owner-authorized callers.
public interface IPartyLinkService
{
    // Enables party mode on the owner's album (party master switch). Ensures the
    // album is ShowOnTv (party implies TV visibility). If an active link already
    // exists it is REUSED (view token stays stable) and only its upload
    // sub-switch is updated; otherwise a fresh link with new view + upload tokens
    // is created. `uploadEnabled` sets the upload sub-switch (null = keep an
    // existing link's value, or default true for a new link). `requireApproval`
    // sets the upload-approval mode (null = keep an existing link's value, or
    // default false for a new link) without rotating tokens. Returns null when
    // the album is missing/foreign.
    Task<PartyEnableResult?> EnableAsync(
        Guid ownerUserId, Guid albumId, Guid createdByUserId,
        bool? uploadEnabled = null,
        bool? requireApproval = null,
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
    // for an album when it is ShowOnTv AND has an active party link, else absent.
    // UploadUrl is null when the upload sub-switch is off. Batch form avoids N+1.
    Task<IReadOnlyDictionary<Guid, PartyLinkUrls>> GetActivePartyUrlsAsync(
        Guid ownerUserId, IReadOnlyCollection<Guid> albumIds,
        CancellationToken cancellationToken = default);

    // Validates a public VIEW token: returns the owner+album it unlocks when the
    // link is enabled, not revoked, not expired, AND its album still belongs to
    // the owner and is ShowOnTv. Null otherwise (generic 404 upstream).
    Task<PartyAccess?> ResolvePublicAsync(
        string token, CancellationToken cancellationToken = default);

    // Validates a public UPLOAD token: like ResolvePublicAsync but matches the
    // separate upload-token hash and additionally requires the upload sub-switch
    // to be on. A view token can never satisfy this (different hash), and vice
    // versa. Null otherwise (generic 404 upstream).
    Task<PartyAccess?> ResolveUploadAsync(
        string uploadToken, CancellationToken cancellationToken = default);
}
