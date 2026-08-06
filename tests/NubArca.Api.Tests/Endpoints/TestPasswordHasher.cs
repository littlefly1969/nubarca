using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;

namespace NubArca.Api.Tests.Endpoints;

/// <summary>
/// Fast, deterministic password hasher for HTTP/integration tests.
///
/// The endpoint suite verifies authentication and authorization behavior, not
/// PBKDF2's implementation. Running production-strength PBKDF2 for every seeded
/// user made those tests CPU-bound. Focused AuthService tests still instantiate
/// ASP.NET Core's real PasswordHasher directly and cover the production seam.
/// </summary>
internal sealed class TestPasswordHasher<TUser> : IPasswordHasher<TUser>
    where TUser : class
{
    private const string Prefix = "nubarca-test-sha256$";

    public string HashPassword(TUser user, string password)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(password);

        var payload = Encoding.UTF8.GetBytes($"{typeof(TUser).FullName}\0{password}");
        return Prefix + Convert.ToHexStringLower(SHA256.HashData(payload));
    }

    public PasswordVerificationResult VerifyHashedPassword(
        TUser user,
        string hashedPassword,
        string providedPassword)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(hashedPassword);
        ArgumentNullException.ThrowIfNull(providedPassword);

        var expected = Encoding.UTF8.GetBytes(HashPassword(user, providedPassword));
        var actual = Encoding.UTF8.GetBytes(hashedPassword);

        return expected.Length == actual.Length
            && CryptographicOperations.FixedTimeEquals(expected, actual)
                ? PasswordVerificationResult.Success
                : PasswordVerificationResult.Failed;
    }
}
