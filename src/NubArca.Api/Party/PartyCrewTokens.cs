using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;

namespace NubArca.Api.Party;

/// <summary>
/// Every secret Party Crew handles: the invite token, the challenge token, the
/// device token and the one-time code.
///
/// <para>Three of the four are the same thing — 256 bits of CSPRNG, handed over
/// once, stored only as <c>SHA-256</c>. A database read cannot impersonate
/// anybody, which is the property that makes an accountless credential
/// acceptable at all.</para>
///
/// <para><b>The code is the exception, and it is why this class has a secret.</b>
/// Six digits is a million possibilities: <c>SHA-256(482117)</c> is a lookup
/// table a laptop builds in a second, so storing one would mean a database dump
/// yields every live code, and a code plus a forwarded link is an account. The
/// stored proof is therefore keyed —
/// <c>HMAC-SHA256(secret, challengeId ‖ "party-crew-otp" ‖ code)</c> — and
/// useless to anybody who does not also hold the server's key.</para>
///
/// <para><b>There is no built-in secret.</b> A key written in the source would
/// be a key every installation shares and every reader knows. The application
/// refuses to start without one rather than signing with a known value — the
/// same decision <see cref="PartyInvitationTokens"/> made, for the same reason.
/// The purpose string keeps this use apart from any other that shares the key.
/// </para>
/// </summary>
public sealed class PartyCrewTokens
{
    /// <summary>Party Crew's own secret — <c>Party__CollaboratorOtpSecret</c> in the environment.</summary>
    public const string CrewSecretKey = "Party:CollaboratorOtpSecret";

    /// <summary>The party's secret, reused only when an operator explicitly set one.</summary>
    public const string PartySecretKey = "Party:TokenSecret";

    /// <summary>
    /// Purpose binding. Without it, a key shared with another Party capability
    /// would let a proof minted for one purpose be replayed against the other.
    /// </summary>
    private static readonly byte[] OtpContext = Encoding.UTF8.GetBytes("party-crew-auth-v1");

    private readonly byte[] _secret;

    public PartyCrewTokens(IConfiguration config)
    {
        _secret = Encoding.UTF8.GetBytes(SecretFrom(config) ?? throw MissingSecret());
    }

    /// <summary>The key Party Crew codes are proven with, or null when there is none worth trusting.</summary>
    public static string? SecretFrom(IConfiguration config) =>
        Configured(config[CrewSecretKey]) ?? Configured(config[PartySecretKey]);

    /// <summary>What the host says when it refuses to start without a key.</summary>
    public static InvalidOperationException MissingSecret() => new(
        "Party Crew one-time codes need a signing secret and have no built-in one. Set "
        + "Party__CollaboratorOtpSecret (or configure Party__TokenSecret) to a strong random value, "
        + "for example the output of `openssl rand -base64 32`.");

    /// <summary>
    /// A fresh opaque credential: 256 bits, URL-safe, ~43 characters.
    ///
    /// <para>Used for the invite, the challenge and the device alike. They are
    /// different rows with different lifetimes and different scopes, but the
    /// strength of each is the same and there is no reason for three
    /// constructions.</para>
    /// </summary>
    public static string NewToken()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    /// <summary>What a token looks like before it is worth a database round trip.</summary>
    public static bool LooksLikeToken(string? token) =>
        token is { Length: 43 } && token.All(c =>
            c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_');

    /// <summary>SHA-256 hex, the one form any of these tokens is ever stored in.</summary>
    public static string Hash(string rawToken) => PartyLinkService.HashToken(rawToken);

    /// <summary>
    /// A six-digit code, uniformly distributed.
    ///
    /// <para><c>RandomNumberGenerator.GetInt32</c> rather than a modulo of
    /// random bytes: the modulo is biased, and a biased code is a smaller
    /// search space than it looks.</para>
    /// </summary>
    public static string NewOtp() => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");

    /// <summary>The stored proof of one code, bound to the challenge it belongs to.</summary>
    /// <summary>
    /// <c>HMAC-SHA256(secret, challengeId ‖ "party-crew-auth-v1" ‖ generation ‖ otp)</c>.
    ///
    /// <para>The GENERATION is in the message, so a code is valid for the send
    /// it was minted for and no other. Replacing the proof on a resend already
    /// stops the previous code matching; binding the generation means the two
    /// could not collide even if a proof were recovered and replayed.</para>
    /// </summary>
    public string OtpProof(Guid challengeId, int generation, string otp)
    {
        using var hmac = new HMACSHA256(_secret);
        var mac = hmac.ComputeHash(
        [
            .. challengeId.ToByteArray(),
            .. OtpContext,
            .. BitConverter.GetBytes(generation),
            .. Encoding.UTF8.GetBytes(otp),
        ]);
        return Convert.ToHexString(mac).ToLowerInvariant();
    }

    /// <summary>
    /// Whether a submitted code matches, compared in constant time.
    ///
    /// <para>An ordinary string comparison returns as soon as two characters
    /// differ, and the time it took says how much of the code was right. With
    /// only a million candidates that is a real shortcut.</para>
    /// </summary>
    public bool OtpMatches(Guid challengeId, int generation, string storedProof, string submitted) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(OtpProof(challengeId, generation, submitted)),
            Encoding.ASCII.GetBytes(storedProof));

    /// <summary>The pairing page for a raw invite token, relative to the public origin.</summary>
    public static string InvitePath(string rawToken) => $"/party/crew/invite#token={rawToken}";

    private static string? Configured(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}

/// <summary>
/// The numbers Party Crew runs on, in one place so a test and the service agree.
/// </summary>
public static class PartyCrewLimits
{
    /// <summary>At most two devices per collaborator, per party. The point of the slice.</summary>
    public const int MaxDevicesPerCollaborator = 2;

    public const int MaxDisplayNameLength = 120;
    public const int MaxEmailLength = 254;
    public const int MaxDeviceLabelLength = 80;
    public const int MaxUserAgentLength = 512;

    /// <summary>Long enough to walk to an inbox, short enough that a lost link ages out.</summary>
    public static readonly TimeSpan InviteLifetime = TimeSpan.FromHours(24);

    /// <summary>Long enough to read an email and type six digits.</summary>
    public static readonly TimeSpan ChallengeLifetime = TimeSpan.FromMinutes(10);

    /// <summary>A season, not a session: a collaborator should not re-pair mid-party.</summary>
    public static readonly TimeSpan DeviceLifetime = TimeSpan.FromDays(365);

    /// <summary>Wrong codes before the challenge is spent.</summary>
    public const int MaxOtpAttempts = 5;

    /// <summary>The floor between two sends of a code to the same address.</summary>
    public static readonly TimeSpan ResendInterval = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How many codes one challenge may ever send, the first included.
    ///
    /// <para>The resend INTERVAL spaces them; this bounds them. Without it a
    /// link is a way to put ten emails in somebody's inbox, one a minute, for
    /// as long as the challenge lives.</para>
    /// </summary>
    public const int MaxOtpSendsPerChallenge = 3;

    /// <summary>
    /// How many challenges one collaborator may start in <see cref="SendWindow"/>.
    ///
    /// <para>Re-opening the link is what resets a challenge's own budget, so
    /// without a second bound there is no bound at all. Per COLLABORATOR and
    /// not per address of origin: an address is not who is being written to,
    /// and a script behind a carrier NAT is indistinguishable from a guest.
    /// </para>
    /// </summary>
    public const int MaxChallengesPerCollaborator = 3;

    public static readonly TimeSpan SendWindow = TimeSpan.FromHours(1);
}
