using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using NubArca.Api.Assistant;
using Xunit;

namespace NubArca.Api.Tests.Assistant;

/// Captures the COMPLETE outbound request — URL, headers and raw body bytes —
/// and answers with whatever the test tells it to.
///
/// Tests assert on the captured BYTES rather than on an intermediate DTO,
/// because the DTO is the thing under test: a field that is serialized but not
/// modelled, or modelled but not serialized, is exactly the mistake worth
/// catching, and only the wire shows it.
internal sealed class CapturingProviderHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

    public CapturingProviderHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        => _respond = respond;

    public string? Body { get; private set; }
    public Uri? Url { get; private set; }
    public string? Authorization { get; private set; }
    public int Calls { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls++;
        Url = request.RequestUri;
        Authorization = request.Headers.Authorization is null
            ? null
            : $"{request.Headers.Authorization.Scheme} {request.Headers.Authorization.Parameter}";
        Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        return _respond(request);
    }

    public static HttpResponseMessage Json(HttpStatusCode status, string json)
        => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Answer(string text) => Json(HttpStatusCode.OK,
        JsonSerializer.Serialize(new { choices = new[] { new { message = new { role = "assistant", content = text } } } }));
}

// The OpenAI-compatible protocol adapter, exercised against a fake endpoint.
//
// No test here reaches a real provider: an automated suite that depends on a
// third party is a suite that fails for reasons that have nothing to do with the
// code, and one that quietly costs money.
public sealed class AssistantTextModelContractTests
{
    private const string Key = "SUPER_SECRET_EXTERNAL_HELP_KEY_XYZ";

    internal static AssistantModelProfile ExternalProfile(
        string baseUrl = "https://provider.example/",
        string apiKey = Key,
        int maxOutputTokens = 321,
        int timeoutSeconds = 30)
        => new(
            Key: "help-default",
            Protocol: AssistantModelProtocol.OpenAiCompatible,
            Trust: AssistantModelTrust.External,
            BaseUrl: baseUrl,
            ApiKey: apiKey,
            Model: "test-model-1",
            Label: "Test Provider",
            TimeoutSeconds: timeoutSeconds,
            MaxOutputTokens: maxOutputTokens);

    private static (OpenAiCompatibleTextModel Model, CapturingProviderHandler Handler) Build(
        Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new CapturingProviderHandler(respond);
        var model = new OpenAiCompatibleTextModel(
            new HttpClient(handler),
            NullLogger<OpenAiCompatibleTextModel>.Instance);
        return (model, handler);
    }

    private static readonly IReadOnlyList<AssistantMessage> Conversation = new[]
    {
        new AssistantMessage(AssistantRole.System, "system rules"),
        new AssistantMessage(AssistantRole.User, "earlier question"),
        new AssistantMessage(AssistantRole.Assistant, "earlier answer"),
        new AssistantMessage(AssistantRole.User, "how do albums work?"),
    };

    [Fact]
    public async Task Posts_The_Compatible_Shape_And_Extracts_The_Answer()
    {
        var (model, handler) = Build(_ => CapturingProviderHandler.Answer("Albums group photos."));

        var result = await model.CompleteAsync(ExternalProfile(), Conversation);

        Assert.True(result.Ok);
        Assert.Equal("Albums group photos.", result.Text);

        Assert.Equal("https://provider.example/v1/chat/completions", handler.Url!.ToString());
        Assert.Equal($"Bearer {Key}", handler.Authorization);

        var body = JsonDocument.Parse(handler.Body!).RootElement;
        Assert.Equal("test-model-1", body.GetProperty("model").GetString());
        Assert.Equal(321, body.GetProperty("max_tokens").GetInt32());

        var messages = body.GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal(4, messages.Count);
        Assert.Equal(new[] { "system", "user", "assistant", "user" },
            messages.Select(m => m.GetProperty("role").GetString()).ToArray());
        Assert.Equal("how do albums work?", messages[^1].GetProperty("content").GetString());
    }

    // The model is given NO capabilities — not empty ones, absent ones.
    [Fact]
    public async Task The_Request_Carries_No_Tool_Surface_At_All()
    {
        var (model, handler) = Build(_ => CapturingProviderHandler.Answer("ok"));
        await model.CompleteAsync(ExternalProfile(), Conversation);

        var body = JsonDocument.Parse(handler.Body!).RootElement;
        foreach (var forbidden in new[]
                 {
                     "tools", "functions", "tool_choice", "function_call",
                     "parallel_tool_calls", "response_format", "attachments",
                 })
        {
            Assert.False(
                body.TryGetProperty(forbidden, out _),
                $"the outbound request must not contain '{forbidden}' — not even empty");
        }

        // And the raw text does not mention them either, which catches a field
        // serialized under a name the DTO does not model.
        foreach (var forbidden in new[] { "\"tools\"", "\"functions\"", "\"tool_choice\"" })
        {
            Assert.DoesNotContain(forbidden, handler.Body!, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_LocalTrusted_Endpoint_Works_Over_Http_Without_A_Key()
    {
        // What an operator's own llama.cpp/Ollama/vLLM server usually is: plain
        // HTTP on the container network, no auth. Refusing it would push people
        // towards declaring a real external provider "local" to make it work.
        var (model, handler) = Build(_ => CapturingProviderHandler.Answer("local answer"));
        var local = ExternalProfile() with
        {
            Trust = AssistantModelTrust.LocalTrusted,
            BaseUrl = "http://model.internal:11434",
            ApiKey = string.Empty,
        };

        var result = await model.CompleteAsync(local, Conversation);

        Assert.True(result.Ok);
        Assert.Equal("http://model.internal:11434/v1/chat/completions", handler.Url!.ToString());
        // No empty `Bearer `: some local servers reject the header outright when
        // it carries nothing.
        Assert.Null(handler.Authorization);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, AssistantFailureReasons.ProviderUnauthorized)]
    [InlineData(HttpStatusCode.Forbidden, AssistantFailureReasons.ProviderUnauthorized)]
    [InlineData(HttpStatusCode.TooManyRequests, AssistantFailureReasons.ProviderRateLimited)]
    [InlineData(HttpStatusCode.InternalServerError, AssistantFailureReasons.ProviderUnavailable)]
    [InlineData(HttpStatusCode.BadGateway, AssistantFailureReasons.ProviderUnavailable)]
    public async Task Provider_Failures_Become_Sanitized_Reasons(HttpStatusCode status, string reason)
    {
        // The provider's own error body is deliberately something that must never
        // reach a user: it quotes the request back, key and all.
        var leaky = JsonSerializer.Serialize(new { error = new { message = $"bad request with {Key}" } });
        var (model, _) = Build(_ => CapturingProviderHandler.Json(status, leaky));

        var result = await model.CompleteAsync(ExternalProfile(), Conversation);

        Assert.False(result.Ok);
        Assert.Equal(reason, result.Reason);
        Assert.Null(result.Text);
        Assert.DoesNotContain(Key, result.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Malformed_Body_Is_Reported_As_Malformed_Not_Thrown()
    {
        var (model, _) = Build(_ => CapturingProviderHandler.Json(HttpStatusCode.OK, "{ this is not json"));
        var result = await model.CompleteAsync(ExternalProfile(), Conversation);
        Assert.False(result.Ok);
        Assert.Equal(AssistantFailureReasons.ProviderMalformed, result.Reason);
    }

    [Fact]
    public async Task An_Empty_Answer_Is_Not_Reported_As_Success()
    {
        // Well-formed, successful, and useless. Returning ok=true with nothing in
        // it would show the user an empty bubble and no explanation.
        var (model, _) = Build(_ => CapturingProviderHandler.Json(
            HttpStatusCode.OK, JsonSerializer.Serialize(new { choices = Array.Empty<object>() })));
        var result = await model.CompleteAsync(ExternalProfile(), Conversation);
        Assert.False(result.Ok);
        Assert.Equal(AssistantFailureReasons.ProviderEmpty, result.Reason);
    }

    [Fact]
    public async Task A_Slow_Provider_Times_Out_Rather_Than_Hanging()
    {
        var (model, _) = Build(_ =>
        {
            Thread.Sleep(TimeSpan.FromSeconds(2));
            return CapturingProviderHandler.Answer("too late");
        });

        var result = await model.CompleteAsync(ExternalProfile(timeoutSeconds: 1), Conversation);
        Assert.False(result.Ok);
        Assert.Equal(AssistantFailureReasons.ProviderTimeout, result.Reason);
    }

    [Fact]
    public async Task A_Cancelled_Caller_Is_Not_Mistaken_For_A_Timeout()
    {
        using var cts = new CancellationTokenSource();
        var (model, _) = Build(_ =>
        {
            cts.Cancel();
            Thread.Sleep(50);
            return CapturingProviderHandler.Answer("unused");
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => model.CompleteAsync(ExternalProfile(), Conversation, cts.Token));
    }
}
