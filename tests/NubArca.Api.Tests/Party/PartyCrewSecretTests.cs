using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using NubArca.Api.Party;

namespace NubArca.Api.Tests.Party;

/// <summary>
/// What proves a one-time code, and why it cannot be a hash.
///
/// <para>Six digits is a million possibilities. <c>SHA-256(482117)</c> is a
/// lookup table a laptop builds in a second, so a database dump of bare hashes
/// would yield every live code — and a code plus a forwarded link is an
/// account. The stored proof is therefore KEYED, and the key has no built-in
/// value: a key written into the source would be a key every installation
/// shares and every reader knows.</para>
/// </summary>
public sealed class PartyCrewSecretTests
{
    private static IConfiguration Config(string? crew, string? party) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [PartyCrewTokens.CrewSecretKey] = crew,
                [PartyCrewTokens.PartySecretKey] = party,
            })
            .Build();

    /// <summary>The documented construction, computed independently.</summary>
    private static string Expected(string secret, Guid challengeId, string otp)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var mac = hmac.ComputeHash(
        [
            .. challengeId.ToByteArray(),
            .. Encoding.UTF8.GetBytes("party-crew-otp"),
            .. Encoding.UTF8.GetBytes(otp),
        ]);
        return Convert.ToHexString(mac).ToLowerInvariant();
    }

    [Fact]
    public void The_dedicated_crew_secret_wins()
    {
        var challengeId = Guid.NewGuid();
        var proof = new PartyCrewTokens(Config("secret-A", "secret-B")).OtpProof(challengeId, "482117");

        Assert.Equal(Expected("secret-A", challengeId, "482117"), proof);
        Assert.NotEqual(Expected("secret-B", challengeId, "482117"), proof);
    }

    [Fact]
    public void An_explicitly_configured_party_secret_is_reused()
    {
        var challengeId = Guid.NewGuid();
        var proof = new PartyCrewTokens(Config(null, "secret-B")).OtpProof(challengeId, "482117");

        Assert.Equal(Expected("secret-B", challengeId, "482117"), proof);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("   ", null)]
    [InlineData(null, " ")]
    public void With_no_secret_party_crew_refuses_to_start(string? crew, string? party)
    {
        Assert.Null(PartyCrewTokens.SecretFrom(Config(crew, party)));
        var refused = Assert.Throws<InvalidOperationException>(
            () => new PartyCrewTokens(Config(crew, party)));
        // The message names the variable an operator has to set, because an
        // application that refuses to start has to say how to fix it.
        Assert.Contains("Party__CollaboratorOtpSecret", refused.Message);
    }

    [Fact]
    public void The_proof_is_bound_to_its_own_challenge()
    {
        var tokens = new PartyCrewTokens(Config("secret-A", null));
        var mine = Guid.NewGuid();
        var yours = Guid.NewGuid();

        // The SAME six digits, two challenges: a proof lifted from one row is
        // worthless against the other.
        Assert.NotEqual(tokens.OtpProof(mine, "482117"), tokens.OtpProof(yours, "482117"));
        Assert.True(tokens.OtpMatches(mine, tokens.OtpProof(mine, "482117"), "482117"));
        Assert.False(tokens.OtpMatches(yours, tokens.OtpProof(mine, "482117"), "482117"));
    }

    [Fact]
    public void A_proof_is_never_the_bare_hash_of_the_code()
    {
        var challengeId = Guid.NewGuid();
        var proof = new PartyCrewTokens(Config("secret-A", null)).OtpProof(challengeId, "482117");

        // The lookup table a dump of bare hashes would be.
        var bare = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("482117")))
            .ToLowerInvariant();
        Assert.NotEqual(bare, proof);
    }

    [Fact]
    public void Every_credential_is_256_bits_of_url_safe_randomness()
    {
        var tokens = Enumerable.Range(0, 64).Select(_ => PartyCrewTokens.NewToken()).ToArray();

        Assert.All(tokens, token =>
        {
            // 32 bytes, base64url, no padding.
            Assert.Equal(43, token.Length);
            Assert.True(PartyCrewTokens.LooksLikeToken(token));
            Assert.All(token, c => Assert.True(
                char.IsAsciiLetterOrDigit(c) || c is '-' or '_', $"Not URL-safe: {c}"));
        });
        Assert.Equal(tokens.Length, tokens.Distinct().Count());
    }

    [Fact]
    public void A_code_is_always_six_digits_including_the_ones_that_start_with_zero()
    {
        var codes = Enumerable.Range(0, 200).Select(_ => PartyCrewTokens.NewOtp()).ToArray();

        // "D6" and not ToString(): 004217 must stay six characters, or a third
        // of the code space would be typed wrong.
        Assert.All(codes, code =>
        {
            Assert.Equal(6, code.Length);
            Assert.All(code, c => Assert.True(char.IsAsciiDigit(c), $"Not a digit: {c}"));
        });
    }

    [Fact]
    public void The_invite_path_puts_the_token_in_the_fragment_and_not_the_query()
    {
        var raw = PartyCrewTokens.NewToken();
        var path = PartyCrewTokens.InvitePath(raw);

        // A fragment is never sent to a server, never written to an access log
        // and never put in a Referer. A query string is all three.
        Assert.StartsWith("/party/crew/invite#token=", path);
        Assert.DoesNotContain("?", path);
        Assert.EndsWith(raw, path);
    }
}
