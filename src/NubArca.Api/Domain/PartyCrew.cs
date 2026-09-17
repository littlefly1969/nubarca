namespace NubArca.Api.Domain;

/// <summary>
/// A collaborator on ONE party, who has no NubArca account and never gets one.
///
/// <para><b>This is not a user, and the distinction is the whole design.</b> A
/// <see cref="User"/> holds a global role whose permissions reach the media
/// library, People, the Private Vault, the albums and the administration. A
/// party's co-organizer needs none of that and must never acquire it, so they
/// are not a user with a narrow role — they are an identity that exists only
/// inside one party and cannot be named anywhere else:</para>
///
/// <code>
/// USER  ──► ROLE ──► GLOBAL PERMISSIONS        (PermissionCatalog)
/// PARTY ──► COLLABORATOR ──► PARTY CAPABILITIES (PartyCrewCapabilities)
/// </code>
///
/// <para>The two vocabularies are deliberately disjoint. A Party Crew
/// capability is never added to <c>PermissionCatalog</c>, a collaborator never
/// appears in <c>AccessRole</c>/<c>RolePermission</c>, and nothing here can be
/// granted to a user or vice versa. There is no row that could accidentally be
/// read as both.</para>
///
/// <para><b>The owner stays the owner.</b> <c>Party.OwnerUserId</c> is
/// unchanged and unchangeable from here: creating the party, choosing its main
/// media source, duplicating it, tearing it down and managing collaborators are
/// owner-only for the life of the party. A collaborator operates the evening;
/// they do not own it.</para>
///
/// <para><b>Email is identity verification, not authorization.</b> The address
/// is what the second factor is sent to, chosen by the owner and never by the
/// person answering. It is not a login and is never the authorizing key: the
/// authority is always the device grant resolved from a cookie. Changing it
/// revokes every device (see <c>PartyCrewService</c>), because a device
/// verified against an address the owner has replaced was verified against
/// somebody who may no longer be the intended person.</para>
/// </summary>
public class PartyCollaborator
{
    public Guid Id { get; set; }

    /// The one party this identity exists inside. There is no cross-party
    /// collaborator: the same person helping at two parties is two rows.
    public Guid PartyId { get; set; }

    public string DisplayName { get; set; } = string.Empty;

    /// Where the second factor goes. Owner-private: never in a guest API, never
    /// on the TV, never on the public party page, never in a log. The pairing
    /// surface shows a masked form only.
    public string Email { get; set; } = string.Empty;

    /// Folded for the uniqueness rule and for lookup. Never displayed.
    public string NormalizedEmail { get; set; } = string.Empty;

    /// One of <see cref="PartyCrewRoles"/>. The role decides the capability
    /// preset; there is deliberately no per-collaborator ACL editor.
    public string RoleKey { get; set; } = string.Empty;

    /// Moves on every security-relevant change — the email, the role, a revoke —
    /// so a stale owner form cannot overwrite a decision it never saw.
    public int Version { get; set; } = 1;

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// Revoked collaborators are kept, not deleted: the audit trail names them,
    /// and a deleted row would leave entries pointing at nothing.
    public DateTime? RevokedAt { get; set; }
}

/// <summary>
/// One capability a collaborator holds on their party.
///
/// <para>Persisted rather than derived at read time, so what a collaborator may
/// do is a fact in the database that an audit can be read against — and so a
/// role preset that changes in a later release does not silently re-grant
/// authority to people paired under the old one. Changing a role replaces the
/// whole set atomically; nothing edits a single row.</para>
///
/// <para>The vocabulary is server-authoritative (<see cref="PartyCrewCapabilities"/>).
/// A client cannot invent a capability, and an unknown key is refused at the
/// database by a check constraint as well as by the service.</para>
/// </summary>
public class PartyCollaboratorGrant
{
    public Guid Id { get; set; }
    public Guid PartyCollaboratorId { get; set; }
    public string CapabilityKey { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// The FIRST factor: a personal link that says which collaborator is pairing.
///
/// <para>On its own it authorizes nothing at all. It names a collaborator and
/// starts a challenge; the challenge is what sends the second factor, and only
/// both together produce a device. That is the point of splitting them — a
/// forwarded link, a link in someone's browser history, a link in a screenshot
/// is not access.</para>
///
/// <para>The raw token is CSPRNG, 256 bits, returned exactly once and never
/// stored: the database holds <c>SHA-256(raw)</c>. Minting a new invite revokes
/// every earlier usable one for that collaborator, so "send a new link" means
/// the previous link stops working rather than two links existing.</para>
/// </summary>
public class PartyCollaboratorInvite
{
    public Guid Id { get; set; }
    public Guid PartyCollaboratorId { get; set; }

    /// SHA-256 hex of the raw token. 64 characters, enforced by the database.
    public string TokenHash { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }

    /// Set when a pairing completed off this invite. A consumed invite cannot
    /// start a second challenge.
    public DateTime? ConsumedAt { get; set; }

    public DateTime? RevokedAt { get; set; }
}

/// <summary>
/// One attempt to pair one device: the link presented, the code sent, and what
/// came back.
///
/// <para>It is the join between the two factors, and it holds the state that
/// makes the device limit survivable. A challenge whose OTP has been verified
/// but which could not create a device — because the collaborator already has
/// two — stays <see cref="VerifiedAt"/> without <see cref="CompletedAt"/>, and
/// that intermediate state is exactly what lets the person free a slot and
/// finish WITHOUT typing a second code. Re-sending an OTP because the product
/// could not count to two would be the product's failure charged to them.</para>
///
/// <para>The challenge token is CSPRNG, 256 bits, stored only as its hash, and
/// carried in a short-lived path-scoped HttpOnly cookie. It is what binds the
/// code to the BROWSER that asked for it: knowing the six digits from someone's
/// inbox is not enough, because the code alone opens nothing.</para>
/// </summary>
public class PartyCollaboratorAuthChallenge
{
    public Guid Id { get; set; }
    public Guid PartyCollaboratorId { get; set; }
    public Guid PartyCollaboratorInviteId { get; set; }

    /// SHA-256 hex of the raw challenge token held in the browser's cookie.
    public string ChallengeTokenHash { get; set; } = string.Empty;

    /// <summary>
    /// The proof of the current code — <c>HMAC-SHA256(secret, id ‖ otp)</c>, not
    /// a bare hash.
    ///
    /// <para>A six-digit code has a million possibilities: <c>SHA-256(otp)</c>
    /// is a lookup table, and storing one would mean a database read yields
    /// every live code. The keyed construction makes the stored value useless
    /// without the server's secret.</para>
    /// </summary>
    public string OtpProof { get; set; } = string.Empty;

    /// <summary>
    /// Which code this is, counting from one.
    ///
    /// <para>Bound into the proof, so a code is valid for the generation it was
    /// minted for and no other. Replacing the proof on a resend already stops
    /// the previous code matching; carrying the generation means a proof cannot
    /// be replayed across generations even if one were somehow recovered, and
    /// it gives the audit a number to talk about that is not the code.</para>
    /// </summary>
    public int OtpGeneration { get; set; } = 1;

    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }

    /// When the current code was sent. A resend moves it, and rate limiting
    /// reads it.
    public DateTime OtpSentAt { get; set; }

    /// <summary>
    /// How many codes this challenge has actually put in somebody's inbox.
    ///
    /// <para>The interval between resends is not a budget: sixty seconds apart,
    /// for the ten minutes a challenge lives, is ten emails from one link. This
    /// is the budget, and it is per CHALLENGE rather than per address, so a
    /// person who genuinely needs a second code gets one and a script holding a
    /// leaked link cannot turn a mailbox into a target.</para>
    ///
    /// <para>Counted only on sends the mail subsystem ACCEPTED: a refused
    /// delivery costs the person nothing.</para>
    /// </summary>
    public int OtpSendCount { get; set; } = 1;

    /// Wrong codes, counted atomically. Past the limit the challenge is spent.
    public int FailedAttempts { get; set; }

    public DateTime? LastAttemptAt { get; set; }

    /// The second factor was satisfied. From here the challenge may list and
    /// revoke this collaborator's devices, and complete.
    public DateTime? VerifiedAt { get; set; }

    /// A device was created off this challenge. It is finished.
    public DateTime? CompletedAt { get; set; }

    public DateTime? RevokedAt { get; set; }
}

/// <summary>
/// One browser that has completed a pairing, identified by a cookie.
///
/// <para>The device is the CREDENTIAL and is party-agnostic: the same phone
/// helping at two parties is one device with two grants. That is why the
/// two-device limit is counted on <see cref="PartyCollaboratorDeviceGrant"/>
/// and not here — a person is allowed as many parties as they are invited to,
/// and two devices at each.</para>
///
/// <para>Token: CSPRNG, 256 bits, returned once in a path-scoped HttpOnly
/// cookie, stored only as its SHA-256. Never in a response body, never in a
/// URL, never in a log.</para>
/// </summary>
public class PartyCrewDevice
{
    public Guid Id { get; set; }

    /// SHA-256 hex of the raw device token held in the cookie.
    public string TokenHash { get; set; } = string.Empty;

    /// Something a person can recognise their own phone by in a list. Derived
    /// from the user agent, never a fingerprint: no canvas, no fonts, no
    /// resolution, nothing that identifies a browser across sites.
    public string? DeviceLabel { get; set; }

    public string? UserAgent { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime LastSeenAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? RevokedAt { get; set; }
}

/// <summary>
/// The authority itself: THIS device may act as THIS collaborator.
///
/// <para>Every Party Crew request resolves to one of these rows and re-reads
/// everything behind it — the device is live, the grant is not revoked, the
/// collaborator is not revoked, the party still exists, the capability is in
/// the collaborator's set, and the OWNER still holds the matching global
/// permission. Nothing is cached into the cookie, so revoking anything takes
/// effect on the next request rather than at an expiry.</para>
///
/// <para><b>At most two unrevoked grants per collaborator</b>, and the limit is
/// serialised in the database rather than checked and hoped for: two devices
/// finishing their codes in the same second must not both be admitted. See
/// <c>PartyCrewAuthService.CompleteAsync</c>.</para>
/// </summary>
public class PartyCollaboratorDeviceGrant
{
    public Guid Id { get; set; }
    public Guid PartyCrewDeviceId { get; set; }
    public Guid PartyCollaboratorId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LastUsedAt { get; set; }
    public DateTime? RevokedAt { get; set; }
}
