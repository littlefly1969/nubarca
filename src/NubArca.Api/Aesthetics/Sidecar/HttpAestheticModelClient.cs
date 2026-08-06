using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace NubArca.Api.Aesthetics.Sidecar;

// HTTP transport to the internal-only HumanAesExpert sidecar. Talks ONLY to the
// configured internal base URL (e.g. http://human-aesexpert:8091) over the
// Docker internal network — never a public host, never outbound internet. The
// image is streamed as a bounded multipart part; the request envelope rides as
// multipart form fields. NO image bytes, filenames, metrics, or model text are
// ever logged here. Errors surface as AestheticSidecarException with a stable,
// sanitized code.
public sealed class HttpAestheticModelClient : IAestheticModelClient
{
    private readonly HttpClient _http;
    private readonly AestheticsOptions _options;

    public HttpAestheticModelClient(HttpClient http, IOptions<AestheticsOptions> options)
    {
        _http = http;
        _options = options.Value;
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_options.SidecarBaseUrl);

    // Regression-test seam: the typed-client registration must disable
    // HttpClient's implicit 100-second timeout so RequestTimeoutSeconds remains
    // the sole deadline. No transport or endpoint details are exposed publicly.
    internal TimeSpan TransportTimeout => _http.Timeout;

    private string? BaseUrl => string.IsNullOrWhiteSpace(_options.SidecarBaseUrl)
        ? null : _options.SidecarBaseUrl.TrimEnd('/');

    public async Task<AestheticSidecarResponse> AnalyzeAsync(
        AestheticSidecarRequest request,
        byte[] imageBytes,
        string imageContentType,
        CancellationToken cancellationToken)
    {
        var baseUrl = BaseUrl
            ?? throw new AestheticSidecarException(
                AestheticErrorCodes.ModelUnavailable, "The aesthetics model is not configured.");

        using var content = new MultipartFormDataContent();
        // Envelope fields (kept as discrete parts so the sidecar reads them
        // without buffering the image as JSON).
        content.Add(new StringContent(request.ContractVersion.ToString()), "contractVersion");
        content.Add(new StringContent(request.ProfileKey), "profileKey");
        content.Add(new StringContent(string.Join(',', request.Capabilities)), "capabilities");
        content.Add(new StringContent(request.Language), "language");
        content.Add(new StringContent(request.PreprocessingProfileKey), "preprocessingProfileKey");

        var imagePart = new ByteArrayContent(imageBytes);
        imagePart.Headers.ContentType =
            MediaTypeHeaderValue.Parse(string.IsNullOrWhiteSpace(imageContentType) ? "application/octet-stream" : imageContentType);
        // Deliberately a neutral, non-identifying part filename (never the real
        // owner filename).
        content.Add(imagePart, "image", "image");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _options.RequestTimeoutSeconds)));

        HttpResponseMessage resp;
        try
        {
            resp = await _http.PostAsync($"{baseUrl}/analyze", content, timeoutCts.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Cooperative cancellation (worker shutdown / operator cancel) — let
            // the analysis service treat this as a cancel, not a failure.
            throw;
        }
        catch (OperationCanceledException)
        {
            throw new AestheticSidecarException(AestheticErrorCodes.Timeout, "The aesthetics model timed out.");
        }
        catch (HttpRequestException ex)
        {
            throw new AestheticSidecarException(AestheticErrorCodes.ModelUnavailable, "The aesthetics model is unavailable.", ex);
        }

        switch (resp.StatusCode)
        {
            case HttpStatusCode.OK:
                break;
            case HttpStatusCode.TooManyRequests:
            case HttpStatusCode.ServiceUnavailable:
            case HttpStatusCode.NotFound:
                throw new AestheticSidecarException(AestheticErrorCodes.ModelUnavailable, "The aesthetics model is unavailable.");
            case HttpStatusCode.RequestTimeout:
            case HttpStatusCode.GatewayTimeout:
                throw new AestheticSidecarException(AestheticErrorCodes.Timeout, "The aesthetics model timed out.");
            default:
                throw new AestheticSidecarException(AestheticErrorCodes.InvalidModelOutput, "The aesthetics model returned an error.");
        }

        // Bound the response body BEFORE parsing (defense in depth).
        var raw = await resp.Content.ReadAsByteArrayAsync(timeoutCts.Token);
        if (raw.Length > AestheticSidecarContract.MaxRawResponseBytes)
        {
            throw new AestheticSidecarException(AestheticErrorCodes.InvalidModelOutput, "The aesthetics model response was too large.");
        }

        WireResponse? wire;
        try
        {
            wire = JsonSerializer.Deserialize<WireResponse>(raw);
        }
        catch (JsonException ex)
        {
            throw new AestheticSidecarException(AestheticErrorCodes.InvalidModelOutput, "The aesthetics model response was malformed.", ex);
        }
        if (wire is null)
        {
            throw new AestheticSidecarException(AestheticErrorCodes.InvalidModelOutput, "The aesthetics model response was empty.");
        }

        return wire.ToResponse();
    }

    // Wire DTOs (camelCase JSON) mapped to the internal contract records.
    private sealed record WireResponse
    {
        [JsonPropertyName("contractVersion")] public int ContractVersion { get; init; }
        [JsonPropertyName("profileKey")] public string ProfileKey { get; init; } = string.Empty;
        [JsonPropertyName("modelName")] public string? ModelName { get; init; }
        [JsonPropertyName("modelRevision")] public string? ModelRevision { get; init; }
        [JsonPropertyName("runtimeName")] public string? RuntimeName { get; init; }
        [JsonPropertyName("runtimeVersion")] public string? RuntimeVersion { get; init; }
        [JsonPropertyName("preprocessingProfileKey")] public string? PreprocessingProfileKey { get; init; }
        [JsonPropertyName("completedCapabilities")] public List<string>? CompletedCapabilities { get; init; }
        [JsonPropertyName("metrics")] public List<WireMetric>? Metrics { get; init; }
        [JsonPropertyName("texts")] public List<WireText>? Texts { get; init; }
        [JsonPropertyName("warnings")] public List<string>? Warnings { get; init; }
        [JsonPropertyName("durationMs")] public long DurationMs { get; init; }

        public AestheticSidecarResponse ToResponse() => new(
            ContractVersion,
            ProfileKey,
            ModelName,
            ModelRevision,
            RuntimeName,
            RuntimeVersion,
            PreprocessingProfileKey,
            CompletedCapabilities ?? new List<string>(),
            (Metrics ?? new List<WireMetric>()).Select(m => m.ToMetric()).ToList(),
            (Texts ?? new List<WireText>()).Select(t => t.ToText()).ToList(),
            Warnings ?? new List<string>(),
            DurationMs);
    }

    private sealed record WireMetric
    {
        [JsonPropertyName("key")] public string Key { get; init; } = string.Empty;
        [JsonPropertyName("value")] public double Value { get; init; }
        [JsonPropertyName("scaleMin")] public double ScaleMin { get; init; }
        [JsonPropertyName("scaleMax")] public double ScaleMax { get; init; }
        [JsonPropertyName("confidence")] public double? Confidence { get; init; }
        [JsonPropertyName("version")] public int Version { get; init; }

        public AestheticSidecarMetric ToMetric() => new(Key, Value, ScaleMin, ScaleMax, Confidence, Version);
    }

    private sealed record WireText
    {
        [JsonPropertyName("kind")] public string Kind { get; init; } = string.Empty;
        [JsonPropertyName("language")] public string Language { get; init; } = string.Empty;
        [JsonPropertyName("text")] public string Text { get; init; } = string.Empty;
        [JsonPropertyName("promptTemplateVersion")] public int? PromptTemplateVersion { get; init; }

        public AestheticSidecarText ToText() => new(Kind, Language, Text, PromptTemplateVersion);
    }
}
