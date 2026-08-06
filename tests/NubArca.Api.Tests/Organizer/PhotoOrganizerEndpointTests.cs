using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Files;
using NubArca.Api.Tests.Endpoints;
using NubArca.Api.Tests.Metadata;
using Xunit;

namespace NubArca.Api.Tests.Organizer;

// HTTP surface for the organizer: auth gating, request validation (400), and a
// no-leak check on the dry-run + run-status responses.
public sealed class PhotoOrganizerEndpointTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory;

    public PhotoOrganizerEndpointTests()
    {
        _factory = new SqliteWebApplicationFactory();
        _factory.EnsureDatabaseCreated();
    }

    public void Dispose() => _factory.Dispose();

    private async Task<Guid> SeedPhotoAsync(Guid owner, string name, DateTime embedded)
    {
        FileItem file;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var files = scope.ServiceProvider.GetRequiredService<IFileItemService>();
            file = await files.CreateAsync(owner, null, name, "image/png", new MemoryStream(ImageFixtures.PlainPng()));
        }
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.BlobMetadata.Where(m => m.BlobObjectId == file.BlobObjectId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(m => m.DateTaken, DateTime.SpecifyKind(embedded, DateTimeKind.Utc))
                    .SetProperty(m => m.DateTakenSource, "DateTimeOriginal")
                    .SetProperty(m => m.MediaCategory, "image"));
        }
        return file.Id;
    }

    private static object ValidRequest(string scope = "all") => new
    {
        scope,
        template = "yyyy/yyyy-MM-dd",
        missingDateBehavior = "skip",
        conflictPolicy = "keep_both",
        targetRootName = "Photos",
    };

    [Fact]
    public async Task DryRun_Requires_Auth()
    {
        var anon = _factory.CreateClient();
        var response = await anon.PostAsJsonAsync("/api/photo-organizer/date-taken/dry-run", ValidRequest());
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task DryRun_Rejects_Invalid_Template_With_400()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        var response = await client.PostAsJsonAsync("/api/photo-organizer/date-taken/dry-run",
            new { scope = "all", template = "yyyy/../etc", missingDateBehavior = "skip", conflictPolicy = "skip" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task DryRun_Returns_Summary_Without_Leaking_Internals()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        await SeedPhotoAsync(owner, "IMG.png", new DateTime(2024, 5, 17, 0, 0, 0, DateTimeKind.Utc));

        var response = await client.PostAsJsonAsync("/api/photo-organizer/date-taken/dry-run", ValidRequest());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"candidateCount\"", body);
        AssertNoLeak(body);
    }

    [Fact]
    public async Task Run_Then_Status_Is_Owner_Scoped_And_No_Leak()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        await SeedPhotoAsync(owner, "IMG.png", new DateTime(2024, 5, 17, 0, 0, 0, DateTimeKind.Utc));

        var runResp = await client.PostAsJsonAsync("/api/photo-organizer/date-taken/run", ValidRequest());
        Assert.Equal(HttpStatusCode.OK, runResp.StatusCode);
        var run = await runResp.Content.ReadFromJsonAsync<RunResult>();
        Assert.NotNull(run);

        var statusResp = await client.GetAsync($"/api/photo-organizer/date-taken/runs/{run!.RunId}");
        Assert.Equal(HttpStatusCode.OK, statusResp.StatusCode);
        var statusBody = await statusResp.Content.ReadAsStringAsync();
        AssertNoLeak(statusBody);

        // Another user cannot read this run.
        var (_, other) = await _factory.CreateAuthenticatedClientAsync("intruder@example.com");
        var foreign = await other.GetAsync($"/api/photo-organizer/date-taken/runs/{run.RunId}");
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
    }

    private static void AssertNoLeak(string json)
    {
        foreach (var needle in new[]
                 {
                     "storageKey", "StorageKey", "blobObjectId", "BlobObjectId",
                     "sha256", "Sha256", "fileItemId", "FileItemId",
                     "ownerUserId", "OwnerUserId", "objects/", "passwordHash",
                 })
        {
            Assert.DoesNotContain(needle, json, StringComparison.Ordinal);
        }
    }

    private sealed record RunResult(Guid RunId, Guid? JobId, string Status);
}
