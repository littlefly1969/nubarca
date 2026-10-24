using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NubArca.Api.Assistant;
using Xunit;

namespace NubArca.Api.Tests.Assistant;

// Before 0.4.6 the document assistant's limits were configured as
// `Assistant:Help:*`. An upgrade must not silently loosen them to the defaults.
public sealed class AssistantLegacyBoundsTests
{
    private static AssistantModelResolver Resolver(Dictionary<string, string?> values)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        AssistantOptions.Register(services, configuration);
        services.AddSingleton<AssistantModelResolver>();
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
        return services.BuildServiceProvider().GetRequiredService<AssistantModelResolver>();
    }

    private static Dictionary<string, string?> EnabledDocumentAssistant() => new()
    {
        ["Assistant:Enabled"] = "true",
        ["Assistant:PrivateKnowledgeModel"] = "documents-local",
        ["Assistant:Models:documents-local:Trust"] = "LocalTrusted",
        ["Assistant:Models:documents-local:BaseUrl"] = "http://model.internal:8080",
        ["Assistant:Models:documents-local:Model"] = "m",
    };

    [Fact]
    public void A_Restrictive_Legacy_Help_Bound_Still_Applies()
    {
        var values = EnabledDocumentAssistant();
        values["Assistant:Help:MaxEvidenceCharacters"] = "1000";
        values["Assistant:Help:MaxHistoryTurns"] = "2";
        values["Assistant:Help:CorpusPath"] = "/app/help-corpus.json";  // retired, ignored

        var resolver = Resolver(values);

        Assert.True(resolver.PrivateKnowledgeModel.IsUsable);
        Assert.Equal(1000, resolver.Bounds.EffectiveEvidenceCharacters);
        Assert.Equal(2, resolver.Bounds.EffectiveHistoryTurns);
        // Untouched keys keep their defaults.
        Assert.Equal(new AssistantBoundsOptions().MaxEvidenceChunks, resolver.Bounds.MaxEvidenceChunks);
    }

    [Fact]
    public void A_Bound_Set_Under_The_New_Name_Wins()
    {
        var values = EnabledDocumentAssistant();
        values["Assistant:Help:MaxEvidenceCharacters"] = "1000";
        values["Assistant:Bounds:MaxEvidenceCharacters"] = "3000";

        Assert.Equal(3000, Resolver(values).Bounds.EffectiveEvidenceCharacters);
    }

    [Fact]
    public void No_Legacy_Keys_Means_The_Defaults()
        => Assert.Equal(
            new AssistantBoundsOptions().MaxEvidenceCharacters,
            Resolver(EnabledDocumentAssistant()).Bounds.MaxEvidenceCharacters);
}
