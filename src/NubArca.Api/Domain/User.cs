using NubArca.Api.Access;

namespace NubArca.Api.Domain;

public class User
{
    public Guid Id { get; set; }
    public string Email { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;

    // Optional given/family name. Nullable because every account that existed
    // before this slice has neither, and a display name is not reliably
    // splittable into the two.
    public string? FirstName { get; set; }
    public string? LastName { get; set; }

    public string? PasswordHash { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? DisabledAt { get; set; }

    // The authoritative authorization source, and a real foreign key into
    // access_roles. A user holds exactly ONE role and the role owns its
    // permissions: there is no per-user exception anywhere, so this column plus
    // that role's rows is the complete answer to "what may this person do".
    public string RoleKey { get; set; } = RoleKeys.Member;

    // Persisted UI language preference. One of the codes in UiLanguages.All
    // ("it" | "en"); Italian is the canonical default (backfilled by the
    // AddUserUiLanguage migration). Only ever set to a validated supported
    // code — never an arbitrary browser locale string.
    public string UiLanguage { get; set; } = UiLanguages.Default;

    // IANA time zone id ("Europe/Rome"). Nullable: unset means "use the
    // browser's". Validated against TimeZoneInfo before it is written, so the
    // column only ever holds an id this runtime can actually resolve.
    public string? TimeZone { get; set; }

    // Stamped by an interactive login only. A background request, a token
    // refresh or a successful password-reset validation must never move it —
    // "when did this person last sign in" has to keep meaning that.
    public DateTime? LastLoginAt { get; set; }

    // When the credential last changed (self-service change, admin reset, or a
    // completed recovery). Null for an account whose password was never set.
    public DateTime? PasswordChangedAt { get; set; }

    // Bumped by every credential-security event. The value is written into the
    // auth cookie at sign-in and re-checked on every request, so changing a
    // password invalidates sessions that were opened with the old one — the
    // browser on the other side of a compromise stops working immediately
    // rather than at the cookie's fourteen-day expiry.
    public int SecurityVersion { get; set; } = 1;
}
