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
/// <para><b>THIS IS NOT AN AUTHORIZATION BOUNDARY.</b> The cookie is presented
/// by the client, so it is an identity claim rather than a proof, and a caller
/// that invents a well-formed value may well get a provisional partition of its
/// own — the middleware cannot afford to verify it, and the shape check below
/// only removes the trivial case. That is deliberately harmless, because a
/// partition buys nothing: the right to change a party's result comes from a
/// participant this party ISSUED at join and the vote endpoint resolves
/// server-side, never from a cookie's existence. An invented identity is
/// refused before a row of any kind is written, so the worst a rotating caller
/// achieves is its own bucket in which to be told no.</para>
///
/// <para>What this IS, then, is a fairness mechanism: it stops one guest's
/// phone from spending the room's allowance, and stops the room's size from
/// being the thing that breaks the evening.</para>
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
        var raw = context.Request.Cookies[PartyGuestSession.BrowserCookieName];
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
