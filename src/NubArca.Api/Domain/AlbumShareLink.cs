namespace NubArca.Api.Domain;

/// <summary>
/// A PUBLIC CAPABILITY over one album, held by whoever has the link.
///
/// <para>Deliberately NOT <c>AlbumMember</c>, which is the other kind of
/// sharing this product has: a member is an account the owner invited by
/// address, who signs in and holds a role. This is the opposite shape — nobody
/// signs in, nothing is invited, and the only thing the holder has is a string.
/// The two never meet, and neither one is the other's fallback.</para>
///
/// <para>Deliberately NOT a <c>PartyAlbumLink</c> either, although it is built
/// on the same idea. A party link belongs to an EVENING: it has phases, a
/// television, guests who arrive, contributions that are moderated. This
/// belongs to an ALBUM, which has none of those and outlives all of them. An
/// album may have a party, may have had one, or may never have one, and none of
/// those change what this link does — which is why it carries its own token and
/// its own routes rather than borrowing the party's.</para>
///
/// <para>The raw token is NEVER stored. It is derived from the link id under a
/// purpose-bound key, so the owner can be shown it again without the database
/// ever holding anything that opens the album. The purpose string is what makes
/// a party token and an album token live in different spaces: not a check that
/// could be forgotten, but two values that cannot collide.</para>
/// </summary>
public sealed class AlbumShareLink
{
    public Guid Id { get; set; }

    public Guid AlbumId { get; set; }

    /// <summary>
    /// The album's owner AT THE TIME the link was made, kept beside the album id
    /// so the resolver can refuse a link whose album has since changed hands
    /// without a second query.
    /// </summary>
    public Guid OwnerUserId { get; set; }

    /// <summary>SHA-256 hex of the derived token. The raw value is never stored.</summary>
    public string TokenHash { get; set; } = string.Empty;

    /// <summary>
    /// Whether the link opens anything at all. Revoking sets this false and is
    /// checked on EVERY public request, so a revoke takes effect on the next
    /// tap rather than on the next deploy.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// The SECOND SWITCH. Reading is what the link is for and is always on;
    /// contributing is a separate decision the owner makes and unmakes without
    /// invalidating a link they have already sent to thirty people.
    /// </summary>
    public bool UploadEnabled { get; set; } = true;

    /// <summary>
    /// Whether a visitor may take the ORIGINAL file rather than a derived,
    /// metadata-stripped rendition.
    ///
    /// <para>Off by default, and the default matters: an original carries the
    /// EXIF the camera wrote, which means GPS. A link is a public share, and
    /// this product's rule is that GPS never reaches one unless somebody
    /// designed it to. So this is that design — an explicit switch, owned by
    /// the owner, defaulting to the safe answer.</para>
    ///
    /// <para>It is deliberately the SAME question, with the same name, that
    /// <c>AlbumMember.AllowOriginalDownload</c> already asks about an invited
    /// account. One product should not have two different answers to "may this
    /// person have the real file".</para>
    /// </summary>
    public bool AllowOriginalDownload { get; set; }

    /// <summary>
    /// Whether opening the link additionally requires a one-time code sent to
    /// an address the owner listed. Off is the ordinary case: a link somebody
    /// forwards to a cousin should work for the cousin.
    /// </summary>
    public bool RequireSecondFactor { get; set; }

    /// <summary>
    /// The owner's own name for this link — "per i parenti", "fotografo". An
    /// owner with three links has to be able to tell which one they are about
    /// to revoke, and a token prefix is not a name.
    /// </summary>
    public string? Label { get; set; }

    /// <summary>
    /// How many files this link may still accept, as a TOTAL over its life.
    ///
    /// <para>There is deliberately no per-person quota: a share has no people
    /// in it. It has a link, and anybody holding the link is the same anonymous
    /// caller, so the only honest subject for a limit is the link itself. This
    /// is not a fairness rule — it is the brake that stops a forwarded link
    /// from filling somebody's disk.</para>
    ///
    /// <para>0 means no ceiling. Reaching the ceiling REFUSES the upload with a
    /// stable code and leaves reading open: a link that quietly switched itself
    /// off is one the owner would learn about from complaints.</para>
    /// </summary>
    public int MaxUploads { get; set; } = AlbumShareLimits.DefaultMaxUploads;

    /// <summary>
    /// What has arrived through this link. Incremented in the same statement
    /// that claims a slot, so two phones finishing at once cannot both take the
    /// last one.
    /// </summary>
    public int UploadCount { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>When the owner revoked it. Set together with <see cref="Enabled"/>.</summary>
    public DateTime? RevokedAt { get; set; }

    /// <summary>Null means "until revoked", which is what most shares are.</summary>
    public DateTime? ExpiresAt { get; set; }

    public Guid? CreatedByUserId { get; set; }
}

/// <summary>
/// One address the owner will let through a second-factor share.
///
/// <para>The list is what makes the second factor AUTHORIZATION rather than
/// theatre: a code sent to whatever address the visitor types proves they own
/// an inbox and nothing else, and a forwarded link would still open. It is
/// owner-private, and the public challenge endpoint answers identically whether
/// or not an address is on it — otherwise the link becomes a way to discover
/// who the owner invited.</para>
/// </summary>
public sealed class AlbumShareGuest
{
    public Guid Id { get; set; }
    public Guid AlbumShareLinkId { get; set; }

    /// <summary>Normalized and lower-cased, because a list is only a list if it matches.</summary>
    public string Email { get; set; } = string.Empty;

    public string? DisplayName { get; set; }

    public DateTime CreatedAt { get; set; }

    /// <summary>Revoking one address closes it without touching the others.</summary>
    public DateTime? RevokedAt { get; set; }
}

/// <summary>
/// A one-time code in flight, for a second-factor share.
///
/// <para>The proof is a KEYED MAC and not a hash, for the reason Party Crew
/// discovered first: six digits is a million possibilities, and a table of
/// <c>SHA-256(482117)</c> is something a laptop builds in a second. A database
/// dump must not yield live codes.</para>
///
/// <para><see cref="Generation"/> is what makes a resend safe: the new code
/// bumps it, and the MAC covers it, so the code from the previous email stops
/// verifying the moment a second one is sent.</para>
/// </summary>
public sealed class AlbumShareChallenge
{
    public Guid Id { get; set; }
    public Guid AlbumShareLinkId { get; set; }
    public Guid AlbumShareGuestId { get; set; }

    /// <summary>HMAC-SHA256(secret, challengeId ‖ purpose ‖ generation ‖ code).</summary>
    public string OtpProof { get; set; } = string.Empty;

    public int Generation { get; set; } = 1;
    public int Attempts { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime? LastSentAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? VerifiedAt { get; set; }
}

/// <summary>
/// The proof a verified visitor carries afterwards, in an HTTP-only cookie.
///
/// <para>Without it a second-factor share would ask for a code on every page
/// load, which makes uploading twenty photographs over an afternoon impossible.
/// 256 bits of CSPRNG, stored only as SHA-256 — a database read cannot
/// impersonate anybody, which is the property that makes an accountless
/// credential acceptable at all.</para>
/// </summary>
public sealed class AlbumShareDevice
{
    public Guid Id { get; set; }
    public Guid AlbumShareLinkId { get; set; }
    public Guid AlbumShareGuestId { get; set; }

    public string TokenHash { get; set; } = string.Empty;

    public string? UserAgent { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime LastSeenAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? RevokedAt { get; set; }
}

public static class AlbumShareLimits
{
    public const int MaxLabelLength = 60;
    public const int MaxEmailLength = 254;
    public const int MaxDisplayNameLength = 120;
    public const int MaxUserAgentLength = 512;

    /// <summary>
    /// The ceiling a new share starts with. Generous enough that an ordinary
    /// wedding never meets it, low enough that a link posted somewhere public
    /// stops before it matters. The owner raises it when they mean to.
    /// </summary>
    public const int DefaultMaxUploads = 2000;
    public const int MaxUploadsCeiling = 100_000;

    /// <summary>How many addresses one share may list. A guest list, not a mailing list.</summary>
    public const int MaxGuests = 50;

    public const int MaxOtpAttempts = 5;
    public static readonly TimeSpan ChallengeLifetime = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan ResendInterval = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan DeviceLifetime = TimeSpan.FromDays(90);

    public static bool IsValidMaxUploads(int value) =>
        value >= 0 && value <= MaxUploadsCeiling;
}

/// <summary>Why a public album-share request was refused, as stable wire codes.</summary>
public static class AlbumShareErrors
{
    /// The link is live but this caller has not proved they are on the list.
    public const string SecondFactorRequired = "second_factor_required";

    /// The owner has closed contribution on a link that still opens for reading.
    public const string UploadsDisabled = "uploads_disabled";

    /// The link's total ceiling is reached. The owner raises it; nothing switches off.
    public const string UploadLimitReached = "upload_limit_reached";

    /// <summary>
    /// The ONE refusal the second factor ever gives: wrong code, expired code,
    /// already-spent code, exhausted attempts and an unlisted address alike.
    ///
    /// <para>There used to be codes for "too many attempts" and "asked again
    /// too soon" and both were enumeration oracles — only a listed address can
    /// exhaust attempts or trip a cooldown, so either one answered "this
    /// address is on the owner's list". The distinctions live in the log now,
    /// where they help an operator and tell a caller nothing.</para>
    /// </summary>
    public const string InvalidCode = "invalid_code";
}
