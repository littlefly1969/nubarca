namespace NubArca.Api.Users;

// Shared minimum password policy for admin-set and self-service password
// changes. Deliberately simple: length bounds + not-all-whitespace. No
// character-class complexity rules — the project has none today and this
// slice does not introduce them.
public static class PasswordPolicy
{
    public const int MinLength = 10;
    public const int MaxLength = 256;

    public const string ErrorMessage = "Password does not meet the minimum requirements.";

    public static bool IsValid(string? password) => TryValidate(password, out _);

    public static bool TryValidate(string? password, out string? error)
    {
        if (string.IsNullOrEmpty(password)
            || password.Trim().Length == 0
            || password.Length < MinLength
            || password.Length > MaxLength)
        {
            error = ErrorMessage;
            return false;
        }

        error = null;
        return true;
    }
}
