namespace NubArca.Api.Albums.Sharing;

// SHARE-ALBUM-01: the SINGLE decision point for "may this authenticated user act
// on this album, and with what authority".
//
// Nothing outside this resolver is allowed to decide that a non-owner may see an
// album's media. In particular the ordinary owner-only endpoints under
// /api/files/{id}/* keep their unchanged `OwnerUserId == caller` checks — shared
// access is a SEPARATE route family (/api/shared-albums/...) that resolves a
// grant here first and only then calls the existing owner-scoped services with
// the ALBUM OWNER's id. That is the same shape the public Party surface already
// uses, and it means a bug in sharing can never widen a private endpoint.
public interface IAlbumAccessResolver
{
    // Resolves the caller's authority over one album. Null when the album is
    // missing, the caller is neither its owner nor an active accepted member, or
    // either account is unavailable — every one of those collapses to the same
    // null so the caller cannot distinguish "no such album" from "not shared
    // with you".
    Task<AlbumAccessGrant?> ResolveAsync(
        Guid albumId, Guid actorUserId, CancellationToken cancellationToken = default);

    // Resolves the caller's authority over ONE media item AS A MEMBER OF THIS
    // ALBUM. Knowing a FileItemId is never sufficient: the item must currently be
    // a member of this album and currently be servable from the album owner's
    // library. Null for every failure, again indistinguishably.
    Task<SharedMediaGrant?> ResolveMediaAsync(
        Guid albumId, Guid actorUserId, Guid fileItemId, SharedMediaAccess access,
        CancellationToken cancellationToken = default);
}

// What the caller wants to do with the media bytes. `Original` additionally
// requires the grant's per-member download permission; `Derived` does not,
// because a thumbnail/preview/poster/playback rendition is what "viewing" is.
public enum SharedMediaAccess
{
    Derived,
    Original,
}

// A resolved authority over one album. `Role` is AlbumRoles.Viewer/... for a
// member, or AlbumAccessGrant.OwnerRole for the album owner.
//
// The owner's grant is synthesised from the Album row alone and never reads
// album_memberships, so the owner stays authoritative even if membership data is
// missing or inconsistent.
public sealed record AlbumAccessGrant(
    Guid AlbumId,
    Guid AlbumOwnerUserId,
    Guid ActorUserId,
    string Role,
    bool AllowOriginalDownload,
    // Null for the owner. The membership's OWN id — never the member's user id,
    // so an owner-facing member list can address a row without learning another
    // user's internal account identifier.
    Guid? MembershipId)
{
    // Sentinel role for the album owner. Deliberately NOT a member of
    // AlbumRoles.All: "owner" must be unrepresentable as a membership write.
    public const string OwnerRole = "owner";

    public bool IsOwner => MembershipId is null;
}

// A resolved authority over one media item within one album.
public sealed record SharedMediaGrant(
    Guid AlbumId,
    // Whose library the bytes come from. This is the id handed to the unchanged
    // owner-scoped services (IFileThumbnailService, VideoHlsServingService,
    // IFileItemService) — never the caller's id.
    Guid MediaOwnerUserId,
    Guid FileItemId,
    SharedMediaKind Kind);

public enum SharedMediaKind
{
    Image,
    Video,
}
