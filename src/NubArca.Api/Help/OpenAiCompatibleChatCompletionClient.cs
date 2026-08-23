using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace NubArca.Api.Help;

// The first (and so far only) provider adapter: the OpenAI-compatible Chat
// Completions wire format.
//
// The format is the interoperability target, not the vendor. Several providers
// speak it, so an installation can point BaseUrl at whichever one it has a
// contract with, and NubArca carries no SDK, no vendor types and no vendor
// concepts beyond this file.
//
// THE REQUEST DTO BELOW HAS NO TOOL FIELDS AND CANNOT GROW THEM BY ACCIDENT.
// There is no `tools`, no `functions`, no `tool_choice` — not empty ones,
// absent ones. A model that is never offered a capability cannot be talked into
// using it, which is a stronger guarantee than a system prompt asking it not to.
public sealed class OpenAiCompatibleChatCompletionClient : IExternalHelpChatClient
{
    private readonly HttpClient _http;
    private readonly IOptions<ExternalHelpOptions> _options;
    private readonly ILogger<OpenAiCompatibleChatCompletionClient> _log;

    public OpenAiCompatibleChatCompletionClient(
        HttpClient http,
        IOptions<ExternalHelpOptions> options,
        ILogger<OpenAiCompatibleChatCompletionClient> log)
    {
        _http = http;
        _options = options;
        _log = log;
    }

    public async Task<HelpChatResult> CompleteAsync(
        IReadOnlyList<HelpChatMessage> messages,
        CancellationToken cancellationToken = default)
    {
        var options = _options.Value;
        if (!options.IsUsable)
        {
            return HelpChatResult.Failure(HelpFailureReasons.NotConfigured);
        }

        var payload = new ChatCompletionRequest
        {
            Model = options.Model,
            MaxTokens = options.EffectiveMaxOutputTokens,
            Messages = messages.Select(m => new ChatMessagePayload
            {
                Role = m.Role switch
                {
                    HelpChatRole.System => "system",
                    HelpChatRole.Assistant => "assistant",
                    _ => "user",
                },
                Content = m.Text,
            }).ToList(),
        };

        var url = $"{options.BaseUrl.TrimEnd('/')}/v1/chat/completions";
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(payload, options: SerializerOptions),
        };
        // The ONLY place the key appears. It is set on the request rather than on
        // the shared HttpClient so it cannot ride along on some future call made
        // through the same client.
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.EffectiveTimeoutSeconds));

        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            using var response = await _http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);

            var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started);
            if (!response.IsSuccessStatusCode)
            {
                // Status CLASS only. A provider error body can quote the request
                // back — including the key in some implementations — so it is
                // never read, never logged and never returned.
                LogOutcome(options, elapsed, (int)response.StatusCode, ok: false);
                return HelpChatResult.Failure(response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                        => HelpFailureReasons.ProviderUnauthorized,
                    HttpStatusCode.TooManyRequests
                        => HelpFailureReasons.ProviderRateLimited,
                    _ => HelpFailureReasons.ProviderUnavailable,
                });
            }

            ChatCompletionResponse? parsed;
            try
            {
                parsed = await response.Content.ReadFromJsonAsync<ChatCompletionResponse>(
                    SerializerOptions, timeout.Token);
            }
            catch (JsonException)
            {
                LogOutcome(options, elapsed, (int)response.StatusCode, ok: false);
                return HelpChatResult.Failure(HelpFailureReasons.ProviderMalformed);
            }

            var text = parsed?.Choices?.FirstOrDefault()?.Message?.Content;
            if (string.IsNullOrWhiteSpace(text))
            {
                LogOutcome(options, elapsed, (int)response.StatusCode, ok: false);
                return HelpChatResult.Failure(HelpFailureReasons.ProviderEmpty);
            }

            LogOutcome(options, elapsed, (int)response.StatusCode, ok: true);
            return HelpChatResult.Success(text.Trim());
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Our own deadline, not the caller's: a timeout rather than a
            // cancellation, and the two deserve different answers.
            return HelpChatResult.Failure(HelpFailureReasons.ProviderTimeout);
        }
        catch (HttpRequestException)
        {
            return HelpChatResult.Failure(HelpFailureReasons.ProviderUnavailable);
        }
    }

    // Safe metrics only: which provider, which model, how long, and the status
    // class. No prompt, no answer, no header, no body.
    private void LogOutcome(ExternalHelpOptions options, TimeSpan elapsed, int status, bool ok)
        => _log.LogInformation(
            "external help: provider={Provider} model={Model} status={StatusClass}xx ok={Ok} ms={Ms}",
            options.ProviderLabel, options.Model, status / 100, ok, (long)elapsed.TotalMilliseconds);

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };

    // ---- NubArca-owned wire DTOs ------------------------------------------
    //
    // Deliberately the minimum subset of the protocol. Anything absent here is
    // absent from the outbound body, which is what makes "the model has no
    // tools" a property of the code rather than a promise in a comment.

    private sealed class ChatCompletionRequest
    {
        [JsonPropertyName("model")] public string Model { get; set; } = string.Empty;
        [JsonPropertyName("messages")] public List<ChatMessagePayload> Messages { get; set; } = new();
        [JsonPropertyName("max_tokens")] public int MaxTokens { get; set; }
    }

    private sealed class ChatMessagePayload
    {
        [JsonPropertyName("role")] public string Role { get; set; } = string.Empty;
        [JsonPropertyName("content")] public string Content { get; set; } = string.Empty;
    }

    private sealed class ChatCompletionResponse
    {
        [JsonPropertyName("choices")] public List<Choice>? Choices { get; set; }
    }

    private sealed class Choice
    {
        [JsonPropertyName("message")] public ChatMessagePayload? Message { get; set; }
    }
}
