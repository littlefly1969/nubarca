using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using NubArca.Api.Domain;

namespace NubArca.Api.Albums.Sharing;

/// <summary>
/// Every secret an album share handles: the link token, the device token and
/// the one-time code.
///
/// <para>The link token is DERIVED from the link id under a keyed MAC rather
/// than generated and stored. The database therefore holds nothing that opens
/// an album — only a digest to match against — while the owner can still be
/// shown their own link a month later without anybody having kept it.</para>
///
/// <para><b>The purpose string is the isolation.</b> A party's view token is
/// <c>HMAC(secret, linkId)</c>; this is <c>HMAC(secret, "album-share-v1" ‖
/// linkId)</c>. The two are computed from different inputs, so an album token
/// can no more open a party than a party token can open an album — not because
/// a check forbids it, but because the values live in different spaces and the
/// digests never meet. A check can be forgotten on a new endpoint; this cannot.
/// </para>
///
/// <para><b>The code is why this class needs a key at all.</b> Six digits is a
/// million possibilities, so <c>SHA-256(482117)</c> is a lookup table a laptop
/// builds in a second and a database dump would yield every live code. The
/// stored proof is keyed — and it covers the challenge's GENERATION, so sending
/// a second code stops the first one verifying. Party Crew reached the same
/// conclusion first; this follows it deliberately rather than inventing a
/// second answer to one question.</para>
///
/// <para><b>There is no built-in secret.</b> A key written into the source is a
/// key every installation shares and every reader of the repository knows. The
/// application refuses to start without one rather than signing with a value
/// that is public by construction.</para>
/// </summary>
public sealed class AlbumShareTokens
{
    /// <summary>The share's own secret, falling back to the party's if unset.</summary>
    public const string ShareSecretKey = "Albums:ShareTokenSecret";

    /// <summary>What an installation that already runs parties has configured.</summary>
    public const string PartySecretKey = "Party:TokenSecret";

    private static readonly byte[] LinkContext = Encoding.UTF8.GetBytes("nubarca-album-share-v1");
    private static readonly byte[] OtpContext = Encoding.UTF8.GetBytes("nubarca-album-share-otp-v1");

    private readonly byte[] _secret;

    public AlbumShareTokens(IConfiguration config)
    {
        var secret = SecretFrom(config);
        if (string.IsNullOrWhiteSpace(secret)) throw MissingSecret();
        _secret = Encoding.UTF8.GetBytes(secret);
    }

    public static string? SecretFrom(IConfiguration config) =>
        config[ShareSecretKey] is { Length: > 0 } own ? own : config[PartySecretKey];

    public static InvalidOperationException MissingSecret() => new(
        "Album share links need a signing secret and have no built-in one. Set "
        + "Albums__ShareTokenSecret (or configure Party__TokenSecret) to a strong random value, "
        + "for example `openssl rand -base64 32`.");

    /// <summary>
    /// The link's public token: URL-safe base64 of a 256-bit MAC, about 43
    /// characters. Deterministic for a link id, so it is never stored and never
    /// needs to be: rotating a link means a new id, which means a new token and
    /// a dead old one.
    /// </summary>
    public string DeriveToken(Guid linkId)
    {
        using var hmac = new HMACSHA256(_secret);
        var mac = hmac.ComputeHash([.. LinkContext, .. linkId.ToByteArray()]);
        return Convert.ToBase64String(mac).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    /// <summary>256 bits of CSPRNG, for a device. Handed over once, stored as a digest.</summary>
    public static string NewDeviceToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    /// <summary>A six-digit code, uniformly drawn — never a timestamp or a counter.</summary>
    public static string NewOtp() => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");

    /// <summary>
    /// The stored proof for one code. Keyed, and bound to the challenge and its
    /// generation so a proof cannot be replayed onto another challenge or
    /// survive a resend.
    /// </summary>
    public string OtpProof(Guid challengeId, int generation, string otp)
    {
        using var hmac = new HMACSHA256(_secret);
        var message = new List<byte>();
        message.AddRange(challengeId.ToByteArray());
        message.AddRange(OtpContext);
        message.AddRange(BitConverter.GetBytes(generation));
        message.AddRange(Encoding.UTF8.GetBytes(otp));
        return Convert.ToHexStringLower(hmac.ComputeHash([.. message]));
    }

    /// <summary>Fixed-time comparison, because a code is a secret being guessed.</summary>
    public bool OtpMatches(Guid challengeId, int generation, string storedProof, string submitted) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(storedProof),
            Encoding.UTF8.GetBytes(OtpProof(challengeId, generation, submitted)));

    /// <summary>SHA-256 hex. The one shape every stored token digest in this product has.</summary>
    public static string Hash(string rawToken) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken)));

    /// <summary>The guest page for a raw token, relative to the public origin.</summary>
    public static string SharePath(string rawToken) => $"/album/{Uri.EscapeDataString(rawToken)}";

    /// <summary>
    /// Whether a string could be one of our tokens at all. A cheap shape test
    /// ahead of a database lookup, never an authorization decision.
    /// </summary>
    public static bool LooksLikeToken(string? token) =>
        token is { Length: >= 40 and <= 64 } && token.All(
            c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    /// <summary>
    /// An address as the list stores and matches it. A list is only a list if
    /// it matches, and people type their own address with capitals and spaces.
    /// </summary>
    public static string NormalizeEmail(string? email) =>
        email?.Trim().ToLowerInvariant() ?? string.Empty;

    public static bool IsPlausibleEmail(string? email)
    {
        var value = NormalizeEmail(email);
        if (value.Length is 0 or > AlbumShareLimits.MaxEmailLength) return false;
        var at = value.IndexOf('@');
        return at > 0 && at < value.Length - 1
            && value.IndexOf('@', at + 1) < 0
            && value.LastIndexOf('.') > at + 1
            && !value.Contains(' ');
    }
}
