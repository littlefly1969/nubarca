using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;

namespace NubArca.Api.Party;

/// <summary>
/// The PERSONAL invitation capability: derivation, hashing and minting.
///
/// <para>The same construction the party's own tokens use, and deliberately not
/// the same token. The raw value is
/// <c>base64url(HMAC-SHA256(secret, CapabilityId ‖ "invitation-rsvp"))</c> —
/// keyed by an operator secret, bound to its purpose by the context string, and
/// derived from a random id that is never exposed. Only its SHA-256 is stored,
/// so a database read cannot open anybody's invitation, while the owner surface
/// can still reproduce the link to put it in an email.</para>
///
/// <para><b>There is no built-in secret, on purpose.</b> The database stores
/// each group's <c>CapabilityId</c>, so a key everybody can read in the source
/// would turn a database dump into every group's working link — names, notes,
/// answers and all. The key is <c>Party:InvitationTokenSecret</c>, else a
/// <c>Party:TokenSecret</c> the operator actually configured (the purpose
/// context keeps the two capabilities apart even under one key), else nothing:
/// the application refuses to start rather than sign with a known key. The
/// party links' own historical fallback is untouched and never used here.</para>
///
/// <para>It shares nothing with a <c>PartyAlbumLink</c>: not its id, not its
/// token, not its context. Holding an invitation is therefore never holding the
/// party's QR, and the two can be rotated without either noticing.</para>
/// </summary>
public sealed class PartyInvitationTokens
{
    /// <summary>The invitation's own secret — <c>Party__InvitationTokenSecret</c> in the environment.</summary>
    public const string InvitationSecretKey = "Party:InvitationTokenSecret";

    /// <summary>The party's secret, reused only when an operator explicitly set it.</summary>
    public const string PartySecretKey = "Party:TokenSecret";

    private static readonly byte[] Context = Encoding.UTF8.GetBytes("invitation-rsvp");

    private readonly byte[] _secret;

    public PartyInvitationTokens(IConfiguration config)
    {
        _secret = Encoding.UTF8.GetBytes(SecretFrom(config) ?? throw MissingSecret());
    }

    /// <summary>
    /// The key personal invitations are signed with, or null when there is none
    /// worth trusting: the dedicated invitation secret first, then an explicitly
    /// configured party secret. Blank is not configured.
    /// </summary>
    public static string? SecretFrom(IConfiguration config) =>
        Configured(config[InvitationSecretKey]) ?? Configured(config[PartySecretKey]);

    /// <summary>What the host says when it refuses to start without a key.</summary>
    public static InvalidOperationException MissingSecret() => new(
        "Personal party invitations need a signing secret and have no built-in one. Set "
        + "Party__InvitationTokenSecret (or configure Party__TokenSecret) to a strong random value, "
        + "for example the output of `openssl rand -base64 32`.");

    /// <summary>The raw personal token for one capability generation. ~43 URL-safe characters.</summary>
    public string Derive(Guid capabilityId)
    {
        using var hmac = new HMACSHA256(_secret);
        var mac = hmac.ComputeHash([.. capabilityId.ToByteArray(), .. Context]);
        return Convert.ToBase64String(mac).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    public static string Hash(string rawToken) => PartyLinkService.HashToken(rawToken);

    /// <summary>A fresh generation: a new random derivation input and the hash of what it derives.</summary>
    public (Guid CapabilityId, string TokenHash) Mint()
    {
        var capabilityId = Guid.NewGuid();
        return (capabilityId, Hash(Derive(capabilityId)));
    }

    /// <summary>The guest-facing page for a raw token, relative to the public origin.</summary>
    public static string InvitationPath(string rawToken) => $"/party/invite/{rawToken}";

    private static string? Configured(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
