using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NubArca.Api.Assistant;
using NubArca.Api.Help;
using Xunit;

namespace NubArca.Api.Tests.Assistant;

// Trust is an explicit operator decision, and configuration fails closed.
//
// The single most dangerous mistake this subsystem can make is deciding that
// something is local when it is not, so every path that could produce that
// answer is asserted here — including the ones that look harmless, like a URL
// that happens to say "localhost".
public sealed class AssistantTrustConfigurationTests
{
    private static AssistantModelOptions Model(
        string trust = nameof(AssistantModelTrust.External),
        string baseUrl = "https://provider.example",
        string apiKey = "k",
        string model = "m",
        string protocol = nameof(AssistantModelProtocol.OpenAiCompatible))
        => new()
        {
            Protocol = protocol,
            Trust = trust,
            BaseUrl = baseUrl,
            ApiKey = apiKey,
            Model = model,
        };

    // ---- URL never decides trust -------------------------------------------

    [Theory]
    [InlineData("http://localhost:11434")]
    [InlineData("http://127.0.0.1:8080")]
    // RFC1918 ranges the identity contract treats as generic, so these describe
    // a shape rather than somebody's network.
    [InlineData("https://172.16.4.9:8443")]
    [InlineData("https://10.0.0.7")]
    [InlineData("https://ollama:11434")]
    public void Trust_Is_Not_Inferred_From_Localhost_Or_LanUrl(string baseUrl)
    {
        // A reverse proxy on localhost can forward to a cloud API. Every one of
        // these LOOKS local and is declared External, and stays External —
        // except the plaintext ones, which an External model may not use at all.
        var resolution = AssistantModelResolver.Validate("p", Model(baseUrl: baseUrl));

        if (baseUrl.StartsWith("http://", StringComparison.Ordinal))
        {
            Assert.False(resolution.IsUsable);
            return;
        }
        Assert.True(resolution.IsUsable);
        Assert.Equal(AssistantModelTrust.External, resolution.Profile!.Trust);
        Assert.Equal("external", resolution.Profile.Boundary);
    }

    [Fact]
    public void A_Public_Url_Declared_LocalTrusted_Stays_LocalTrusted()
    {
        // The other direction, and the reason this is policy rather than
        // detection: a trusted GPU server on another site is not on this LAN,
        // and NubArca has no business overruling the operator about it.
        var resolution = AssistantModelResolver.Validate("p", Model(
            trust: nameof(AssistantModelTrust.LocalTrusted),
            baseUrl: "https://models.example.org"));

        Assert.True(resolution.IsUsable);
        Assert.Equal(AssistantModelTrust.LocalTrusted, resolution.Profile!.Trust);
        Assert.Equal("localTrusted", resolution.Profile.Boundary);
    }

    // ---- transport and credentials by classification ------------------------

    [Fact]
    public void External_Model_Requires_Secure_Transport()
    {
        // The key travels in an Authorization header on every request, and the
        // request crosses the boundary anyway.
        Assert.False(AssistantModelResolver
            .Validate("p", Model(baseUrl: "http://provider.example")).IsUsable);
        Assert.True(AssistantModelResolver
            .Validate("p", Model(baseUrl: "https://provider.example")).IsUsable);
    }

    [Fact]
    public void External_Model_Requires_A_Key()
        => Assert.False(AssistantModelResolver.Validate("p", Model(apiKey: "")).IsUsable);

    [Fact]
    public void LocalTrusted_Model_May_Use_Http()
    {
        var resolution = AssistantModelResolver.Validate("p", Model(
            trust: nameof(AssistantModelTrust.LocalTrusted),
            baseUrl: "http://model.internal:11434"));

        Assert.True(resolution.IsUsable);
        Assert.Equal(AssistantModelTrust.LocalTrusted, resolution.Profile!.Trust);
    }

    [Fact]
    public void LocalTrusted_Model_May_Omit_ApiKey()
        => Assert.True(AssistantModelResolver.Validate("p", Model(
            trust: nameof(AssistantModelTrust.LocalTrusted),
            baseUrl: "http://model.internal:11434",
            apiKey: string.Empty)).IsUsable);

    // ---- fail closed ---------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("local")]
    [InlineData("Local")]
    [InlineData("trusted")]
    [InlineData("internal")]
    [InlineData("none")]
    // Numeric, which `Enum.TryParse` would happily read as LocalTrusted — a
    // value nobody writes on purpose and exactly the one an accident produces.
    [InlineData("1")]
    [InlineData("2")]
    [InlineData("0")]
    public void Unknown_Trust_Fails_Closed(string trust)
    {
        // Never silently mapped to Local. A typo in the one field that decides
        // whether private data may ever be sent must not resolve to the
        // permissive answer.
        var resolution = AssistantModelResolver.Validate("p", Model(trust: trust));
        Assert.False(resolution.IsUsable);
        Assert.Null(resolution.Profile);
    }

    [Theory]
    [InlineData("external")]
    [InlineData("EXTERNAL")]
    [InlineData("  External  ")]
    public void A_Trust_Value_An_Operator_Plainly_Meant_Is_Accepted(string trust)
    {
        // Case and surrounding whitespace are accidents with no second meaning —
        // an environment variable with a trailing space should not silently turn
        // Help off. A value that could mean something ELSE is what fails closed.
        var resolution = AssistantModelResolver.Validate("p", Model(trust: trust));
        Assert.True(resolution.IsUsable);
        Assert.Equal(AssistantModelTrust.External, resolution.Profile!.Trust);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("Ollama")]
    [InlineData("Anthropic")]
    [InlineData("openai_compatible")]
    public void Unknown_Protocol_Fails_Closed(string protocol)
        => Assert.False(AssistantModelResolver.Validate("p", Model(protocol: protocol)).IsUsable);

    [Fact]
    public void ManagedLocal_Is_Not_Claimed_As_Implemented()
    {
        // NubArca ships no runtime whose isolation and egress lifecycle it
        // controls. The enum value exists so the schema does not have to change
        // when one arrives; activating it would let an installation present a
        // guarantee nothing implements.
        var resolution = AssistantModelResolver.Validate("p", Model(
            trust: nameof(AssistantModelTrust.ManagedLocal),
            baseUrl: "http://managed.internal:8000"));

        Assert.False(resolution.IsUsable);
        Assert.Equal(
            AssistantCapabilities.None,
            AssistantCapabilityPolicy.ForTrust(AssistantModelTrust.ManagedLocal));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("ftp://provider.example")]
    [InlineData("file:///etc/passwd")]
    public void An_Unusable_BaseUrl_Fails_Closed(string baseUrl)
        => Assert.False(AssistantModelResolver.Validate("p", Model(baseUrl: baseUrl)).IsUsable);

    // ---- selection -----------------------------------------------------------

    private static AssistantModelResolver Resolver(
        AssistantOptions assistant, ExternalHelpOptions? legacy = null)
        => new(
            Options.Create(assistant),
            Options.Create(legacy ?? new ExternalHelpOptions()),
            NullLogger<AssistantModelResolver>.Instance);

    [Fact]
    public void Help_Uses_The_Named_Profile_And_Nothing_Else()
    {
        var resolver = Resolver(new AssistantOptions
        {
            Enabled = true,
            HelpModel = "help-default",
            Models =
            {
                ["help-default"] = Model(baseUrl: "https://chosen.example"),
                ["other"] = Model(
                    trust: nameof(AssistantModelTrust.LocalTrusted),
                    baseUrl: "http://other.internal"),
            },
        });

        Assert.True(resolver.HelpModel.IsUsable);
        Assert.Equal("help-default", resolver.HelpModel.Profile!.Key);
        Assert.Equal(AssistantModelTrust.External, resolver.HelpModel.Profile.Trust);
    }

    [Fact]
    public void A_Disabled_Assistant_Resolves_To_Nothing()
    {
        var resolver = Resolver(new AssistantOptions
        {
            Enabled = false,
            HelpModel = "help-default",
            Models = { ["help-default"] = Model() },
        });

        Assert.False(resolver.HelpModel.IsUsable);
        Assert.Equal(AssistantFailureReasons.Disabled, resolver.HelpModel.Reason);
    }

    [Fact]
    public void A_HelpModel_Naming_A_Missing_Profile_Resolves_To_Nothing()
        => Assert.False(Resolver(new AssistantOptions
        {
            Enabled = true,
            HelpModel = "typo",
            Models = { ["help-default"] = Model() },
        }).HelpModel.IsUsable);

    // ---- legacy compatibility -------------------------------------------------

    [Fact]
    public void A_Legacy_ExternalHelp_Configuration_Still_Works()
    {
        var resolver = Resolver(new AssistantOptions(), new ExternalHelpOptions
        {
            Enabled = true,
            BaseUrl = "https://legacy.example",
            ApiKey = "legacy-key",
            Model = "legacy-model",
            ProviderLabel = "Legacy Provider",
            CorpusPath = "legacy-corpus.json",
        });

        Assert.True(resolver.HelpModel.IsUsable);
        Assert.Equal("Legacy Provider", resolver.HelpModel.Profile!.Label);
        // Bounds and corpus location come across too, so an upgrade does not
        // silently start looking for the corpus somewhere else.
        Assert.Equal("legacy-corpus.json", resolver.HelpBounds.CorpusPath);
    }

    [Fact]
    public void A_Legacy_Configuration_Can_Never_Declare_Itself_LocalTrusted()
    {
        // Including — especially — when it points somewhere that looks local. A
        // configuration shape that predates the trust axis cannot assert one,
        // and an upgrade must not quietly turn an external installation into a
        // trusted-local one.
        var resolver = Resolver(new AssistantOptions(), new ExternalHelpOptions
        {
            Enabled = true,
            BaseUrl = "https://localhost:8443",
            ApiKey = "k",
            Model = "m",
        });

        Assert.True(resolver.HelpModel.IsUsable);
        Assert.Equal(AssistantModelTrust.External, resolver.HelpModel.Profile!.Trust);
        Assert.Equal(AssistantModelResolver.LegacyExternalHelpKey, resolver.HelpModel.Profile.Key);
    }

    [Fact]
    public void The_New_Configuration_Wins_Over_A_Legacy_One()
    {
        // An operator who has started migrating never gets a silent mix of the
        // two, and in particular never keeps talking to the old provider
        // because a stale variable is still set somewhere.
        var resolver = Resolver(
            new AssistantOptions
            {
                Enabled = true,
                HelpModel = "new",
                Models = { ["new"] = Model(baseUrl: "https://new.example") },
                Help = { CorpusPath = "new-corpus.json" },
            },
            new ExternalHelpOptions
            {
                Enabled = true,
                BaseUrl = "https://legacy.example",
                ApiKey = "legacy-key",
                Model = "legacy-model",
                CorpusPath = "legacy-corpus.json",
            });

        Assert.Equal("new", resolver.HelpModel.Profile!.Key);
        Assert.Equal("https://new.example", resolver.HelpModel.Profile.BaseUrl);
        Assert.Equal("new-corpus.json", resolver.HelpBounds.CorpusPath);
    }

    [Fact]
    public void Nothing_Configured_At_All_Is_Disabled_Rather_Than_Broken()
    {
        var resolver = Resolver(new AssistantOptions());
        Assert.False(resolver.HelpModel.IsUsable);
        Assert.Equal(AssistantFailureReasons.Disabled, resolver.HelpModel.Reason);
    }

    // ---- bounds ---------------------------------------------------------------

    [Fact]
    public void Configured_Bounds_Can_Tighten_And_Cannot_Become_Unbounded()
    {
        var bounds = new AssistantHelpOptions
        {
            MaxQuestionCharacters = int.MaxValue,
            MaxHistoryTurns = -5,
            MaxHistoryCharacters = int.MaxValue,
            MaxEvidenceChunks = 9999,
            MaxEvidenceCharacters = int.MaxValue,
        };

        Assert.Equal(8000, bounds.EffectiveQuestionCharacters);
        Assert.Equal(0, bounds.EffectiveHistoryTurns);
        Assert.Equal(32000, bounds.EffectiveHistoryCharacters);
        Assert.Equal(20, bounds.EffectiveEvidenceChunks);
        Assert.Equal(60000, bounds.EffectiveEvidenceCharacters);

        var tighter = new AssistantHelpOptions { MaxQuestionCharacters = 100, MaxEvidenceChunks = 2 };
        Assert.Equal(100, tighter.EffectiveQuestionCharacters);
        Assert.Equal(2, tighter.EffectiveEvidenceChunks);
    }

    [Fact]
    public void Profile_Timeout_And_Output_Are_Clamped()
    {
        var profile = AssistantTextModelContractTests.ExternalProfile() with
        {
            TimeoutSeconds = 99999,
            MaxOutputTokens = 0,
        };
        Assert.Equal(120, profile.EffectiveTimeoutSeconds);
        Assert.Equal(1, profile.EffectiveMaxOutputTokens);
    }
}
