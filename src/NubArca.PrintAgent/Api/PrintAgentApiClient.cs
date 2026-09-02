using System.Net;
using System.Net.Http.Json;
using NubArca.PrintAgent.Adapters;

namespace NubArca.PrintAgent.Api;

public sealed record AgentEnrollmentResponse(Guid StationId, string StationCredential, string DesiredState);
public sealed record AgentHeartbeatResponse(string DesiredState, DateTime ServerTime);
public sealed record AgentDeviceReport(string DeviceKey, string DisplayName, string? Manufacturer,
    string? Model, string AdapterKind, PrinterCapabilities Capabilities, string ObservedState);
public sealed record AgentClaim(Guid JobId, string ClaimToken, string Kind, string Format,
    string ArtifactUrl, long ArtifactByteLength, string ContentType, string DeviceKey);

public sealed class PrintAgentApiClient
{
    public const string CredentialHeader = "X-NubArca-Print-Credential";
    public const string ClaimHeader = "X-NubArca-Print-Claim";
    private readonly HttpClient _http;
    private string? _credential;

    public PrintAgentApiClient(HttpClient http) => _http = http;
    public void SetCredential(string credential) => _credential = credential;

    public async Task<AgentEnrollmentResponse> EnrollAsync(Guid stationId, string token,
        string agentVersion, CancellationToken cancellationToken)
    {
        using var response = await _http.PostAsJsonAsync("api/print-agent/enroll",
            new { stationId, enrollmentToken = token, agentVersion }, cancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AgentEnrollmentResponse>(cancellationToken))!;
    }

    public async Task<AgentHeartbeatResponse> HeartbeatAsync(string agentVersion,
        IReadOnlyList<AgentDeviceReport> devices, CancellationToken cancellationToken)
    {
        using var request = Json(HttpMethod.Post, "api/print-agent/heartbeat", new { agentVersion, devices });
        using var response = await _http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AgentHeartbeatResponse>(cancellationToken))!;
    }

    public async Task<AgentClaim?> ClaimAsync(string adapterKind, CancellationToken cancellationToken)
    {
        using var request = Json(HttpMethod.Post, "api/print-agent/jobs/claim", new { adapterKind });
        using var response = await _http.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NoContent) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<AgentClaim>(cancellationToken);
    }

    public async Task DownloadAsync(AgentClaim claim, string path, long maxBytes,
        CancellationToken cancellationToken)
    {
        if (claim.ArtifactByteLength is <= 0 || claim.ArtifactByteLength > maxBytes)
            throw new InvalidDataException("Artifact exceeds the configured bound.");
        using var request = Auth(HttpMethod.Get, claim.ArtifactUrl);
        request.Headers.TryAddWithoutValidation(ClaimHeader, claim.ClaimToken);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long length && length > maxBytes)
            throw new InvalidDataException("Artifact response exceeds the configured bound.");
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write,
            FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.WriteThrough);
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            total += read;
            if (total > maxBytes) throw new InvalidDataException("Artifact stream exceeds the configured bound.");
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        await target.FlushAsync(cancellationToken);
    }

    public async Task MarkSubmittingAsync(AgentClaim claim, CancellationToken cancellationToken)
    {
        using var request = Json(HttpMethod.Post, $"api/print-agent/jobs/{claim.JobId:D}/submitting",
            new { claimToken = claim.ClaimToken });
        using var response = await _http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public Task MarkSubmittingAsync(Guid jobId, string claimToken, CancellationToken cancellationToken) =>
        MarkSubmittingAsync(new AgentClaim(jobId, claimToken, "", "", "", 0, "", ""), cancellationToken);

    public async Task ReportAsync(Guid jobId, string claimToken, string outcome,
        string? failureCode, string? spoolReference, CancellationToken cancellationToken)
    {
        using var request = Json(HttpMethod.Post, $"api/print-agent/jobs/{jobId:D}/result",
            new { claimToken, outcome, failureCode, spoolReference });
        using var response = await _http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private HttpRequestMessage Json(HttpMethod method, string url, object body)
    {
        var request = Auth(method, url);
        request.Content = JsonContent.Create(body);
        return request;
    }
    private HttpRequestMessage Auth(HttpMethod method, string url)
    {
        if (string.IsNullOrWhiteSpace(_credential)) throw new InvalidOperationException("Agent is not enrolled.");
        var request = new HttpRequestMessage(method, url);
        request.Headers.TryAddWithoutValidation(CredentialHeader, _credential);
        return request;
    }
}
