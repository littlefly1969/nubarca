using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;

namespace NubArca.Api.Party;

/// <summary>
/// The PERSONAL invitation capability: derivation, hashing and minting.
///
/// <para>The same construction the party's own tokens use, and deliberately not
/// the same token. The raw value is
/// <c>base64url(HMAC-SHA256(Party:TokenSecret, CapabilityId ‖ "invitation-rsvp"))</c>
/// — keyed by the installation's party secret, bound to its purpose by the
/// context string, and derived from a random id that is never exposed. Only its
/// SHA-256 is stored, so a database read cannot open anybody's invitation, while
/// the owner surface can still reproduce the link to put it in an email.</para>
///
/// <para>It shares nothing with a <c>PartyAlbumLink</c>: not its id, not its
/// token, not its context. Holding an invitation is therefore never holding the
/// party's QR, and the two can be rotated without either noticing.</para>
/// </summary>
public sealed class PartyInvitationTokens
{
    private static readonly byte[] Context = Encoding.UTF8.GetBytes("invitation-rsvp");

    private readonly byte[] _secret;

    public PartyInvitationTokens(IConfiguration config)
    {
        var configured = config["Party:TokenSecret"];
        _secret = Encoding.UTF8.GetBytes(
            string.IsNullOrWhiteSpace(configured) ? PartyLinkService.DefaultSecret : configured);
    }

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
}
