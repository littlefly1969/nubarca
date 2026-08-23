using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NubArca.Api.Help;
using Xunit;

namespace NubArca.Api.Tests.Help;

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

// A. The OpenAI-compatible provider contract, exercised against a fake provider.
//
// No test here reaches a real provider: an automated suite that depends on a
// third party is a suite that fails for reasons that have nothing to do with the
// code, and one that quietly costs money.
public sealed class ExternalHelpProviderContractTests
{
    private const string Key = "SUPER_SECRET_EXTERNAL_HELP_KEY_XYZ";

    private static (OpenAiCompatibleChatCompletionClient Client, CapturingProviderHandler Handler) Build(
        Func<HttpRequestMessage, HttpResponseMessage> respond,
        Action<ExternalHelpOptions>? tweak = null)
    {
        var options = new ExternalHelpOptions
        {
            Enabled = true,
            BaseUrl = "https://provider.example/",
            ApiKey = Key,
            Model = "test-model-1",
            ProviderLabel = "Test Provider",
            MaxOutputTokens = 321,
        };
        tweak?.Invoke(options);
        var handler = new CapturingProviderHandler(respond);
        var client = new OpenAiCompatibleChatCompletionClient(
            new HttpClient(handler),
            Options.Create(options),
            NullLogger<OpenAiCompatibleChatCompletionClient>.Instance);
        return (client, handler);
    }

    private static readonly IReadOnlyList<HelpChatMessage> Conversation = new[]
    {
        new HelpChatMessage(HelpChatRole.System, "system rules"),
        new HelpChatMessage(HelpChatRole.User, "earlier question"),
        new HelpChatMessage(HelpChatRole.Assistant, "earlier answer"),
        new HelpChatMessage(HelpChatRole.User, "how do albums work?"),
    };

    [Fact]
    public async Task Posts_The_Compatible_Shape_And_Extracts_The_Answer()
    {
        var (client, handler) = Build(_ => CapturingProviderHandler.Answer("Albums group photos."));

        var result = await client.CompleteAsync(Conversation);

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

    // D. The model is given NO capabilities — not empty ones, absent ones.
    [Fact]
    public async Task The_Request_Carries_No_Tool_Surface_At_All()
    {
        var (client, handler) = Build(_ => CapturingProviderHandler.Answer("ok"));
        await client.CompleteAsync(Conversation);

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

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, HelpFailureReasons.ProviderUnauthorized)]
    [InlineData(HttpStatusCode.Forbidden, HelpFailureReasons.ProviderUnauthorized)]
    [InlineData(HttpStatusCode.TooManyRequests, HelpFailureReasons.ProviderRateLimited)]
    [InlineData(HttpStatusCode.InternalServerError, HelpFailureReasons.ProviderUnavailable)]
    [InlineData(HttpStatusCode.BadGateway, HelpFailureReasons.ProviderUnavailable)]
    public async Task Provider_Failures_Become_Sanitized_Reasons(HttpStatusCode status, string reason)
    {
        // The provider's own error body is deliberately something that must never
        // reach a user: it quotes the request back, key and all.
        var leaky = JsonSerializer.Serialize(new { error = new { message = $"bad request with {Key}" } });
        var (client, _) = Build(_ => CapturingProviderHandler.Json(status, leaky));

        var result = await client.CompleteAsync(Conversation);

        Assert.False(result.Ok);
        Assert.Equal(reason, result.Reason);
        Assert.Null(result.Text);
        Assert.DoesNotContain(Key, result.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Malformed_Body_Is_Reported_As_Malformed_Not_Thrown()
    {
        var (client, _) = Build(_ => CapturingProviderHandler.Json(HttpStatusCode.OK, "{ this is not json"));
        var result = await client.CompleteAsync(Conversation);
        Assert.False(result.Ok);
        Assert.Equal(HelpFailureReasons.ProviderMalformed, result.Reason);
    }

    [Fact]
    public async Task An_Empty_Answer_Is_Not_Reported_As_Success()
    {
        // Well-formed, successful, and useless. Returning ok=true with nothing in
        // it would show the user an empty bubble and no explanation.
        var (client, _) = Build(_ => CapturingProviderHandler.Json(
            HttpStatusCode.OK, JsonSerializer.Serialize(new { choices = Array.Empty<object>() })));
        var result = await client.CompleteAsync(Conversation);
        Assert.False(result.Ok);
        Assert.Equal(HelpFailureReasons.ProviderEmpty, result.Reason);
    }

    [Fact]
    public async Task A_Slow_Provider_Times_Out_Rather_Than_Hanging()
    {
        var (client, _) = Build(
            _ =>
            {
                Thread.Sleep(TimeSpan.FromSeconds(2));
                return CapturingProviderHandler.Answer("too late");
            },
            o => o.TimeoutSeconds = 1);

        var result = await client.CompleteAsync(Conversation);
        Assert.False(result.Ok);
        Assert.Equal(HelpFailureReasons.ProviderTimeout, result.Reason);
    }

    [Fact]
    public async Task A_Cancelled_Caller_Is_Not_Mistaken_For_A_Timeout()
    {
        using var cts = new CancellationTokenSource();
        var (client, _) = Build(_ =>
        {
            cts.Cancel();
            Thread.Sleep(50);
            return CapturingProviderHandler.Answer("unused");
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.CompleteAsync(Conversation, cts.Token));
    }

    [Fact]
    public async Task An_Unusable_Configuration_Never_Reaches_The_Network()
    {
        // http:// with the insecure escape hatch closed: the key would otherwise
        // travel in a plaintext Authorization header.
        var (client, handler) = Build(
            _ => CapturingProviderHandler.Answer("unused"),
            o => o.BaseUrl = "http://provider.example");

        var result = await client.CompleteAsync(Conversation);

        Assert.False(result.Ok);
        Assert.Equal(HelpFailureReasons.NotConfigured, result.Reason);
        Assert.Equal(0, handler.Calls);
    }
}
