using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Ai;
using NubArca.Api.Data;
using NubArca.Api.Domain.Ai;
using NubArca.Api.Tests.Endpoints;

namespace NubArca.Api.Tests.Admin;

// Phase 0C: admin-only AI status + aggregate diagnostics endpoints. Aggregate/
// operational data only — no raw vectors, internal ids, SHA, storage keys,
// paths, payloads, stack traces, or owner-private AI data.
public sealed class AiAdminEndpointsTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory;

    public AiAdminEndpointsTests()
    {
        _factory = new SqliteWebApplicationFactory();
        _factory.EnsureDatabaseCreated();
    }

    public void Dispose() => _factory.Dispose();

    private async Task<HttpClient> AdminClientAsync(string email = "admin@example.com")
    {
        var userId = await _factory.SeedUserAsync(email);
        await _factory.PromoteToAdminAsync(userId);
        return await _factory.LoginAsync(email);
    }

    private static readonly string[] ForbiddenNeedles =
    {
        "StorageKey", "storageKey", "storage_key",
        "BlobObjectId", "blobObjectId",
        "Sha256", "sha256",
        "/storage/objects/", "PayloadJson", "TokenHash",
        "EmbeddingBytes", "embeddingBytes", "Vector", "vector",
        "stackTrace", "Exception",
    };

    [Theory]
    [InlineData("/api/admin/ai/status")]
    [InlineData("/api/admin/ai/diagnostics")]
    [InlineData("/api/admin/ai/face-settings")]
    public async Task Requires_Authentication(string path)
    {
        var anonymous = _factory.CreateClient();
        var response = await anonymous.GetAsync(path);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/admin/ai/status")]
    [InlineData("/api/admin/ai/diagnostics")]
    [InlineData("/api/admin/ai/face-settings")]
    public async Task Non_Admin_Is_Forbidden(string path)
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Face_Settings_Returns_Thresholds_And_Flags_No_Internal_Fields()
    {
        var client = await AdminClientAsync();

        var response = await client.GetAsync("/api/admin/ai/face-settings");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var raw = await response.Content.ReadAsStringAsync();
        // Enabled flags + provisional thresholds are surfaced; face processing is
        // OFF by default.
        Assert.Contains("faceDetectionEnabled", raw, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("clusterSimilarityThreshold", raw, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("searchDefaultSimilarityThreshold", raw, StringComparison.OrdinalIgnoreCase);
        // The clustering-mode name "pgvector_knn" legitimately contains "vector";
        // strip that known-safe token before the raw-vector-leak sweep.
        var sweep = raw.Replace("pgvector_knn", "mode");
        foreach (var needle in ForbiddenNeedles)
        {
            Assert.DoesNotContain(needle, sweep, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Face_Settings_Update_Persists_Cluster_Threshold_And_Louvain_Resolution()
    {
        var client = await AdminClientAsync();

        var resp = await client.PutAsJsonAsync("/api/admin/ai/face-settings", new
        {
            clusterSimilarityThreshold = 0.45,
            knnLouvainResolution = 1.5,
        });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var diag = await resp.Content.ReadFromJsonAsync<FaceDiagnosticsDto>();
        Assert.NotNull(diag);
        Assert.Equal(0.45, diag!.Thresholds.ClusterSimilarityThreshold, 3);
        Assert.Equal(1.5, diag.Thresholds.KnnLouvainResolution, 3);
        // The read-only clustering view reflects the effective (edited) edge threshold.
        Assert.Equal(0.45, diag.Clustering.KnnMinSimilarity, 3);

        // Re-read is persisted.
        var again = await (await client.GetAsync("/api/admin/ai/face-settings"))
            .Content.ReadFromJsonAsync<FaceDiagnosticsDto>();
        Assert.Equal(1.5, again!.Thresholds.KnnLouvainResolution, 3);
    }

    [Fact]
    public async Task Face_Settings_Update_Rejects_Out_Of_Range_Resolution()
    {
        var client = await AdminClientAsync();
        var resp = await client.PutAsJsonAsync("/api/admin/ai/face-settings", new { knnLouvainResolution = 9.0 });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    private sealed record FaceThresholdsDto(double ClusterSimilarityThreshold, double KnnLouvainResolution);
    private sealed record FaceClusteringDto(double KnnMinSimilarity, int KnnMaxClusterSize);
    private sealed record FaceDiagnosticsDto(FaceThresholdsDto Thresholds, FaceClusteringDto Clustering);

    [Fact]
    public async Task Status_Returns_Disabled_By_Default_And_No_Internal_Fields()
    {
        var client = await AdminClientAsync();

        var response = await client.GetAsync("/api/admin/ai/status");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<AiStatus>();
        Assert.NotNull(body);
        Assert.False(body!.Enabled);
        Assert.Equal("none", body.DefaultProvider);
        Assert.NotEmpty(body.Capabilities);

        var raw = await response.Content.ReadAsStringAsync();
        foreach (var needle in ForbiddenNeedles)
        {
            Assert.DoesNotContain(needle, raw, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Diagnostics_Returns_Aggregate_Only_Groups()
    {
        var client = await AdminClientAsync();

        // Seed two provider diagnostics for the same capability/code.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            for (var i = 0; i < 2; i++)
            {
                db.AiIndexDiagnostics.Add(new AiIndexDiagnostic
                {
                    Id = Guid.NewGuid(),
                    Capability = AiCapabilities.ImageEmbedding,
                    TargetKind = AiDiagnosticTargetKinds.Provider,
                    ErrorCode = "no-default-profile",
                    IsPermanent = false,
                    AttemptCount = 0,
                    OccurredAt = DateTime.UtcNow,
                });
            }
            await db.SaveChangesAsync();
        }

        var response = await client.GetAsync("/api/admin/ai/diagnostics");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<AiDiagnosticsStats>();
        Assert.NotNull(body);
        Assert.Equal(2, body!.Total);
        var group = Assert.Single(body.Groups);
        Assert.Equal(AiCapabilities.ImageEmbedding, group.Capability);
        Assert.Equal("provider", group.TargetKind);
        Assert.Equal("no-default-profile", group.ErrorCode);
        Assert.Equal(2, group.Count);
        Assert.False(group.IsPermanent);

        var raw = await response.Content.ReadAsStringAsync();
        foreach (var needle in ForbiddenNeedles)
        {
            Assert.DoesNotContain(needle, raw, StringComparison.Ordinal);
        }
    }
}
