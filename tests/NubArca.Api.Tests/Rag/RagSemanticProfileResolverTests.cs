using Microsoft.Extensions.Options;
using NubArca.Api.Rag;
using NubArca.Api.Rag.Domains;
using Xunit;

namespace NubArca.Api.Tests.Rag;

// Which domain embeds with which model, and which domain does not embed at all.
//
// One global switch stopped being defensible the moment it was measured.
// Against `multilingual-e5-small`, Product Help's MRR goes from 0.938 to 0.969
// while the repository's Recall@5 goes from 0.800 DOWN to 0.700 — a
// general-purpose multilingual sentence model asked to discriminate among
// 23,745 chunks of mostly C# returns plausible neighbours that are wrong, and
// they displace correct results the lexical path had already found. Those are
// not two opinions about one setting.
//
// The asymmetry below is the part worth reading twice. A system domain may
// inherit the installation default; an OWNER-PRIVATE one may not, because
// "semantic was switched on for Help eighteen months ago" is not a decision
// anybody made about a person's own documents.
public sealed class RagSemanticProfileResolverTests
{
    private const string E5 = "rag-text-multilingual-e5-small-v1";
    private const string Other = "rag-text-other-v1";

    [Fact]
    public void ProductHelp_UsesItsConfiguredProfile()
    {
        var resolver = Resolver(new RagOptions
        {
            Domains =
            {
                [RagDomains.ProductHelp] = new RagDomainSemanticOptions
                {
                    SemanticEnabled = true,
                    TextEmbeddingProfileKey = E5,
                },
            },
        });

        var settings = resolver.Resolve(RagDomainKey.ProductHelp);

        Assert.True(settings.Enabled);
        Assert.Equal(E5, settings.ProfileKey);
    }

    [Fact]
    public void Repository_CanRemainLexicalWhileHelpIsSemantic()
    {
        // The configuration the measurements actually argue for.
        var resolver = Resolver(new RagOptions
        {
            Domains =
            {
                [RagDomains.ProductHelp] = new RagDomainSemanticOptions
                {
                    SemanticEnabled = true,
                    TextEmbeddingProfileKey = E5,
                },
                [RagDomains.NubArcaRepository] = new RagDomainSemanticOptions
                {
                    SemanticEnabled = false,
                },
            },
        });

        Assert.True(resolver.Resolve(RagDomainKey.ProductHelp).Enabled);
        Assert.False(resolver.Resolve(RagDomainKey.NubArcaRepository).Enabled);
    }

    [Fact]
    public void DifferentDomains_CanUseDifferentProfiles()
    {
        var resolver = Resolver(new RagOptions
        {
            Domains =
            {
                [RagDomains.ProductHelp] = new RagDomainSemanticOptions
                {
                    SemanticEnabled = true, TextEmbeddingProfileKey = E5,
                },
                [RagDomains.NubArcaRepository] = new RagDomainSemanticOptions
                {
                    SemanticEnabled = true, TextEmbeddingProfileKey = Other,
                },
            },
        });

        Assert.Equal(E5, resolver.Resolve(RagDomainKey.ProductHelp).ProfileKey);
        Assert.Equal(Other, resolver.Resolve(RagDomainKey.NubArcaRepository).ProfileKey);
    }

    [Fact]
    public void DomainProfileResolution_NeverMixesProfiles()
    {
        // Two profiles are two coordinate systems, and a cosine between them is
        // a number with no meaning. Configuring one domain must not move
        // another's answer by even one field.
        var options = new RagOptions
        {
            SemanticEnabled = true,
            TextEmbeddingProfileKey = E5,
            Domains =
            {
                [RagDomains.NubArcaRepository] = new RagDomainSemanticOptions
                {
                    SemanticEnabled = true, TextEmbeddingProfileKey = Other,
                },
            },
        };
        var resolver = Resolver(options);

        Assert.Equal(E5, resolver.Resolve(RagDomainKey.ProductHelp).ProfileKey);
        Assert.Equal(Other, resolver.Resolve(RagDomainKey.NubArcaRepository).ProfileKey);
    }

    [Fact]
    public void An_Unmentioned_System_Domain_Inherits_The_Installation_Default()
    {
        // The compatibility path. An installation that configured
        // `Rag__SemanticEnabled` before per-domain settings existed keeps
        // working exactly as it did.
        var resolver = Resolver(new RagOptions
        {
            SemanticEnabled = true,
            TextEmbeddingProfileKey = E5,
        });

        Assert.Equal(E5, resolver.Resolve(RagDomainKey.ProductHelp).ProfileKey);
        Assert.Equal(E5, resolver.Resolve(RagDomainKey.NubArcaRepository).ProfileKey);
    }

    [Fact]
    public void A_Domain_Section_For_One_Domain_Does_Not_Opt_The_Others_Out()
    {
        // `SemanticEnabled` is NULLABLE for a reason. If "unmentioned" and
        // "false" were the same value, adding a `Domains` entry for the
        // repository would silently turn Product Help's semantic retrieval off —
        // a configuration change with an effect nobody asked for and nothing to
        // point at.
        var resolver = Resolver(new RagOptions
        {
            SemanticEnabled = true,
            TextEmbeddingProfileKey = E5,
            Domains =
            {
                [RagDomains.NubArcaRepository] = new RagDomainSemanticOptions
                {
                    SemanticEnabled = false,
                },
            },
        });

        Assert.True(resolver.Resolve(RagDomainKey.ProductHelp).Enabled);
        Assert.False(resolver.Resolve(RagDomainKey.NubArcaRepository).Enabled);
    }

    // ---- the owner-private asymmetry ----------------------------------------

    [Fact]
    public void UserDocuments_SemanticMustBeExplicitlyEnabled()
    {
        // Semantic switched on installation-wide, and an owner-private domain
        // that says nothing. It stays OFF: a person's own documents are not
        // covered by a decision made about product documentation.
        var resolver = Resolver(new RagOptions
        {
            SemanticEnabled = true,
            TextEmbeddingProfileKey = E5,
        });

        var settings = resolver.Resolve(RagDomainKey.UserDocuments);

        Assert.False(settings.Enabled);
        Assert.Null(settings.ProfileKey);
        // …while the system domains that DID inherit are unaffected, so this is
        // a rule about privacy class rather than a resolver that stopped working.
        Assert.True(resolver.Resolve(RagDomainKey.ProductHelp).Enabled);
    }

    [Fact]
    public void UserDocuments_DoesNotInheritTheProfileKeyEither()
    {
        // Enabled explicitly, profile inherited implicitly, would embed a
        // person's documents into whichever coordinate system Help happens to
        // use. Both halves have to be said out loud.
        var resolver = Resolver(new RagOptions
        {
            SemanticEnabled = true,
            TextEmbeddingProfileKey = E5,
            Domains =
            {
                [RagDomains.UserDocuments] = new RagDomainSemanticOptions
                {
                    SemanticEnabled = true,
                },
            },
        });

        Assert.False(resolver.Resolve(RagDomainKey.UserDocuments).Enabled);
    }

    [Fact]
    public void UserDocuments_WithBothStatedExplicitly_IsEnabled()
    {
        // The control: the rule is "say it", not "you may never".
        var resolver = Resolver(new RagOptions
        {
            Domains =
            {
                [RagDomains.UserDocuments] = new RagDomainSemanticOptions
                {
                    SemanticEnabled = true, TextEmbeddingProfileKey = E5,
                },
            },
        });

        var settings = resolver.Resolve(RagDomainKey.UserDocuments);

        Assert.True(settings.Enabled);
        Assert.Equal(E5, settings.ProfileKey);
    }

    // ---- degenerate configuration -------------------------------------------

    [Fact]
    public void EnabledWithNoProfile_IsNotEnabled()
    {
        // "Semantic on, no model" is not a state a caller should have to handle.
        // It resolves to disabled here so retrieval reports a reason instead of
        // guessing which profile was meant.
        var resolver = Resolver(new RagOptions { SemanticEnabled = true });

        Assert.False(resolver.Resolve(RagDomainKey.ProductHelp).Enabled);
        Assert.Null(resolver.Resolve(RagDomainKey.ProductHelp).ProfileKey);
    }

    [Fact]
    public void A_Blank_Profile_Key_Is_No_Profile()
    {
        var resolver = Resolver(new RagOptions
        {
            SemanticEnabled = true,
            TextEmbeddingProfileKey = "   ",
        });

        Assert.False(resolver.Resolve(RagDomainKey.ProductHelp).Enabled);
    }

    [Fact]
    public void An_Unknown_Domain_Resolves_To_Nothing()
    {
        // There is no default domain anywhere else in the substrate and there is
        // not one here: a typo must not inherit an installation's model.
        var resolver = Resolver(new RagOptions
        {
            SemanticEnabled = true,
            TextEmbeddingProfileKey = E5,
        });

        Assert.False(resolver.Resolve(new RagDomainKey("private-library")).Enabled);
    }

    [Fact]
    public void A_Domain_Key_Is_Matched_Case_Insensitively()
    {
        // Configuration keys arrive from environment variables and `.env` files.
        // An operator writing `Rag__Domains__Product-Help__…` meant the domain,
        // and silently resolving to the global default would be the wrong model
        // applied without a word.
        var resolver = Resolver(new RagOptions
        {
            Domains =
            {
                ["Product-Help"] = new RagDomainSemanticOptions
                {
                    SemanticEnabled = true, TextEmbeddingProfileKey = E5,
                },
            },
        });

        Assert.Equal(E5, resolver.Resolve(RagDomainKey.ProductHelp).ProfileKey);
    }

    private static IRagSemanticProfileResolver Resolver(RagOptions options)
        => new RagSemanticProfileResolver(RagDomainRegistry.Instance, Options.Create(options));
}
