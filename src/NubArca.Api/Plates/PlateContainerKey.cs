using System.Security.Cryptography;
using System.Text;

namespace NubArca.Api.Plates;

// Computes the hidden, owner-scoped logical container key for a user's Plates
// area: __nubarca_plates_{ownerScopedHash}.
//
// ownerScopedHash is an HMAC-SHA256 of the owner id (with a versioned scheme
// prefix) under a configured pepper, rendered lower-hex. Using an HMAC (not a
// bare hash of the id) means the key is:
//   * deterministic per owner (same owner → same key);
//   * non-reversible (cannot recover the owner id from the key);
//   * not usable to infer the user id (the pepper gates any brute force);
//   * internal only — it is stored on PlateImage.LogicalContainerKey but NEVER
//     returned through any API/DTO/log.
// The scheme is versioned so the pepper/algorithm can be rotated later.
//
// Mirrors Files/ContentFingerprint, the established keyed-hash pattern.
public static class PlateContainerKey
{
    // PERSISTED in PlateImage.LogicalContainerKey. The prefix is concatenated,
    // never hashed, so migration RenameLogicalContainerKeyPrefixes rewrites it in
    // place without touching the hash body. Nothing queries by this column — it
    // is an opaque internal grouping label — so the rewrite is purely cosmetic
    // continuity, not a lookup contract.
    public const string Prefix = "__nubarca_plates_";
    public const string Scheme = "plates:v1:";

    // Fixed fallback used ONLY when no pepper is configured, so dev/test run
    // without setup. Production SHOULD configure a real secret via Plates__Pepper.
    //
    // This literal is HMAC key material: changing it changes every key derived
    // WITHOUT a configured pepper. An installation that relied on the fallback
    // and wants its existing owner grouping preserved sets Plates__Pepper to the
    // value it was previously deriving from, which keeps the value out of source.
    private const string DevelopmentFallbackPepper = "nubarca-plates-dev-pepper-v1";

    public static string Compute(string? pepper, Guid ownerUserId)
    {
        var key = Encoding.UTF8.GetBytes(
            string.IsNullOrEmpty(pepper) ? DevelopmentFallbackPepper : pepper);
        var message = Encoding.UTF8.GetBytes(Scheme + ownerUserId.ToString("N"));
        var mac = HMACSHA256.HashData(key, message);
        return Prefix + Convert.ToHexStringLower(mac);
    }
}
