namespace NubArca.Api.Domain;

// Owner + album scoped PUBLIC read-only "party" access link. When Enabled (and
// not revoked/expired) an unauthenticated visitor holding the link's token may
// view party-safe DERIVED media (metadata-stripped thumbnails/previews) of the
// album — never originals, never metadata, never owner/AI/face internals.
//
// SECURITY: only the token HASH is stored (TokenHash). The raw token is NEVER
// persisted. The raw token is a deterministic, high-entropy value derived on
// demand via HMAC-SHA256(serverSecret, Id) so an owner-authorized surface (the
// owner settings API and the owner's own paired TV) can re-render the QR
// without the server ever holding the raw secret at rest. Re-enabling party
// mode creates a NEW row (new Id → new token) so a previously-shared QR/link
// stops working (see PartyLinkService.EnableAsync).
public class PartyAlbumLink
{
    public Guid Id { get; set; }
    public Guid OwnerUserId { get; set; }
    public Guid AlbumId { get; set; }

    // SHA-256 hex of the derived public VIEW/download token. Raw token never stored.
    public string TokenHash { get; set; } = string.Empty;

    // SHA-256 hex of the derived public UPLOAD token — a SEPARATE token from the
    // view token so upload can be controlled/revoked independently. Nullable so
    // the additive migration needs no backfill; new links always set it. Raw
    // token never stored.
    public string? UploadTokenHash { get; set; }

    // The party-mode on/off state for this link (the master switch). A disabled
    // or revoked link is rejected on every public request (live revocation).
    public bool Enabled { get; set; }

    // Sub-switch: whether anonymous UPLOAD is currently accepted for this link.
    // Only meaningful while Enabled; disabling the party master kills upload too.
    public bool UploadEnabled { get; set; }

    // When true, new anonymous uploads land as PENDING (PartyUploadItem) and are
    // NOT visible on the public party page / TV until the owner approves them.
    // Default false: uploads stay immediately visible, matching the low-friction
    // party behavior. Never rotates the view/upload tokens when toggled.
    public bool RequireUploadApproval { get; set; }

    // When true, a new guest MESSAGE lands as pending and is not projected to
    // the TV until a manager approves it. Deliberately independent of
    // RequireUploadApproval: a host may well want every photo through instantly
    // and every written greeting read first, or the reverse. Changing it
    // governs NEW submissions only — turning approval off does not publish the
    // backlog somebody already declined to approve.
    public bool RequireMessageApproval { get; set; }

    // --- Slideshow timing (owner-configurable, TV-facing) ---
    // How long a PHOTO holds the party slideshow, in seconds. The TV reads these
    // through its album-items context; changing either takes effect on the TV's
    // next poll WITHOUT rotating a token or restarting the slideshow.
    public int PhotoSlideSeconds { get; set; } = PartySlideshowDefaults.PhotoSeconds;

    // The MOST a single video may monopolise the party slideshow, in seconds.
    // This bounds the SLIDESHOW, never the file: the stored video is untouched
    // and plays in full anywhere else. A video shorter than the cap advances on
    // its natural end.
    public int MaxVideoSlideSeconds { get; set; } = PartySlideshowDefaults.MaxVideoSeconds;

    // --- Per-participant upload quotas ---
    // Maximum media of each kind ONE participant may contribute through this
    // link. 0 means unlimited, which is the default and the historical
    // behaviour. Photo and video quotas are independent: exhausting one never
    // blocks the other. Lowering a quota below what a participant has already
    // used deletes nothing — it simply leaves them no remaining slots.
    public int MaxPhotoUploadsPerParticipant { get; set; }
    public int MaxVideoUploadsPerParticipant { get; set; }

    public bool GameEnabled { get; set; }
    public int MinChallengeIntervalSeconds { get; set; } = PartyChallengeDefaults.MinIntervalSeconds;
    public int MaxChallengeIntervalSeconds { get; set; } = PartyChallengeDefaults.MaxIntervalSeconds;
    public int VotesPerGuest { get; set; } = PartyChallengeDefaults.VotesPerGuest;
    public int? MaxChallengesPerSession { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    // Set when party mode is turned off (or superseded by a re-enable). A
    // revoked link never grants access again.
    public DateTime? RevokedAt { get; set; }

    // Optional hard expiry. Null in the basic slice (no expiry).
    public DateTime? ExpiresAt { get; set; }

    // Who enabled it (owner or, in future, a delegated user). Owner-scoped.
    public Guid? CreatedByUserId { get; set; }
}

public static class PartyChallengeDefaults
{
    public const int MinIntervalSeconds = 300;
    public const int MaxIntervalSeconds = 540;
    public const int VotesPerGuest = 3;
    public const int MinAllowedIntervalSeconds = 30;
    public const int MaxAllowedIntervalSeconds = 86_400;
    public const int MinVotesPerGuest = 1;
    public const int MaxVotesPerGuest = 20;
    public const int MaxMaxChallengesPerSession = 100;

    public static bool IsValid(int min, int max, int votes, int? sessionMax) =>
        min >= MinAllowedIntervalSeconds && max <= MaxAllowedIntervalSeconds
        && max >= min && votes is >= MinVotesPerGuest and <= MaxVotesPerGuest
        && (sessionMax is null || sessionMax is >= 1 and <= MaxMaxChallengesPerSession);
}

// Defaults and validated ranges for the owner-configurable party slideshow and
// quota settings. Kept beside the entity so the API validator, the owner UI
// contract and the TV timing all quote ONE set of numbers.
public static class PartySlideshowDefaults
{
    public const int PhotoSeconds = 9;
    public const int MinPhotoSeconds = 3;
    public const int MaxPhotoSeconds = 60;

    public const int MaxVideoSeconds = 60;
    public const int MinMaxVideoSeconds = 5;
    public const int MaxMaxVideoSeconds = 600;

    // 0 = unlimited on both quotas; the upper bound only stops absurd input.
    public const int MinQuota = 0;
    public const int MaxQuota = 10_000;

    public static bool IsValidPhotoSeconds(int value)
        => value >= MinPhotoSeconds && value <= MaxPhotoSeconds;

    public static bool IsValidMaxVideoSeconds(int value)
        => value >= MinMaxVideoSeconds && value <= MaxMaxVideoSeconds;

    public static bool IsValidQuota(int value)
        => value >= MinQuota && value <= MaxQuota;
}
