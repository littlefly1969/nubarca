using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Assistant;
using NubArca.Api.Data;
using NubArca.Api.Domain.Rag;
using NubArca.Api.Rag;
using NubArca.Api.Rag.Domains;
using NubArca.Api.Tests.Assistant;
using NubArca.Api.Tests.Endpoints;
using Xunit;

namespace NubArca.Api.Tests.Help;

// Domain isolation, at the bytes-on-the-wire boundary.
//
// Slice 2 gave NubArca a second retrieval domain, and `nubarca-repository` is
// `SystemInternal`: an External model must never be grounded on it. That claim
// is worth exactly as much as the outbound HTTP body, so this test indexes an
// unmistakable repository-only sentinel into the REAL index, asks an ordinary
// Product Help question through the REAL endpoint, and reads the complete
// request the fake provider received.
//
// The sentinel is placed in a source that would rank well for the question that
// is asked — it is about faces, in Italian — so an isolation failure would
// actually surface it. A sentinel about something unrelated would be absent for
// the wrong reason.
public sealed class ExternalRepositoryIsolationTests : IDisposable
{
    private const string RepositorySentinel = "REPOSITORY_ONLY_SENTINEL_R47";
    private const string PublicMarker = "NubArca is a self-hosted personal media library";

    private readonly SqliteWebApplicationFactory _factory;
    private readonly string _corpusPath;
    private readonly CapturingProviderHandler _handler;

    public ExternalRepositoryIsolationTests()
    {
        _corpusPath = HelpPrivacyTests.WriteCorpus();
        _handler = new CapturingProviderHandler(
            _ => CapturingProviderHandler.Answer("An album is a named collection."));

        _factory = new SqliteWebApplicationFactory(
            HelpPrivacyTests.ExternalConfiguration(_corpusPath, "SUPER_SECRET_KEY_R47"));
        _factory.ConfigureExtraServices = services =>
            services.AddHttpClient<IAssistantTextModel, OpenAiCompatibleTextModel>()
                .ConfigurePrimaryHttpMessageHandler(() => _handler);
        _factory.EnsureDatabaseCreated();
    }

    public void Dispose()
    {
        _factory.Dispose();
        try { File.Delete(_corpusPath); } catch (IOException) { }
    }

    [Fact]
    public async Task ExternalProductHelp_DoesNotSendRepositoryOnlyEvidence()
    {
        SeedRepositoryOnlySource();
        var client = await AuthenticatedAsync();

        var response = await client.PostAsync(
            "/api/help/ai/chat", Question("How do albums work?"));
        response.EnsureSuccessStatusCode();

        Assert.Equal(1, _handler.Calls);
        var body = _handler.Body!;

        // The bytes that actually left NubArca.
        Assert.DoesNotContain(RepositorySentinel, body, StringComparison.Ordinal);
        Assert.DoesNotContain("nubarca-repository", body, StringComparison.Ordinal);

        // …and the request was not simply empty, which is what makes the absence
        // mean something: approved public product text is there, and so is the
        // question the person typed.
        Assert.Contains(PublicMarker, body, StringComparison.Ordinal);
        Assert.Contains("How do albums work?", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_Italian_Faces_Question_Cannot_Reach_Repository_Evidence_Either()
    {
        // The sentinel source is ABOUT faces and is written in Italian, so it
        // would rank for this question if domain isolation were not doing the
        // work. Absence here is absence for the right reason.
        SeedRepositoryOnlySource();
        var client = await AuthenticatedAsync();

        await client.PostAsync(
            "/api/help/ai/chat", Question("come faccio a utilizzare la funzione dei volti?"));

        if (_handler.Calls == 0)
        {
            // The fixture corpus is about albums, so "no strong evidence" is a
            // legitimate outcome — and a request that was never made cannot
            // have leaked anything.
            return;
        }
        Assert.DoesNotContain(RepositorySentinel, _handler.Body!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExternalProductHelp_RequestStillHasNoTools()
    {
        // Preserved from Slice 1 and re-asserted here, because a retrieval
        // platform is exactly the kind of change that grows a `tools` array by
        // accident. The fields are ABSENT rather than empty.
        SeedRepositoryOnlySource();
        var client = await AuthenticatedAsync();

        await client.PostAsync("/api/help/ai/chat", Question("How do albums work?"));

        var body = _handler.Body!;
        Assert.DoesNotContain("\"tools\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"functions\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"tool_choice\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_External_Model_Is_Refused_The_Repository_Domain_Before_The_Provider_Is_Called()
    {
        // The structural gate, asserted where it applies. The Assistant checks
        // trust ∩ domain policy over the EVIDENCE, before a prompt exists — so
        // a future caller that asked for `nubarca-repository` with an External
        // model stops here rather than at the point of serialization.
        SeedRepositoryOnlySource();
        var repository = RagDomainRegistry.NubArcaRepository;

        Assert.False(AssistantRagPolicy.MayGroundOn(AssistantModelTrust.External, repository));

        using var scope = _factory.Services.CreateScope();
        var retriever = scope.ServiceProvider.GetRequiredService<IRagRetriever>();
        var result = await retriever.RetrieveAsync(new RagQuery(
            RagDomainKey.NubArcaRepository, "volti gruppi suggeriti assegna nome", 5, 8000));

        // Retrieval itself works for this domain — that is the point of having
        // it — and the evidence it returns is stamped `nubarca-repository`, so
        // the Assistant's gate refuses it for an External model.
        Assert.True(result.HasStrongEvidence);
        Assert.Contains(result.Evidence, e => e.Text.Contains(RepositorySentinel, StringComparison.Ordinal));
        Assert.Equal(
            RagFailureReasons.DomainNotAllowed,
            AssistantRagPolicy.Refuse(AssistantModelTrust.External, repository, result.Evidence));

        // The SAME evidence is permitted to a LocalTrusted model, which is what
        // makes this a policy rather than a blanket refusal.
        Assert.Null(AssistantRagPolicy.Refuse(
            AssistantModelTrust.LocalTrusted, repository, result.Evidence));
    }

    [Fact]
    public async Task Product_Help_Retrieval_Never_Returns_Repository_Chunks()
    {
        SeedRepositoryOnlySource();

        using var scope = _factory.Services.CreateScope();
        var retriever = scope.ServiceProvider.GetRequiredService<IRagRetriever>();
        var result = await retriever.RetrieveAsync(new RagQuery(
            RagDomainKey.ProductHelp, "volti gruppi suggeriti assegna nome", 5, 8000));

        Assert.All(result.Evidence, e => Assert.Equal(RagDomainKey.ProductHelp, e.Domain));
        Assert.DoesNotContain(result.Evidence,
            e => e.Text.Contains(RepositorySentinel, StringComparison.Ordinal));
    }

    // ---- fixture -------------------------------------------------------------

    /// A source that exists ONLY in `nubarca-repository`, written to rank for a
    /// faces question so that isolation is what keeps it out rather than luck.
    private void SeedRepositoryOnlySource()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        if (db.RagSources.Any()) return;

        var source = new RagSource
        {
            Id = Guid.NewGuid(),
            SourceKey = "src/NubArca.Api/Ai/Faces/PeopleService.cs",
            Path = "src/NubArca.Api/Ai/Faces/PeopleService.cs",
            Title = "PeopleService.cs",
            SourceKind = RagSourceKinds.SourceCode,
            Revision = "sentinel-revision",
            ContentHash = RagHash.Sha256Hex(RepositorySentinel),
            Language = RagLanguages.Italian,
            CodeLanguage = RagCodeLanguages.CSharp,
            CreatedAt = DateTime.UtcNow,
        };
        db.RagSources.Add(source);
        db.RagDomainSources.Add(new RagDomainSource
        {
            Id = Guid.NewGuid(),
            DomainKey = RagDomains.NubArcaRepository,
            SourceId = source.Id,
            Priority = 65,
            CreatedAt = DateTime.UtcNow,
        });
        db.RagChunks.Add(new RagChunk
        {
            Id = Guid.NewGuid(),
            SourceId = source.Id,
            Ordinal = 1,
            Heading = "PeopleService › AssignAsync (L1–L40)",
            Text = $"{RepositorySentinel}: gruppi suggeriti, volti, assegna nome, "
                   + "persone e album sono gestiti da questo servizio interno di NubArca.",
            TextHash = RagHash.Sha256Hex(RepositorySentinel),
            Language = RagLanguages.Italian,
            CreatedAt = DateTime.UtcNow,
        });
        db.SaveChanges();
    }

    private async Task<HttpClient> AuthenticatedAsync()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync(
            $"iso-{Guid.NewGuid():N}@example.com");
        return client;
    }

    private static JsonContent Question(string message)
        => JsonContent.Create(new { message, history = Array.Empty<object>() });
}
