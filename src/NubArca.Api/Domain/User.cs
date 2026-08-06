namespace NubArca.Api.Domain;

public class User
{
    public Guid Id { get; set; }
    public string Email { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? PasswordHash { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? DisabledAt { get; set; }
    // Minimal operator marker for slice 46. Only gates `/api/admin/*` today;
    // not a general RBAC concept. False for every existing user (defaulted
    // by the AddUserIsAdmin migration).
    public bool IsAdmin { get; set; }

    // Persisted UI language preference. One of the codes in UiLanguages.All
    // ("it" | "en"); Italian is the canonical default (backfilled by the
    // AddUserUiLanguage migration). Only ever set to a validated supported
    // code — never an arbitrary browser locale string.
    public string UiLanguage { get; set; } = UiLanguages.Default;
}
