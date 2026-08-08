using System.Net;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Domain;
using NubArca.Api.Files;
using NubArca.Api.Tests.Endpoints;
using NubArca.Api.Tests.Metadata;
using Xunit;

namespace NubArca.Api.Tests.Cast;

// Minting a bearer capability is the one Cast action worth capping. Segment
// fetches deliberately are not — a two-hour film is hundreds of them, and the
// URL they use is already scoped to one video with an expiry.
//
// The limit here is tightened to 3/60s so the assertion costs four requests
// instead of twenty-one; the production default is 20/minute per user.
public sealed class CastGrantRateLimitTests : IDisposable
{
    private const int Limit = 3;

    private readonly SqliteWebApplicationFactory _factory;

    public CastGrantRateLimitTests()
    {
        _factory = new SqliteWebApplicationFactory(new Dictionary<string, string?>
        {
            ["Media:VideoHlsProvider"] = "ffmpeg",
            ["RateLimits:CastGrantCreate:PermitLimit"] = Limit.ToString(),
            ["RateLimits:CastGrantCreate:WindowSeconds"] = "60",
        });
        _factory.EnsureDatabaseCreated();
    }

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Grant_Creation_Is_Rate_Limited_Per_User()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        FileItem file;
        using (var scope = _factory.Services.CreateScope())
        {
            var files = scope.ServiceProvider.GetRequiredService<IFileItemService>();
            var head = ImageFixtures.MinimalMp4();
            var bytes = new byte[1024];
            Array.Copy(head, bytes, head.Length);
            for (var i = head.Length; i < bytes.Length; i++) bytes[i] = (byte)(i & 0xFF);
            file = await files.CreateAsync(
                owner, null, "clip.mp4", "video/mp4", new MemoryStream(bytes));
        }

        // The ladder is not published, so each of these is a cheap 202 — the
        // point is the LIMITER, not the outcome.
        for (var i = 0; i < Limit; i++)
        {
            var response = await client.PostAsync($"/api/cast/videos/{file.Id}/grant", null);
            Assert.NotEqual(HttpStatusCode.TooManyRequests, response.StatusCode);
        }

        var overLimit = await client.PostAsync($"/api/cast/videos/{file.Id}/grant", null);
        Assert.Equal(HttpStatusCode.TooManyRequests, overLimit.StatusCode);

        // A DIFFERENT user is not caught by the first user's budget: the
        // partition is the account, not the address they share.
        var (_, other) = await _factory.CreateAuthenticatedClientAsync("other@example.com");
        var otherResponse = await other.PostAsync($"/api/cast/videos/{file.Id}/grant", null);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, otherResponse.StatusCode);
    }
}
