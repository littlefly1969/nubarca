using System.Security.Cryptography;
using System.Text;

namespace NubArca.Api.Files;

// Computes the opaque, keyed content fingerprint stored in the deleted-content
// ledger and matched during import. It is an HMAC-SHA256 of the blob's SHA-256
// content hash (lower-hex) under a configured pepper, rendered as lower-hex.
//
// Using an HMAC (not the bare SHA) means the persisted value is not itself a
// content hash: it never leaks the SHA, and without the pepper a leaked ledger
// cannot be brute-force-matched against known files. The scheme is versioned so
// the pepper/algorithm can be rotated later (a new scheme lives beside the old).
public static class ContentFingerprint
{
    public const string Scheme = "hmac-sha256-v1";

    // A fixed fallback used ONLY when no pepper is configured, so dev/test run
    // without extra setup. Production SHOULD configure a real secret pepper.
    // This literal is HMAC key material behind the persisted deleted-content
    // ledger: changing it invalidates every fingerprint stored WITHOUT a
    // configured pepper. Production configures DeletedContent__Pepper, so this
    // fallback is a dev/test convenience there rather than live key material.
    private const string DevelopmentFallbackPepper = "nubarca-deleted-content-dev-pepper-v1";

    // Computes the fingerprint for a blob's content hash (the SHA-256 lower-hex
    // as stored on BlobObject.Sha256). Never returns or logs the input SHA.
    public static string Compute(string? pepper, string sha256Hex)
    {
        if (string.IsNullOrEmpty(sha256Hex))
        {
            throw new ArgumentException("A content hash is required.", nameof(sha256Hex));
        }

        var key = Encoding.UTF8.GetBytes(
            string.IsNullOrEmpty(pepper) ? DevelopmentFallbackPepper : pepper);
        // Normalize the input so casing never affects the fingerprint.
        var message = Encoding.UTF8.GetBytes(sha256Hex.ToLowerInvariant());
        var mac = HMACSHA256.HashData(key, message);
        return Convert.ToHexStringLower(mac);
    }
}
