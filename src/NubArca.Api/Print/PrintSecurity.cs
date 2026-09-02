using System.Security.Cryptography;
using System.Text;

namespace NubArca.Api.Print;

internal static class PrintSecurity
{
    public static string NewToken(int bytes = 32) =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string Digest(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    public static bool FixedTimeEquals(string expectedHex, string raw)
    {
        try
        {
            return CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(expectedHex),
                SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
