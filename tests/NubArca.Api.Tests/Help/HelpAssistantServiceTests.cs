using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NubArca.Api.Assistant;
using NubArca.Api.Help;
using NubArca.Api.Rag;
using NubArca.Api.Rag.ProductHelp;
using Xunit;

namespace NubArca.Api.Tests.Help;

// The Help service itself: what it sends, what it refuses to send, and the
// cases where the right behaviour is to make NO model call at all.
//
// Unit level on purpose. The endpoint tests prove the same gates through the
// real HTTP stack; these prove them without a web host, so a regression names
// the decision rather than "a request had zero calls".
public sealed class HelpAssistantServiceTests
{
    /// Counts calls and records the last conversation. There is nowhere in
    /// IAssistantTextModel to pass a tool, so a fake cannot accidentally model
    /// one either.
    private sealed class CountingModel : IAssistantTextModel
    {
        public int Calls { get; private set; }
        public IReadOnlyList<AssistantMessage>? Last { get; private set; }
        public AssistantModelProfile? UsedProfile { get; private set; }

        public Task<AssistantCompletion> CompleteAsync(
            AssistantModelProfile profile,
            IReadOnlyList<AssistantMessage> messages,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            Last = messages;
            UsedProfile = profile;
            return Task.FromResult(AssistantCompletion.Success("an answer"));
        }
    }

    private sealed class StubRetriever : IRagRetriever
    {
        public StubRetriever(bool available, RagRetrievalResult result)
        {
            IsAvailable = available;
            Result = result;
        }

        public bool IsAvailable { get; }
        public RagQuery? Asked { get; private set; }
        private RagRetrievalResult Result { get; }

        public Task<RagRetrievalResult> RetrieveAsync(
            RagQuery query, CancellationToken cancellationToken = default)
        {
            Asked = query;
            return Task.FromResult(IsAvailable
                ? Result
                : RagRetrievalResult.Unavailable(query.Domain, RagFailureReasons.IndexUnavailable));
        }

        public Task<RagDomainStatus> GetStatusAsync(
            RagDomainKey domain, Guid? ownerUserId = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new RagDomainStatus(
                domain, IsAvailable, IsAvailable ? "r" : null, 1, 1, null, 0, 0, false, null));
    }

    private static RagRetrievalResult Strong(params RagEvidence[] evidence)
        => new(RagDomainKey.ProductHelp, RagRetrievalOutcome.Strong, evidence, RagRetrievalModes.Lexical);

    private static RagEvidence Evidence(string path = "docs/help/faces.md", string section = "Ignorati")
        => new(
            Id: $"{path}#1",
            Domain: RagDomainKey.ProductHelp,
            Path: path,
            Title: "Volti e persone",
            Section: section,
            Text: "Con Ripristina i volti ignorati tornano fra i volti non assegnati.",
            Feature: "faces",
            SourceKind: ProductHelpVocabulary.SourceKind.UserGuide,
            Audience: ProductHelpVocabulary.Audience.User,
            Intent: ProductHelpVocabulary.Intent.HowTo,
            Language: ProductHelpVocabulary.Language.Italian,
            Score: 12.5);

    private static (HelpAssistantService Service, CountingModel Model, StubRetriever Knowledge) Build(
        RagRetrievalResult? retrieval = null,
        bool knowledgeAvailable = true,
        AssistantOptions? assistant = null,
        AssistantHelpOptions? bounds = null)
    {
        var model = new CountingModel();
        var knowledge = new StubRetriever(
            knowledgeAvailable,
            retrieval ?? Strong(Evidence()));

        var options = assistant ?? new AssistantOptions
        {
            Enabled = true,
            HelpModel = "help-default",
            Models =
            {
                ["help-default"] = new AssistantModelOptions
                {
                    Trust = nameof(AssistantModelTrust.External),
                    BaseUrl = "https://provider.example",
                    ApiKey = "k",
                    Model = "m",
                    Label = "Test Provider",
                },
            },
        };
        if (bounds is not null) options.Help = bounds;

        var resolver = new AssistantModelResolver(
            Options.Create(options),
            Options.Create(new ExternalHelpOptions()),
            NullLogger<AssistantModelResolver>.Instance);

        return (
            new HelpAssistantService(model, knowledge, resolver, NullLogger<HelpAssistantService>.Instance),
            model,
            knowledge);
    }

    // ---- the no-call gates -------------------------------------------------

    [Fact]
    public async Task RevisionMismatch_DoesNotCallModel()
    {
        // A corpus built from a different revision leaves the retriever
        // unavailable, and Help refuses rather than buying an answer that would
        // describe a release this installation is not running.
        var (service, model, _) = Build(knowledgeAvailable: false);

        var answer = await service.AskAsync("come uso i volti?", Array.Empty<HelpTurn>());

        Assert.False(answer.Ok);
        Assert.Equal(AssistantFailureReasons.KnowledgeUnavailable, answer.Reason);
        Assert.Equal(0, model.Calls);
        Assert.Empty(answer.Sources);
    }

    [Fact]
    public async Task NoStrongEvidence_DoesNotCallModel()
    {
        var (service, model, _) = Build(
            RagRetrievalResult.None(RagDomainKey.ProductHelp, RagRetrievalModes.Lexical));

        var answer = await service.AskAsync("quanto costa un abbonamento?", Array.Empty<HelpTurn>());

        Assert.False(answer.Ok);
        // Distinct from KnowledgeUnavailable: the corpus is fine and nobody has
        // anything to fix.
        Assert.Equal(AssistantFailureReasons.NoSupportingKnowledge, answer.Reason);
        Assert.Equal(0, model.Calls);
    }

    [Fact]
    public async Task A_Disabled_Assistant_Does_Not_Call_A_Model()
    {
        var (service, model, _) = Build(assistant: new AssistantOptions { Enabled = false });

        var answer = await service.AskAsync("come uso i volti?", Array.Empty<HelpTurn>());

        Assert.False(answer.Ok);
        Assert.Equal(AssistantFailureReasons.Disabled, answer.Reason);
        Assert.Equal(0, model.Calls);
        Assert.False((await service.GetStatusAsync()).Enabled);
    }

    [Fact]
    public async Task An_Empty_Question_Does_Not_Call_A_Model()
    {
        var (service, model, _) = Build();
        var answer = await service.AskAsync("   ", Array.Empty<HelpTurn>());
        Assert.False(answer.Ok);
        Assert.Equal(0, model.Calls);
    }

    // ---- what it asks for, and what it sends --------------------------------

    [Fact]
    public async Task It_Retrieves_Specifically_From_Product_Help()
    {
        var (service, _, knowledge) = Build();

        await service.AskAsync("come uso i volti?", Array.Empty<HelpTurn>());

        Assert.NotNull(knowledge.Asked);
        // A constant, not a parameter reachable from the request: the domain a
        // feature may read is a property of the feature.
        Assert.Equal(RagDomainKey.ProductHelp, knowledge.Asked!.Domain);
    }

    [Fact]
    public async Task The_Prompt_Carries_The_Evidence_And_The_Question_And_Nothing_Else()
    {
        var (service, model, _) = Build();

        var answer = await service.AskAsync("come ripristino i volti?", new[]
        {
            new HelpTurn(true, "ciao"),
            new HelpTurn(false, "Chiedimi di NubArca."),
        });

        Assert.True(answer.Ok);
        Assert.Equal(1, model.Calls);
        var messages = model.Last!;
        Assert.Equal(AssistantRole.System, messages[0].Role);
        Assert.Contains("Con Ripristina i volti ignorati", messages[0].Text, StringComparison.Ordinal);
        Assert.Equal(new[] { AssistantRole.System, AssistantRole.User, AssistantRole.Assistant, AssistantRole.User },
            messages.Select(m => m.Role).ToArray());
        Assert.Equal("come ripristino i volti?", messages[^1].Text);
    }

    [Fact]
    public async Task Sources_Name_The_Section_So_An_Answer_Can_Be_Traced()
    {
        var (service, _, _) = Build(Strong(Evidence(), Evidence(section: "Gruppi suggeriti")));

        var answer = await service.AskAsync("come uso i volti?", Array.Empty<HelpTurn>());

        Assert.Equal(
            new[] { "docs/help/faces.md · Ignorati", "docs/help/faces.md · Gruppi suggeriti" },
            answer.Sources);
    }

    [Fact]
    public async Task A_Long_Question_Is_Truncated_Rather_Than_Sent_Whole()
    {
        var (service, model, _) = Build(bounds: new AssistantHelpOptions { MaxQuestionCharacters = 40 });

        await service.AskAsync(new string('a', 5000), Array.Empty<HelpTurn>());

        Assert.Equal(40, model.Last![^1].Text.Length);
    }

    [Fact]
    public async Task History_Is_Bounded_By_Turns_And_By_Characters()
    {
        // A client is not trusted to bound itself: the browser owns this
        // conversation, so an oversized history is a request shape rather than
        // an impossibility.
        var (service, model, _) = Build(bounds: new AssistantHelpOptions { MaxHistoryTurns = 2 });

        var history = Enumerable.Range(0, 20)
            .Select(i => new HelpTurn(i % 2 == 0, $"turn {i}"))
            .ToList();
        await service.AskAsync("come uso i volti?", history);

        // system + 2 history turns + the question.
        Assert.Equal(4, model.Last!.Count);
        Assert.Contains("turn 19", model.Last[^2].Text, StringComparison.Ordinal);

        var (tight, tightModel, _) = Build(
            bounds: new AssistantHelpOptions { MaxHistoryCharacters = 0 });
        await tight.AskAsync("come uso i volti?", history);
        Assert.Equal(2, tightModel.Last!.Count);
    }

    // ---- status -------------------------------------------------------------

    [Theory]
    [InlineData(nameof(AssistantModelTrust.External), "external")]
    [InlineData(nameof(AssistantModelTrust.LocalTrusted), "localTrusted")]
    public async Task Status_Reports_The_Boundary_So_The_Disclosure_Can_Be_True(
        string trust, string expected)
    {
        var (service, _, _) = Build(assistant: new AssistantOptions
        {
            Enabled = true,
            HelpModel = "m",
            Models =
            {
                ["m"] = new AssistantModelOptions
                {
                    Trust = trust,
                    BaseUrl = trust == nameof(AssistantModelTrust.External)
                        ? "https://provider.example"
                        : "http://model.internal:11434",
                    ApiKey = trust == nameof(AssistantModelTrust.External) ? "k" : string.Empty,
                    Model = "m",
                    Label = "A Label",
                },
            },
        });

        var status = await service.GetStatusAsync();
        Assert.True(status.Enabled);
        Assert.Equal(expected, status.ModelBoundary);
        Assert.Equal("A Label", status.ProviderLabel);
    }

    [Fact]
    public async Task A_Disabled_Assistant_Reports_The_Safer_Boundary()
    {
        // Nothing is disclosed in this state, and of the two strings this is the
        // one to be wrong with.
        var (service, _, _) = Build(assistant: new AssistantOptions { Enabled = false });
        Assert.Equal("external", (await service.GetStatusAsync()).ModelBoundary);
    }
}
