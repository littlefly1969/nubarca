using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NubArca.Api.Assistant;

// The one implemented protocol: the OpenAI-compatible Chat Completions wire
// format.
//
// The format is the interoperability target, not the vendor. A hosted provider,
// an operator's Ollama/vLLM/llama.cpp/LM Studio server and a future NubArca
// runtime can all speak it, which is why protocol and trust are separate axes —
// this class is indifferent to which side of the boundary it is talking to, and
// AssistantModelProfile has already decided that question.
//
// THE REQUEST DTO BELOW HAS NO TOOL FIELDS AND CANNOT GROW THEM BY ACCIDENT.
// There is no `tools`, no `functions`, no `tool_choice` — not empty ones, absent
// ones. A model that is never offered a capability cannot be talked into using
// it, which is a stronger guarantee than a system prompt asking it not to.
public sealed class OpenAiCompatibleTextModel : IAssistantTextModel
{
    private readonly HttpClient _http;
    private readonly ILogger<OpenAiCompatibleTextModel> _log;

    public OpenAiCompatibleTextModel(HttpClient http, ILogger<OpenAiCompatibleTextModel> log)
    {
        _http = http;
        _log = log;
    }

    public async Task<AssistantCompletion> CompleteAsync(
        AssistantModelProfile profile,
        IReadOnlyList<AssistantMessage> messages,
        CancellationToken cancellationToken = default)
    {
        if (profile.Protocol != AssistantModelProtocol.OpenAiCompatible)
        {
            return AssistantCompletion.Failure(AssistantFailureReasons.NotConfigured);
        }

        var payload = new ChatCompletionRequest
        {
            Model = profile.Model,
            MaxTokens = profile.EffectiveMaxOutputTokens,
            Messages = messages.Select(m => new ChatMessagePayload
            {
                Role = m.Role switch
                {
                    AssistantRole.System => "system",
                    AssistantRole.Assistant => "assistant",
                    _ => "user",
                },
                Content = m.Text,
            }).ToList(),
        };

        var url = $"{profile.BaseUrl.TrimEnd('/')}/v1/chat/completions";
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(payload, options: SerializerOptions),
        };
        // The ONLY place a key appears, and only when there is one: a trusted
        // local server usually wants no auth, and sending `Bearer ` with nothing
        // after it makes some of them reject the request outright.
        //
        // It is set on the REQUEST rather than on the shared HttpClient so it
        // cannot ride along on some other call made through the same client —
        // which matters more now that one client serves profiles on both sides
        // of the trust boundary.
        if (!string.IsNullOrWhiteSpace(profile.ApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", profile.ApiKey);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(profile.EffectiveTimeoutSeconds));

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
                LogOutcome(profile, elapsed, (int)response.StatusCode, ok: false);
                return AssistantCompletion.Failure(response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                        => AssistantFailureReasons.ProviderUnauthorized,
                    HttpStatusCode.TooManyRequests
                        => AssistantFailureReasons.ProviderRateLimited,
                    _ => AssistantFailureReasons.ProviderUnavailable,
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
                LogOutcome(profile, elapsed, (int)response.StatusCode, ok: false);
                return AssistantCompletion.Failure(AssistantFailureReasons.ProviderMalformed);
            }

            var text = parsed?.Choices?.FirstOrDefault()?.Message?.Content;
            if (string.IsNullOrWhiteSpace(text))
            {
                LogOutcome(profile, elapsed, (int)response.StatusCode, ok: false);
                return AssistantCompletion.Failure(AssistantFailureReasons.ProviderEmpty);
            }

            LogOutcome(profile, elapsed, (int)response.StatusCode, ok: true);
            return AssistantCompletion.Success(text.Trim());
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Our own deadline, not the caller's: a timeout rather than a
            // cancellation, and the two deserve different answers.
            return AssistantCompletion.Failure(AssistantFailureReasons.ProviderTimeout);
        }
        catch (HttpRequestException)
        {
            return AssistantCompletion.Failure(AssistantFailureReasons.ProviderUnavailable);
        }
    }

    // Safe metrics only: which profile, which trust classification, how long, and
    // the status class. No prompt, no answer, no header, no body — and no base
    // URL, which would put an operator's internal hostname in a log for no
    // operational gain.
    private void LogOutcome(AssistantModelProfile profile, TimeSpan elapsed, int status, bool ok)
        => _log.LogInformation(
            "assistant: profile={Profile} trust={Trust} status={StatusClass}xx ok={Ok} ms={Ms}",
            profile.Key, profile.Trust, status / 100, ok, (long)elapsed.TotalMilliseconds);

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
