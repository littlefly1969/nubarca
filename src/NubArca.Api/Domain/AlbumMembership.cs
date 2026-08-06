namespace NubArca.Api.Domain;

// SHARE-ALBUM-01: a LIVE album share between two authenticated NubArca users.
//
// An album always has exactly ONE owner (`Album.OwnerUserId`) and that owner is
// authoritative: no membership row is ever created for them, and their access
// does not depend on this table existing or being consistent. A membership row
// grants a DIFFERENT user bounded, revocable access to that one album — never to
// the owner's library, never to another album, and never to the owner's private
// semantic layer (People / faces / person names).
//
// ONE ROW PER (AlbumId, MemberUserId), enforced by a plain unique index so the
// uniqueness is concurrency-safe on both PostgreSQL and SQLite without a partial
// predicate. Re-inviting somebody who declined or was revoked REUSES their row
// (state back to pending, decision timestamps cleared) rather than accumulating
// rows — the history of who did what and when lives in the audit log, which is
// where a security question about it should be answered from.
//
// Access requires State == Accepted AND RevokedAt == null. Both are re-checked
// on EVERY request (see AlbumAccessResolver), so a revoke takes effect on the
// next call rather than at the next cache expiry.
public class AlbumMembership
{
    public Guid Id { get; set; }

    public Guid AlbumId { get; set; }

    // The invited user. Never the album owner (rejected by the service and by
    // ck_album_memberships_member_not_inviter for the self-invite case).
    public Guid MemberUserId { get; set; }

    // One of AlbumRoles (viewer | contributor | editor) — all three assignable
    // as of SHARE-ALBUM-03. The check constraint documents the closed domain;
    // "owner" is not in it, so ownership can never be granted by a role write.
    public string Role { get; set; } = AlbumRoles.Viewer;

    // One of AlbumMembershipStates (pending | accepted | declined | revoked).
    public string State { get; set; } = AlbumMembershipStates.Pending;

    // Per-member permission to fetch the ORIGINAL bytes of a shared item.
    // Default false: a share grants viewing, and downloading the untouched
    // original is a separate, explicit decision by the owner.
    public bool AllowOriginalDownload { get; set; }

    // Who created the invitation. Always the album owner in SHARE-ALBUM-01;
    // recorded separately from the album owner because SHARE-ALBUM-03 keeps the
    // distinction between actor and owner meaningful.
    public Guid InvitedByUserId { get; set; }

    public DateTime InvitedAt { get; set; }

    // Set when the RECIPIENT accepts. Null until then, and cleared on re-invite.
    public DateTime? AcceptedAt { get; set; }

    // Set when the RECIPIENT declines. Null otherwise.
    public DateTime? DeclinedAt { get; set; }

    // Set when the OWNER cancels a pending invitation or revokes an accepted
    // membership. A row with RevokedAt != null never grants access again unless
    // the owner explicitly re-invites (which clears it).
    public DateTime? RevokedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}

// The closed set of album roles. Owner is NOT a membership role — it is a
// property of the Album itself and is deliberately absent from this catalog so
// that "make somebody an owner" is unrepresentable as a membership write.
public static class AlbumRoles
{
    // Read the album and its media. The only role enabled in SHARE-ALBUM-01.
    public const string Viewer = "viewer";

    // Viewer + may contribute their OWN media as a linked, revocable
    // contribution. Enabled by SHARE-ALBUM-02.
    public const string Contributor = "contributor";

    // Contributor + may curate the album: title, description, cover, order, and
    // removing ANY item. Enabled by SHARE-ALBUM-03.
    //
    // Curation only. An Editor never governs ACCESS or LIFECYCLE — no invites,
    // no role changes, no revocation, no allowOriginalDownload, no deleting the
    // album, no ownership transfer, no public/Party/TV sharing. Those stay with
    // the single Owner, and the split is enforced by which service a route
    // reaches, not by a role check sprinkled at each call site.
    public const string Editor = "editor";

    public static readonly IReadOnlyList<string> All = [Viewer, Contributor, Editor];

    // Roles a client may request. All three are now assignable; "owner" is
    // deliberately absent from the catalog entirely, so making somebody an
    // owner stays unrepresentable as a membership write.
    public static readonly IReadOnlyList<string> Assignable = [Viewer, Contributor, Editor];

    public static bool IsKnown(string? role) => role is not null && All.Contains(role);

    public static bool IsAssignable(string? role) => role is not null && Assignable.Contains(role);

    // May a member holding this role ADD media to the album?
    //
    // Deliberately separate from the right to WITHDRAW: withdrawal follows from
    // owning the file and having contributed it, not from currently holding
    // Contributor. A member downgraded to Viewer keeps their contributions in
    // the album and keeps the right to take them back — see
    // IAlbumSharingService.WithdrawContributionAsync.
    public static bool CanContribute(string? role) =>
        role is Contributor or Editor;

    // May a member holding this role CURATE the album — title, description,
    // cover, order, and removing any item?
    //
    // Curation only: this predicate says nothing about governance, which is the
    // Owner's alone and is never expressed as a role check.
    public static bool CanEdit(string? role) => role is Editor;
}

public static class AlbumMembershipStates
{
    // Invited; the recipient has not answered. No access.
    public const string Pending = "pending";

    // The recipient accepted. Grants access while RevokedAt is null.
    public const string Accepted = "accepted";

    // The recipient declined. No access; the owner may re-invite.
    public const string Declined = "declined";

    // The owner cancelled the invitation or revoked the membership. No access;
    // the owner may re-invite.
    public const string Revoked = "revoked";

    public static readonly IReadOnlyList<string> All = [Pending, Accepted, Declined, Revoked];
}
