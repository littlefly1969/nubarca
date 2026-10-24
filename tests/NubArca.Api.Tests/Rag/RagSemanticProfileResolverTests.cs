using Microsoft.Extensions.Options;
using NubArca.Api.Rag;
using NubArca.Api.Rag.Domains;
using Xunit;

namespace NubArca.Api.Tests.Rag;

// Whether owner-private document search embeds, and with which model.
//
// There is no installation-wide default to inherit: semantic retrieval over a
// person's own documents is on only when BOTH the switch and the profile are
// stated for `user-documents` itself. A decision made about anything else is
// not a decision about somebody's documents.
public sealed class RagSemanticProfileResolverTests
{
    private const string E5 = "rag-text-multilingual-e5-small-v1";

    [Fact]
    public void UserDocuments_Is_Lexical_When_Nothing_Is_Configured()
    {
        var settings = Resolver(new RagOptions()).Resolve(RagDomainKey.UserDocuments);

        Assert.False(settings.Enabled);
        Assert.Null(settings.ProfileKey);
    }

    [Fact]
    public void UserDocuments_WithBothStatedExplicitly_IsEnabled()
    {
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

    [Fact]
    public void UserDocuments_Explicitly_Disabled_Stays_Off_Even_With_A_Profile()
    {
        var resolver = Resolver(new RagOptions
        {
            Domains =
            {
                [RagDomains.UserDocuments] = new RagDomainSemanticOptions
                {
                    SemanticEnabled = false, TextEmbeddingProfileKey = E5,
                },
            },
        });

        Assert.False(resolver.Resolve(RagDomainKey.UserDocuments).Enabled);
    }

    // ---- degenerate configuration -------------------------------------------

    [Fact]
    public void EnabledWithNoProfile_IsNotEnabled()
    {
        // "Semantic on, no model" is not a state a caller should have to handle.
        // It resolves to disabled so retrieval reports a reason instead of
        // guessing which profile was meant.
        var resolver = Resolver(new RagOptions
        {
            Domains = { [RagDomains.UserDocuments] = new RagDomainSemanticOptions { SemanticEnabled = true } },
        });

        Assert.False(resolver.Resolve(RagDomainKey.UserDocuments).Enabled);
        Assert.Null(resolver.Resolve(RagDomainKey.UserDocuments).ProfileKey);
    }

    [Fact]
    public void A_Blank_Profile_Key_Is_No_Profile()
    {
        var resolver = Resolver(new RagOptions
        {
            Domains =
            {
                [RagDomains.UserDocuments] = new RagDomainSemanticOptions
                {
                    SemanticEnabled = true, TextEmbeddingProfileKey = "   ",
                },
            },
        });

        Assert.False(resolver.Resolve(RagDomainKey.UserDocuments).Enabled);
    }

    [Fact]
    public void An_Unknown_Domain_Resolves_To_Nothing()
    {
        // There is no default domain anywhere else in the substrate and there is
        // not one here: a typo, or a removed domain, gets no model.
        var resolver = Resolver(new RagOptions
        {
            Domains =
            {
                ["product-help"] = new RagDomainSemanticOptions
                {
                    SemanticEnabled = true, TextEmbeddingProfileKey = E5,
                },
            },
        });

        Assert.False(resolver.Resolve(new RagDomainKey("product-help")).Enabled);
        Assert.False(resolver.Resolve(new RagDomainKey("private-library")).Enabled);
    }

    [Fact]
    public void A_Domain_Key_Is_Matched_Case_Insensitively()
    {
        // Configuration keys arrive from environment variables and `.env` files.
        // An operator writing `Rag__Domains__User-Documents__…` meant the domain.
        var resolver = Resolver(new RagOptions
        {
            Domains =
            {
                ["User-Documents"] = new RagDomainSemanticOptions
                {
                    SemanticEnabled = true, TextEmbeddingProfileKey = E5,
                },
            },
        });

        Assert.Equal(E5, resolver.Resolve(RagDomainKey.UserDocuments).ProfileKey);
    }

    private static IRagSemanticProfileResolver Resolver(RagOptions options)
        => new RagSemanticProfileResolver(RagDomainRegistry.Instance, Options.Create(options));
}
