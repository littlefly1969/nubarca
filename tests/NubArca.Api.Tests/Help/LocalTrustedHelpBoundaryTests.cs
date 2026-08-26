using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Assistant;
using NubArca.Api.Tests.Assistant;
using NubArca.Api.Tests.Endpoints;
using Xunit;

namespace NubArca.Api.Tests.Help;

// THE TEST THIS SLICE EXISTS FOR.
//
// A LocalTrusted model is ELIGIBLE for private context. Help does not give it
// any, because Help's operation policy is public product knowledge — and the two
// facts are separate, so proving one does not prove the other.
//
// It runs the whole stack against a plaintext, authless OpenAI-compatible
// endpoint, which is what an operator's own llama.cpp/Ollama/vLLM server
// actually is, and then reads the complete outbound body for the same sentinels
// the external test uses.
public sealed class LocalTrustedHelpBoundaryTests : IDisposable
{
    private const string FileSentinel = "PRIVATE_FILE_SENTINEL_X91";
    private const string PersonSentinel = "PRIVATE_PERSON_SENTINEL_Q72";
    private const string AlbumSentinel = "PRIVATE_ALBUM_SENTINEL_M44";
    private const string GpsSentinel = "PRIVATE_GPS_SENTINEL_K18";
    private static readonly string[] AllSentinels =
        { FileSentinel, PersonSentinel, AlbumSentinel, GpsSentinel };

    private readonly SqliteWebApplicationFactory _factory;
    private readonly string _corpusPath;
    private readonly CapturingProviderHandler _handler;

    public LocalTrustedHelpBoundaryTests()
    {
        _corpusPath = HelpPrivacyTests.WriteCorpus();
        _handler = new CapturingProviderHandler(
            _ => CapturingProviderHandler.Answer("An album is a named collection."));

        // Plaintext HTTP, no API key, a container-network hostname. Every one of
        // those would be refused for an External profile, and every one of them
        // is normal here.
        _factory = new SqliteWebApplicationFactory(new Dictionary<string, string?>
        {
            ["Assistant:Enabled"] = "true",
            ["Assistant:HelpModel"] = "local",
            ["Assistant:Models:local:Protocol"] = "OpenAiCompatible",
            ["Assistant:Models:local:Trust"] = "LocalTrusted",
            ["Assistant:Models:local:BaseUrl"] = "http://model.internal:11434",
            ["Assistant:Models:local:Model"] = "local-model-1",
            ["Assistant:Models:local:Label"] = "Local Model",
            ["Assistant:Help:CorpusPath"] = _corpusPath,
        });
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
    public async Task A_LocalTrusted_Endpoint_Answers_Over_Http_Without_A_Key()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();

        var response = await client.PostAsync(
            "/api/help/ai/chat", HelpPrivacyTests.Question("How do albums work?"));
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"ok\":true", body, StringComparison.Ordinal);
        Assert.Equal(1, _handler.Calls);
        Assert.Equal("http://model.internal:11434/v1/chat/completions", _handler.Url!.ToString());
        // No `Bearer ` with nothing after it: some local servers reject it.
        Assert.Null(_handler.Authorization);
    }

    [Fact]
    public async Task LocalTrusted_Help_Still_Receives_No_Private_Library_Data()
    {
        // Trust widened what the MODEL is eligible for. It did not widen what
        // HELP sends, and this slice deliberately implements no feature that
        // uses the extra eligibility.
        var (ownerId, client) = await _factory.CreateAuthenticatedClientAsync();
        await HelpPrivacyTests.SeedPrivateLibraryAsync(_factory, ownerId);

        var response = await client.PostAsync(
            "/api/help/ai/chat", HelpPrivacyTests.Question("How do albums work?"));
        response.EnsureSuccessStatusCode();

        Assert.Equal(1, _handler.Calls);
        var outbound = _handler.Body!;
        foreach (var sentinel in AllSentinels)
        {
            Assert.DoesNotContain(sentinel, outbound, StringComparison.Ordinal);
        }
        // Not empty: the approved public evidence and the question are there.
        Assert.Contains("NubArca is a self-hosted personal media library", outbound, StringComparison.Ordinal);
        Assert.Contains("How do albums work?", outbound, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_LocalTrusted_Request_Is_Still_Physically_Tool_Free()
    {
        // The tool-free contract is a property of the transport, not of the
        // trust classification: a local model does not get tools either, because
        // there is nowhere in the request to put them.
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        await client.PostAsync("/api/help/ai/chat", HelpPrivacyTests.Question("How do albums work?"));

        var body = JsonDocument.Parse(_handler.Body!).RootElement;
        foreach (var forbidden in new[] { "tools", "functions", "tool_choice", "attachments" })
        {
            Assert.False(body.TryGetProperty(forbidden, out _));
        }
    }

    [Fact]
    public async Task The_Status_Says_LocalTrusted_So_The_Disclosure_Can_Be_True()
    {
        // The UI used to say "external" unconditionally. That stopped being true
        // the moment this configuration became possible, and a privacy
        // disclosure that is wrong is worse than none.
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        var status = await (await client.GetAsync("/api/help/ai/status")).Content.ReadAsStringAsync();

        Assert.Contains("\"modelBoundary\":\"localTrusted\"", status, StringComparison.Ordinal);
        Assert.Contains("\"enabled\":true", status, StringComparison.Ordinal);
        Assert.Contains("Local Model", status, StringComparison.Ordinal);
        // Still no endpoint, model id or internal hostname.
        foreach (var secret in new[] { "model.internal", "local-model-1", "11434" })
        {
            Assert.DoesNotContain(secret, status, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Weak_Evidence_Does_Not_Call_A_Local_Model_Either()
    {
        // The privacy cost is lower and a confidently wrong answer is exactly as
        // wrong, so the no-hallucination gate is not an external-only rule.
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();

        var response = await client.PostAsync(
            "/api/help/ai/chat", HelpPrivacyTests.Question("what is the weather forecast tomorrow?"));
        response.EnsureSuccessStatusCode();

        Assert.Equal(0, _handler.Calls);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains(AssistantFailureReasons.NoSupportingKnowledge, body, StringComparison.Ordinal);
    }
}
