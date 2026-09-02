using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Data;
using NubArca.Api.Domain.Print;
using NubArca.Api.Print;
using NubArca.Api.Tests.Endpoints;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.PixelFormats;

namespace NubArca.Api.Tests.Print;

public sealed class PrintFoundationTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory = new();
    public PrintFoundationTests() => _factory.EnsureDatabaseCreated();
    public void Dispose() => _factory.Dispose();

    [Fact]
    public void State_Machine_Rejects_Impossible_Transitions()
    {
        Assert.True(PrintJobStateMachine.CanTransition(PrintJobStates.Ready, PrintJobStates.Claimed));
        Assert.True(PrintJobStateMachine.CanTransition(PrintJobStates.Submitted, PrintJobStates.Completed));
        Assert.False(PrintJobStateMachine.CanTransition(PrintJobStates.Completed, PrintJobStates.Claimed));
        Assert.Throws<InvalidOperationException>(() =>
            PrintJobStateMachine.EnsureTransition(PrintJobStates.Completed, PrintJobStates.Claimed));
    }

    [Fact]
    public void Station_Status_Is_Server_Derived()
    {
        var now = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal("online", PrintStationStatus.Calculate(now.AddSeconds(-20), now, false, true,
            [PrintDeviceStates.Ready], 90, 300));
        Assert.Equal("degraded", PrintStationStatus.Calculate(now.AddSeconds(-120), now, false, true,
            [PrintDeviceStates.Ready], 90, 300));
        Assert.Equal("offline", PrintStationStatus.Calculate(now.AddSeconds(-400), now, false, true,
            [PrintDeviceStates.Ready], 90, 300));
        Assert.Equal("degraded", PrintStationStatus.Calculate(now, now, false, true,
            [PrintDeviceStates.Error], 90, 300));
    }

    [Fact]
    public void Capability_Matching_Is_Format_Specific_And_Fails_Closed()
    {
        Assert.True(PrintCapabilityMatcher.SupportsFormat("""{"formats":["10x15"]}""", "10x15"));
        Assert.False(PrintCapabilityMatcher.SupportsFormat("""{"formats":["A4"]}""", "10x15"));
        Assert.False(PrintCapabilityMatcher.SupportsFormat("not-json", "10x15"));
    }

    [Fact]
    public async Task Diagnostic_Render_Is_Real_10x15_Raster()
    {
        var bytes = await new PrintArtifactRenderer().RenderDiagnosticAsync(
            "Studio", "DNP DS620", DateTime.UnixEpoch, "10x15", "abc12345", default);
        using var image = Image.Load(bytes);
        Assert.Equal(1800, image.Width);
        Assert.Equal(1200, image.Height);
        Assert.True(bytes.Length > 10_000);
    }

    [Fact]
    public async Task Owner_Photo_Render_Preserves_Portrait_And_Landscape_10x15_Geometry()
    {
        var renderer = new PrintArtifactRenderer();
        foreach (var (width, height, expectedWidth, expectedHeight) in new[]
        {
            (400, 800, 1200, 1800),
            (800, 400, 1800, 1200),
        })
        {
            using var sourceImage = new Image<Rgb24>(width, height, new Rgb24(25, 80, 120));
            using var source = new MemoryStream();
            await sourceImage.SaveAsJpegAsync(source);
            var result = await renderer.RenderPhoto10x15Async(source.ToArray(), default);
            using var rendered = Image.Load(result);
            Assert.Equal(expectedWidth, rendered.Width);
            Assert.Equal(expectedHeight, rendered.Height);
        }
    }

    [Fact]
    public async Task Owner_Photo_Render_Applies_Exif_Orientation_Before_Fitting()
    {
        using var sourceImage = new Image<Rgb24>(800, 400, new Rgb24(25, 80, 120));
        sourceImage.Metadata.ExifProfile = new ExifProfile();
        sourceImage.Metadata.ExifProfile.SetValue(ExifTag.Orientation, (ushort)6);
        using var source = new MemoryStream();
        await sourceImage.SaveAsJpegAsync(source);

        var result = await new PrintArtifactRenderer().RenderPhoto10x15Async(source.ToArray(), default);
        using var rendered = Image.Load(result);

        Assert.Equal(1200, rendered.Width);
        Assert.Equal(1800, rendered.Height);
    }

    [Fact]
    public async Task Enrollment_Is_One_Shot_Hash_Only_And_Revocation_Blocks_Agent()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var created = await CreateStationAsync(owner, "Studio");
        var enrolled = await EnrollAsync(created.Id, created.Token);
        Assert.Equal(HttpStatusCode.OK, enrolled.StatusCode);
        var body = (await enrolled.Content.ReadFromJsonAsync<JsonElement>());
        var credential = body.GetProperty("stationCredential").GetString()!;

        Assert.Equal(HttpStatusCode.Unauthorized, (await EnrollAsync(created.Id, created.Token)).StatusCode);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var station = await db.PrintStations.SingleAsync(x => x.Id == created.Id);
            Assert.NotNull(station.CredentialHash);
            Assert.DoesNotContain(credential, station.CredentialHash!);
            Assert.NotEqual(created.Token, (await db.PrintStationEnrollments.SingleAsync()).TokenHash);
        }

        Assert.Equal(HttpStatusCode.NoContent,
            (await owner.DeleteAsync($"/api/print/stations/{created.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await HeartbeatAsync(credential)).StatusCode);
    }

    [Fact]
    public async Task Wrong_Station_Cannot_Consume_Enrollment()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var first = await CreateStationAsync(owner, "One");
        var second = await CreateStationAsync(owner, "Two");
        Assert.Equal(HttpStatusCode.Unauthorized, (await EnrollAsync(second.Id, first.Token)).StatusCode);
    }

    [Fact]
    public async Task Expired_Enrollment_Is_Rejected()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var station = await CreateStationAsync(owner, "Expired");
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var enrollment = await db.PrintStationEnrollments.SingleAsync(x => x.PrintStationId == station.Id);
            enrollment.ExpiresAt = DateTime.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await EnrollAsync(station.Id, station.Token)).StatusCode);
    }

    [Fact]
    public async Task Owner_Cannot_See_Or_Control_Another_Owners_Station()
    {
        var (_, firstOwner) = await _factory.CreateAuthenticatedClientAsync();
        var (_, secondOwner) = await _factory.CreateAuthenticatedClientAsync("second-owner@example.com");
        var station = await CreateStationAsync(firstOwner, "Private station");

        Assert.Empty((await secondOwner.GetFromJsonAsync<JsonElement[]>("/api/print/stations"))!);
        Assert.Equal(HttpStatusCode.NotFound,
            (await secondOwner.PutAsJsonAsync($"/api/print/stations/{station.Id}/desired-state",
                new { desiredState = "paused" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await secondOwner.DeleteAsync($"/api/print/stations/{station.Id}")).StatusCode);
    }

    [Fact]
    public async Task Paused_Station_Heartbeats_But_Claims_No_Job_Then_Resumes_End_To_End()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var created = await CreateStationAsync(owner, "Studio");
        var enrollment = await EnrollAsync(created.Id, created.Token);
        var credential = (await enrollment.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("stationCredential").GetString()!;
        (await HeartbeatAsync(credential)).EnsureSuccessStatusCode();

        var stations = (await owner.GetFromJsonAsync<JsonElement[]>("/api/print/stations"))!;
        var printerId = stations.Single().GetProperty("devices")[0].GetProperty("id").GetGuid();
        var test = await owner.PostAsJsonAsync($"/api/print/stations/{created.Id}/test-jobs",
            new { printerDeviceId = printerId });
        Assert.Equal(HttpStatusCode.Accepted, test.StatusCode);

        (await owner.PutAsJsonAsync($"/api/print/stations/{created.Id}/desired-state",
            new { desiredState = "paused" })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.OK, (await HeartbeatAsync(credential)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await ClaimAsync(credential)).StatusCode);

        (await owner.PutAsJsonAsync($"/api/print/stations/{created.Id}/desired-state",
            new { desiredState = "running" })).EnsureSuccessStatusCode();
        var claimResponse = await ClaimAsync(credential);
        claimResponse.EnsureSuccessStatusCode();
        var claim = await claimResponse.Content.ReadFromJsonAsync<JsonElement>();
        var jobId = claim.GetProperty("jobId").GetGuid();
        var token = claim.GetProperty("claimToken").GetString()!;
        var artifact = new HttpRequestMessage(HttpMethod.Get, $"/api/print-agent/jobs/{jobId}/artifact");
        artifact.Headers.Add("X-NubArca-Print-Credential", credential);
        artifact.Headers.Add("X-NubArca-Print-Claim", token);
        var artifactResponse = await _factory.CreateClient().SendAsync(artifact);
        artifactResponse.EnsureSuccessStatusCode();
        Assert.Equal("image/png", artifactResponse.Content.Headers.ContentType?.MediaType);

        var submitting = AgentRequest(HttpMethod.Post, $"/api/print-agent/jobs/{jobId}/submitting",
            credential, new { claimToken = token });
        (await _factory.CreateClient().SendAsync(submitting)).EnsureSuccessStatusCode();
        var result = AgentRequest(HttpMethod.Post, $"/api/print-agent/jobs/{jobId}/result",
            credential, new { claimToken = token, outcome = "completed", spoolReference = "test" });
        (await _factory.CreateClient().SendAsync(result)).EnsureSuccessStatusCode();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(PrintJobStates.Completed, (await db.PrintJobs.SingleAsync(x => x.Id == jobId)).State);
        Assert.Equal(HttpStatusCode.NoContent, (await ClaimAsync(credential)).StatusCode);
    }

    [Fact]
    public async Task Artifact_Is_Inaccessible_To_A_Different_Station()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var a = await EnrolledStationAsync(owner, "A");
        var b = await EnrolledStationAsync(owner, "B");
        (await HeartbeatAsync(a.Credential)).EnsureSuccessStatusCode();
        (await HeartbeatAsync(b.Credential)).EnsureSuccessStatusCode();
        var station = (await owner.GetFromJsonAsync<JsonElement[]>("/api/print/stations"))!
            .Single(x => x.GetProperty("id").GetGuid() == a.Id);
        var printerId = station.GetProperty("devices")[0].GetProperty("id").GetGuid();
        (await owner.PostAsJsonAsync($"/api/print/stations/{a.Id}/test-jobs",
            new { printerDeviceId = printerId })).EnsureSuccessStatusCode();
        var claimResponse = await ClaimAsync(a.Credential);
        var claim = await claimResponse.Content.ReadFromJsonAsync<JsonElement>();
        var request = new HttpRequestMessage(HttpMethod.Get,
            $"/api/print-agent/jobs/{claim.GetProperty("jobId").GetGuid()}/artifact");
        request.Headers.Add("X-NubArca-Print-Credential", b.Credential);
        request.Headers.Add("X-NubArca-Print-Claim", claim.GetProperty("claimToken").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await _factory.CreateClient().SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task Concurrent_Claims_Grant_One_Lease_And_Missing_Printer_Becomes_Offline()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var station = await EnrolledStationAsync(owner, "Atomic");
        (await HeartbeatAsync(station.Credential)).EnsureSuccessStatusCode();
        var listed = (await owner.GetFromJsonAsync<JsonElement[]>("/api/print/stations"))!.Single();
        var printerId = listed.GetProperty("devices")[0].GetProperty("id").GetGuid();
        (await owner.PostAsJsonAsync($"/api/print/stations/{station.Id}/test-jobs",
            new { printerDeviceId = printerId })).EnsureSuccessStatusCode();

        var claims = await Task.WhenAll(ClaimAsync(station.Credential), ClaimAsync(station.Credential));
        Assert.Single(claims, x => x.StatusCode == HttpStatusCode.OK);
        Assert.Single(claims, x => x.StatusCode == HttpStatusCode.NoContent);

        var emptyHeartbeat = AgentRequest(HttpMethod.Post, "/api/print-agent/heartbeat",
            station.Credential, new { agentVersion = "test", devices = Array.Empty<object>() });
        (await _factory.CreateClient().SendAsync(emptyHeartbeat)).EnsureSuccessStatusCode();
        listed = (await owner.GetFromJsonAsync<JsonElement[]>("/api/print/stations"))!.Single();
        Assert.Equal("offline", listed.GetProperty("devices")[0].GetProperty("observedState").GetString());
    }

    [Fact]
    public async Task Queued_Job_Can_Be_Cancelled_And_Is_Never_Claimed()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var station = await EnrolledStationAsync(owner, "Cancel");
        (await HeartbeatAsync(station.Credential)).EnsureSuccessStatusCode();
        var listed = (await owner.GetFromJsonAsync<JsonElement[]>("/api/print/stations"))!.Single();
        var printerId = listed.GetProperty("devices")[0].GetProperty("id").GetGuid();
        var created = await owner.PostAsJsonAsync($"/api/print/stations/{station.Id}/test-jobs",
            new { printerDeviceId = printerId });
        var jobId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.NoContent,
            (await owner.PostAsync($"/api/print/jobs/{jobId}/cancel", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await ClaimAsync(station.Credential)).StatusCode);
    }

    [Fact]
    public async Task Ready_Job_Is_Not_Claimed_While_Its_Printer_Is_Offline()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var station = await EnrolledStationAsync(owner, "Offline printer");
        (await HeartbeatAsync(station.Credential)).EnsureSuccessStatusCode();
        var listed = (await owner.GetFromJsonAsync<JsonElement[]>("/api/print/stations"))!.Single();
        var printerId = listed.GetProperty("devices")[0].GetProperty("id").GetGuid();
        (await owner.PostAsJsonAsync($"/api/print/stations/{station.Id}/test-jobs",
            new { printerDeviceId = printerId })).EnsureSuccessStatusCode();

        var emptyHeartbeat = AgentRequest(HttpMethod.Post, "/api/print-agent/heartbeat",
            station.Credential, new { agentVersion = "test", devices = Array.Empty<object>() });
        (await _factory.CreateClient().SendAsync(emptyHeartbeat)).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.NoContent, (await ClaimAsync(station.Credential)).StatusCode);
    }

    [Fact]
    public async Task Definite_Failure_Is_Explicitly_Retryable_But_Ambiguous_Delivery_Is_Not()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var station = await EnrolledStationAsync(owner, "Retry");
        (await HeartbeatAsync(station.Credential)).EnsureSuccessStatusCode();
        var listed = (await owner.GetFromJsonAsync<JsonElement[]>("/api/print/stations"))!.Single();
        var printerId = listed.GetProperty("devices")[0].GetProperty("id").GetGuid();

        async Task<(Guid JobId, string Claim)> CreateAndClaim()
        {
            (await owner.PostAsJsonAsync($"/api/print/stations/{station.Id}/test-jobs",
                new { printerDeviceId = printerId })).EnsureSuccessStatusCode();
            var response = await ClaimAsync(station.Credential);
            var claim = await response.Content.ReadFromJsonAsync<JsonElement>();
            return (claim.GetProperty("jobId").GetGuid(), claim.GetProperty("claimToken").GetString()!);
        }

        var failed = await CreateAndClaim();
        (await _factory.CreateClient().SendAsync(AgentRequest(HttpMethod.Post,
            $"/api/print-agent/jobs/{failed.JobId}/submitting", station.Credential,
            new { claimToken = failed.Claim }))).EnsureSuccessStatusCode();
        (await _factory.CreateClient().SendAsync(AgentRequest(HttpMethod.Post,
            $"/api/print-agent/jobs/{failed.JobId}/result", station.Credential,
            new { claimToken = failed.Claim, outcome = "failed", failureCode = "paper_out" })))
            .EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NoContent,
            (await owner.PostAsync($"/api/print/jobs/{failed.JobId}/retry", null)).StatusCode);
        var retryClaim = await ClaimAsync(station.Credential);
        retryClaim.EnsureSuccessStatusCode();
        var retry = await retryClaim.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(failed.JobId,
            retry.GetProperty("jobId").GetGuid());

        // Complete the explicit retry before creating the ambiguous job.
        var retryToken = retry.GetProperty("claimToken").GetString()!;
        (await _factory.CreateClient().SendAsync(AgentRequest(HttpMethod.Post,
            $"/api/print-agent/jobs/{failed.JobId}/submitting", station.Credential,
            new { claimToken = retryToken }))).EnsureSuccessStatusCode();
        (await _factory.CreateClient().SendAsync(AgentRequest(HttpMethod.Post,
            $"/api/print-agent/jobs/{failed.JobId}/result", station.Credential,
            new { claimToken = retryToken, outcome = "completed" }))).EnsureSuccessStatusCode();

        var unknown = await CreateAndClaim();
        (await _factory.CreateClient().SendAsync(AgentRequest(HttpMethod.Post,
            $"/api/print-agent/jobs/{unknown.JobId}/submitting", station.Credential,
            new { claimToken = unknown.Claim }))).EnsureSuccessStatusCode();
        (await _factory.CreateClient().SendAsync(AgentRequest(HttpMethod.Post,
            $"/api/print-agent/jobs/{unknown.JobId}/result", station.Credential,
            new { claimToken = unknown.Claim, outcome = "delivery-unknown" })))
            .EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict,
            (await owner.PostAsync($"/api/print/jobs/{unknown.JobId}/retry", null)).StatusCode);
    }

    private async Task<(Guid Id, string Token)> CreateStationAsync(HttpClient owner, string name)
    {
        var response = await owner.PostAsJsonAsync("/api/print/stations", new { name });
        response.EnsureSuccessStatusCode();
        var value = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (value.GetProperty("id").GetGuid(), value.GetProperty("enrollmentToken").GetString()!);
    }
    private async Task<(Guid Id, string Credential)> EnrolledStationAsync(HttpClient owner, string name)
    {
        var created = await CreateStationAsync(owner, name);
        var response = await EnrollAsync(created.Id, created.Token);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (created.Id, body.GetProperty("stationCredential").GetString()!);
    }
    private Task<HttpResponseMessage> EnrollAsync(Guid station, string token) =>
        _factory.CreateClient().PostAsJsonAsync("/api/print-agent/enroll",
            new { stationId = station, enrollmentToken = token, agentVersion = "test" });
    private Task<HttpResponseMessage> HeartbeatAsync(string credential)
    {
        var request = AgentRequest(HttpMethod.Post, "/api/print-agent/heartbeat", credential,
            new { agentVersion = "test", devices = new[] { new { deviceKey = "fake-10x15",
                displayName = "Fake", manufacturer = "NubArca", model = "CI", adapterKind = "fake",
                capabilities = new { formats = new[] { "10x15" }, color = true }, observedState = "ready" } } });
        return _factory.CreateClient().SendAsync(request);
    }
    private Task<HttpResponseMessage> ClaimAsync(string credential) =>
        _factory.CreateClient().SendAsync(AgentRequest(HttpMethod.Post, "/api/print-agent/jobs/claim",
            credential, new { adapterKind = "fake" }));
    private static HttpRequestMessage AgentRequest(HttpMethod method, string url, string credential, object json)
    {
        var request = new HttpRequestMessage(method, url) { Content = JsonContent.Create(json) };
        request.Headers.Add("X-NubArca-Print-Credential", credential);
        return request;
    }
}
