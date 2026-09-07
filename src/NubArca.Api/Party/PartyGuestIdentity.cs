using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;

namespace NubArca.Api.Party;

/// <summary>
/// Who, anonymously, is doing this — as opposed to what they are allowed to do.
///
/// <para>A party's capability tokens say WHAT a browser may do: read the album,
/// contribute, print. They are printed on a QR and shared by everyone at the
/// party, so they identify nobody. This derives the other half: one anonymous
/// guest identity per browser per <see cref="Domain.PartyAlbumLink"/>, which is
/// what every counter, quota and vote in Party is actually about.</para>
///
/// <para><b>The browser token is not a capability.</b> It is a random value the
/// server issues and stores nowhere, held in one cookie for the whole
/// <c>/api/party</c> surface — so a guest who reads the album, uploads a photo
/// and prints a keepsake is ONE guest, not three. Before this, the cookie was
/// path-scoped to the capability token that minted it, and each capability
/// quietly grew its own participant with its own allowance.</para>
///
/// <para><b>Per link, the identity is different.</b> The stored key is
/// <c>SHA-256(HMAC-SHA256(serverSecret, browserToken ++ linkId))</c>. Two
/// consequences are the point of the construction: the same browser at two
/// parties produces two unrelated keys, so no counter and no vote can cross
/// between them; and a database read gives an attacker neither the browser
/// token nor any value they could present, because presenting requires the raw
/// token and deriving requires the server secret.</para>
///
/// <para>It remains an anonymous BROWSER identity, not a person. Clearing site
/// data or switching device produces a new guest, and that is deliberate: the
/// alternatives are fingerprinting, IP identity and asking for a name, none of
/// which this codebase does.</para>
/// </summary>
public interface IPartyGuestIdentity
{
    /// <summary>A fresh browser token. The only moment the raw value exists.</summary>
    string NewBrowserToken();

    /// <summary>
    /// The stored identity key for this browser at this party. Deterministic, so
    /// every capability on the link resolves the same guest.
    /// </summary>
    string LinkIdentityHash(string browserToken, Guid partyAlbumLinkId);

    /// <summary>
    /// The key a pre-migration cookie was stored under: a plain hash of the raw
    /// participant token, minted per capability path. Kept ONLY so a party that
    /// is running right now can carry its guests across the change.
    /// </summary>
    string LegacyIdentityHash(string legacyParticipantToken);

    /// <summary>
    /// Whether a value has the shape this service issues. A cheap check, never a
    /// validation: it keeps a truncated or stray cookie out of a derivation, and
    /// nothing more.
    /// </summary>
    bool LooksIssued(string? token);
}

public sealed class PartyGuestIdentity : IPartyGuestIdentity
{
    // 32 bytes of CSPRNG output as unpadded base64url — the same order of
    // entropy as the party tokens, and the same shape, so one length check
    // covers both the browser token and a legacy participant token.
    private const int TokenBytes = 32;
    internal const int TokenLength = 43;

    // Same key material as the party link tokens: an installation configures one
    // party secret, not two. The fallback matches PartyLinkService's for the
    // same reason it exists there — a dev machine needs no configuration, and
    // production sets Party__TokenSecret.
    private const string DefaultSecret = "nubarca-party-token-secret-v1";

    private readonly byte[] _secret;

    public PartyGuestIdentity(IConfiguration config)
    {
        var configured = config["Party:TokenSecret"];
        _secret = Encoding.UTF8.GetBytes(
            string.IsNullOrWhiteSpace(configured) ? DefaultSecret : configured);
    }

    public string NewBrowserToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(TokenBytes);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public string LinkIdentityHash(string browserToken, Guid partyAlbumLinkId)
    {
        // The link id is part of the MESSAGE, not of the key, so one secret
        // serves every party while the derived value stays per-party.
        var message = new byte[Encoding.UTF8.GetByteCount(browserToken) + 16];
        var written = Encoding.UTF8.GetBytes(browserToken, message);
        partyAlbumLinkId.TryWriteBytes(message.AsSpan(written));
        return Hex(SHA256.HashData(HMACSHA256.HashData(_secret, message)));
    }

    public string LegacyIdentityHash(string legacyParticipantToken) =>
        Hex(SHA256.HashData(Encoding.UTF8.GetBytes(legacyParticipantToken)));

    public bool LooksIssued(string? token)
    {
        if (token is null || token.Length != TokenLength) return false;
        foreach (var c in token)
        {
            var ok = c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_';
            if (!ok) return false;
        }
        return true;
    }

    private static string Hex(byte[] value) => Convert.ToHexStringLower(value);
}
