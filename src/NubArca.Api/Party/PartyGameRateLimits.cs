using System.Security.Cryptography;
using System.Text;
using NubArca.Api.Endpoints;

namespace NubArca.Api.Party;

/// <summary>
/// How the live Party Game is rate limited, and why it cannot be limited like
/// the rest of Party.
///
/// <para>Everything else public in Party is an occasional act: opening the hub,
/// uploading a photograph, writing a greeting. Those are limited per IP address,
/// and that is right for them. The game is CONTINUOUS — every phone and every
/// television on the party's snapshot polls every couple of seconds — and a
/// party is one Wi-Fi network, so per-IP limiting would make the room's own size
/// the thing that breaks it. Twenty guests on the house Wi-Fi are twenty people,
/// not one abusive client.</para>
///
/// <para>So the primary partition is the guest's own <b>server-issued</b>
/// participant token: the same HttpOnly cookie that already carries their
/// identity for quotas and votes. Each guest gets their own allowance, and the
/// room's size stops mattering.</para>
///
/// <para><b>What this bounds and what it does not.</b> The cookie is presented
/// by the client, so it is an identity claim rather than a proof. A caller that
/// invents a well-formed value gets its own partition — which is why the shape
/// check below exists (junk falls back to the address bucket), and why the
/// damage is bounded elsewhere rather than here: a minted participant may cast
/// exactly one vote per round, held by a unique index, so vote spam cannot move
/// a result; the address fallback still catches every cookie-less request; and
/// idle partitions are reclaimed by the runtime. This is a fairness mechanism
/// for a party, not an authentication boundary.</para>
///
/// <para>The partition key is a SHA-256 of the token. Nothing here is logged or
/// returned, and hashing means a partition key can never become a way to read a
/// guest's session back out of a diagnostic.</para>
/// </summary>
public static class PartyGameRateLimits
{
    /// Snapshot reads and the one-per-mount join. Polled continuously.
    public const string ReadPolicy = "party-game-read";

    /// Casting or changing one answer. A write, so tighter.
    public const string VotePolicy = "party-game-vote";

    // The stage and the guest page poll every 2.5s = 24 requests a minute. A
    // guest's allowance covers that plus a refresh burst and a second tab.
    public const int DefaultReadPermitsPerGuest = 60;

    // The fallback holds televisions, which never join, and first visits before
    // a cookie exists. Generous on purpose: 24 requests a minute each means this
    // accommodates well over thirty simultaneous displays on one address.
    public const int DefaultReadPermitsPerAddress = 900;

    // A guest changing their mind every two seconds for a whole minute.
    public const int DefaultVotePermitsPerGuest = 30;

    // First votes from a large room, before the cookie the response mints.
    public const int DefaultVotePermitsPerAddress = 240;

    public const int DefaultWindowSeconds = 60;

    private const string GuestPrefix = "party-game-guest:";
    private const string AddressPrefix = "party-game-address:";

    // 32 CSPRNG bytes as unpadded base64url — see PartyParticipantService.
    private const int TokenLength = 43;

    /// <summary>
    /// The bucket this request belongs to: the guest who sent a server-shaped
    /// participant token, or the address it came from.
    /// </summary>
    public static string Partition(HttpContext context)
    {
        var raw = context.Request.Cookies[PartyEndpoints.PartyParticipantCookieName];
        return LooksServerIssued(raw)
            ? GuestPrefix + Fingerprint(raw!)
            : AddressPrefix + (context.Connection.RemoteIpAddress?.ToString() ?? "unknown");
    }

    /// Whether a partition key names one guest rather than one address.
    public static bool IsGuest(string partitionKey) =>
        partitionKey.StartsWith(GuestPrefix, StringComparison.Ordinal);

    /// <summary>
    /// A cheap shape check, not a validation: it costs nothing and it keeps a
    /// stray or truncated cookie from minting a partition of its own. Anything
    /// that fails it is limited by address, which is the safe side to be on.
    /// </summary>
    private static bool LooksServerIssued(string? token)
    {
        if (token is null || token.Length != TokenLength) return false;
        foreach (var c in token)
        {
            var ok = c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_';
            if (!ok) return false;
        }
        return true;
    }

    private static string Fingerprint(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
