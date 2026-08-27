using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace NubArca.Api.Tests;

public class HealthEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public HealthEndpointTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Postgres", "");
        });
    }

    [Fact]
    public async Task Health_Returns_Ok_With_Status_Ok()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<HealthResponse>();
        Assert.NotNull(payload);
        Assert.Equal("ok", payload!.Status);
    }

    [Fact]
    public async Task The_Private_Documents_Feature_Does_Not_Stop_A_Databaseless_Host_Booting()
    {
        // This fixture runs with an EMPTY connection string, which is a
        // supported configuration — and the reason it is worth a named test is
        // that the owner-private feature broke it once. Its corpus source exists
        // only when a database does, and registering the service with a plain
        // `AddScoped<T>()` made the container's startup graph validation fail:
        // the whole application refused to boot, not merely the feature.
        //
        // A host with no database has no private knowledge, so the honest
        // behaviour is a feature that reports itself unavailable — which is
        // something a running process can say and a crashed one cannot.
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/assistant/documents/status");

        // Unauthenticated, so the answer is a refusal rather than a status — the
        // point is that the SERVER answered at all.
        Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    private sealed record HealthResponse(string Status);
}
