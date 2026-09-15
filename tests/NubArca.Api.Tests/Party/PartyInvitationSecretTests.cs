using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using NubArca.Api.Party;

namespace NubArca.Api.Tests.Party;

/// <summary>
/// Which key signs a personal invitation — and that no key everybody can read
/// ever does.
///
/// <para>The database stores each group's capability id, so a signing key that
/// is public in the source would make a database dump into every group's working
/// link. The rule: the dedicated invitation secret, else a party secret the
/// operator actually configured, else the application does not start.</para>
/// </summary>
public sealed class PartyInvitationSecretTests
{
    private static IConfiguration Config(string? invitation, string? party) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [PartyInvitationTokens.InvitationSecretKey] = invitation,
                [PartyInvitationTokens.PartySecretKey] = party,
            })
            .Build();

    /// <summary>The documented construction, computed independently.</summary>
    private static string Expected(string secret, Guid generation)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var mac = hmac.ComputeHash([.. generation.ToByteArray(), .. Encoding.UTF8.GetBytes("invitation-rsvp")]);
        return Convert.ToBase64String(mac).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    [Fact]
    public void The_dedicated_invitation_secret_wins()
    {
        var generation = Guid.NewGuid();
        var raw = new PartyInvitationTokens(Config("secret-A", "secret-B")).Derive(generation);

        Assert.Equal(Expected("secret-A", generation), raw);
        Assert.NotEqual(Expected("secret-B", generation), raw);
    }

    [Fact]
    public void An_explicitly_configured_party_secret_is_reused()
    {
        var generation = Guid.NewGuid();
        var raw = new PartyInvitationTokens(Config(null, "secret-B")).Derive(generation);

        Assert.Equal(Expected("secret-B", generation), raw);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("   ", null)]
    [InlineData(null, " ")]
    public void With_no_secret_there_is_no_invitation_capability(string? invitation, string? party)
    {
        Assert.Null(PartyInvitationTokens.SecretFrom(Config(invitation, party)));
        var refused = Assert.Throws<InvalidOperationException>(() => new PartyInvitationTokens(Config(invitation, party)));
        Assert.Contains("Party__InvitationTokenSecret", refused.Message);
    }

    [Fact]
    public void The_party_links_built_in_key_never_signs_an_invitation()
    {
        // The only way to get tokens is to configure a key, and a configured key
        // is not the built-in one, so nothing derivable from the source opens a
        // group.
        var generation = Guid.NewGuid();
        var raw = new PartyInvitationTokens(Config("an-operator-secret", null)).Derive(generation);
        Assert.NotEqual(Expected(PartyLinkService.DefaultSecret, generation), raw);
        Assert.Throws<InvalidOperationException>(() => new PartyInvitationTokens(new ConfigurationBuilder().Build()));
    }

    [Fact]
    public void Under_one_key_the_invitation_is_none_of_the_partys_capabilities()
    {
        var shared = Config(null, "shared-secret");
        var links = new PartyLinkService(null!, TimeProvider.System, null!, null!, shared);
        var generation = Guid.NewGuid();

        var invitation = new PartyInvitationTokens(shared).Derive(generation);
        var (viewHash, uploadHash) = links.MintTokenHashes(generation);

        Assert.NotEqual(links.DeriveViewToken(generation), invitation);
        Assert.NotEqual(links.DerivePrintToken(generation), invitation);
        Assert.NotEqual(viewHash, PartyInvitationTokens.Hash(invitation));
        Assert.NotEqual(uploadHash, PartyInvitationTokens.Hash(invitation));
    }

    [Fact]
    public void A_host_with_a_database_refuses_to_start_without_a_key()
    {
        var storage = Path.Combine(Path.GetTempPath(), $"nubarca-secret-{Guid.NewGuid():N}");
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            // Never reached: the refusal comes before anything connects.
            builder.UseSetting("ConnectionStrings:Postgres", "Host=127.0.0.1;Port=1;Database=none;Username=none;Password=none");
            builder.UseSetting("Storage:RootPath", storage);
            builder.UseSetting(PartyInvitationTokens.InvitationSecretKey, "");
            builder.UseSetting(PartyInvitationTokens.PartySecretKey, "");
        });

        var failure = Record.Exception(() => factory.Services);

        Assert.NotNull(failure);
        var messages = new List<string>();
        for (var e = failure; e is not null; e = e.InnerException) messages.Add(e.Message);
        Assert.Contains(messages, m => m.Contains("Party__InvitationTokenSecret"));
    }
}
