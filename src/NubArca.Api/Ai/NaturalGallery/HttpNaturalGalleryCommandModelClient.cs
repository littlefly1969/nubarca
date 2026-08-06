using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using NubArca.Api.Ai;

namespace NubArca.Api.Ai.NaturalGallery;

// HTTP transport to the internal-only decoder-LLM sidecar. Talks ONLY to the
// configured internal base URL (e.g. http://nubarca-nlu:8090) over the Docker
// internal network — never a public host, never outbound internet. No user
// command text is logged here. When no base URL is configured the client reports
// not-ready so the "onnx" interpreter is treated as unavailable.
public sealed class HttpNaturalGalleryCommandModelClient : INaturalGalleryCommandModelClient
{
    private readonly HttpClient _http;
    private readonly AiNaturalGallerySearchOptions _options;

    public HttpNaturalGalleryCommandModelClient(HttpClient http, IOptions<AiOptions> options)
    {
        _http = http;
        _options = options.Value.NaturalGallerySearch;
    }

    public string ModelKey => "sidecar";

    private string? BaseUrl => string.IsNullOrWhiteSpace(_options.ModelServiceBaseUrl)
        ? null : _options.ModelServiceBaseUrl!.TrimEnd('/');

    public async Task<bool> IsReadyAsync(CancellationToken cancellationToken = default)
    {
        var baseUrl = BaseUrl;
        if (baseUrl is null) return false;
        try
        {
            using var resp = await _http.GetAsync($"{baseUrl}/health", cancellationToken);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task<string> CompleteJsonAsync(
        string systemPrompt, string userPrompt, int maxOutputTokens, CancellationToken cancellationToken = default)
    {
        var baseUrl = BaseUrl
            ?? throw new InterpreterUnavailableException("No NLU sidecar base URL configured.");

        HttpResponseMessage resp;
        try
        {
            resp = await _http.PostAsJsonAsync(
                $"{baseUrl}/interpret",
                new SidecarRequest(systemPrompt, userPrompt, maxOutputTokens),
                cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InterpreterTimeoutException();
        }
        catch (HttpRequestException ex)
        {
            throw new InterpreterUnavailableException(ex.Message);
        }

        switch (resp.StatusCode)
        {
            case HttpStatusCode.OK:
                var body = await resp.Content.ReadFromJsonAsync<SidecarResponse>(cancellationToken);
                return body?.Json ?? body?.Text ?? "";
            case HttpStatusCode.TooManyRequests:
            case HttpStatusCode.ServiceUnavailable when resp.Headers.RetryAfter is not null:
                throw new InterpreterBusyException();
            case HttpStatusCode.ServiceUnavailable:
            case HttpStatusCode.NotFound:
                throw new InterpreterUnavailableException();
            case HttpStatusCode.RequestTimeout:
            case HttpStatusCode.GatewayTimeout:
                throw new InterpreterTimeoutException();
            default:
                throw new InterpreterUnavailableException($"sidecar status {(int)resp.StatusCode}");
        }
    }

    private sealed record SidecarRequest(
        [property: JsonPropertyName("system")] string System,
        [property: JsonPropertyName("user")] string User,
        [property: JsonPropertyName("maxTokens")] int MaxTokens);

    private sealed record SidecarResponse
    {
        [JsonPropertyName("json")] public string? Json { get; init; }
        [JsonPropertyName("text")] public string? Text { get; init; }
    }
}
