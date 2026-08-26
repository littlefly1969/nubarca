using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Data;
using NubArca.Api.Tests.Endpoints;
using NubArca.Api.Uploads;

namespace NubArca.Api.Tests.Uploads;

// mobile-sync-v1: HTTP-level coverage for the OPTIONAL Idempotency-Key
// contract on POST /api/files. The guarantees under test:
//   * an ambiguous retry (lost response after durable commit) reconstructs the
//     ORIGINAL logical result instead of ingesting twice;
//   * the key is an OPERATION identity scoped by the authenticated owner —
//     never blob identity, never cross-user state;
//   * failed operations are never cached as successful;
//   * without the header, behavior is byte-for-byte unchanged (including the
//     ordinary duplicate-name 409), and physical blob deduplication stays the
//     server's SHA-256 content-addressed model.
public sealed class FileUploadIdempotencyEndpointTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory;

    public FileUploadIdempotencyEndpointTests()
    {
        _factory = new SqliteWebApplicationFactory();
        _factory.EnsureDatabaseCreated();
    }

    public void Dispose() => _factory.Dispose();

    private static MultipartFormDataContent MultipartWithFile(
        byte[] payload, string filename, string contentType)
    {
        var multipart = new MultipartFormDataContent();
        var part = new ByteArrayContent(payload);
        part.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        multipart.Add(part, "file", filename);
        return multipart;
    }

    private static HttpRequestMessage KeyedPost(
        byte[] payload, string filename, string key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/files")
        {
            Content = MultipartWithFile(payload, filename, "text/plain"),
        };
        request.Headers.Add("Idempotency-Key", key);
        return request;
    }

    private async Task<int> CountFileItemsAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.FileItems.CountAsync();
    }

    private async Task<(int blobs, int refs)> BlobStatsAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var blobs = await db.BlobObjects.CountAsync();
        var refs = await db.BlobObjects.SumAsync(b => (int?)b.ReferenceCount) ?? 0;
        return (blobs, refs);
    }

    [Fact]
    public async Task Post_Without_Header_Keeps_Legacy_Duplicate_Name_Behavior()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        var payload = "same-name"u8.ToArray();

        var first = await client.PostAsync("/api/files", MultipartWithFile(payload, "dup.txt", "text/plain"));
        var second = await client.PostAsync("/api/files", MultipartWithFile(payload, "dup.txt", "text/plain"));

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        // No idempotency key involved: the ordinary sibling rule still answers 409.
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal(1, await CountFileItemsAsync());
    }

    [Fact]
    public async Task Post_With_Invalid_Idempotency_Key_Returns_400()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();

        var response = await client.SendAsync(KeyedPost("x"u8.ToArray(), "x.txt", "bad key with spaces!"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Lost_Response_Retry_With_Same_Key_Replays_Original_Result()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        var payload = "committed-but-response-lost"u8.ToArray();

        // First attempt: the server durably commits but the response is lost.
        var first = await client.SendAsync(KeyedPost(payload, "report.txt", "sync-v1-lost-response"));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var original = await first.Content.ReadFromJsonAsync<Api.Files.FileSummary>();

        // The ambiguous retry arrives with the SAME operation key.
        var retry = await client.SendAsync(KeyedPost(payload, "report.txt", "sync-v1-lost-response"));

        Assert.NotNull(original);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        var replayed = await retry.Content.ReadFromJsonAsync<Api.Files.FileSummary>();
        Assert.NotNull(replayed);
        Assert.Equal(original!.Id, replayed!.Id);
        // One logical operation → exactly one FileItem.
        Assert.Equal(1, await CountFileItemsAsync());
    }

    [Fact]
    public async Task Replay_Survives_A_Fresh_Scope_Like_A_Server_Restart()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();

        var created = await client.SendAsync(
            KeyedPost("restart-proof"u8.ToArray(), "memo.txt", "sync-v1-restart-case"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var summary = await created.Content.ReadFromJsonAsync<Api.Files.FileSummary>();

        // Replay state lives in the SAME database as everything else, so a new
        // DI scope (what a restarted server would build on) reconstructs it.
        using var scope = _factory.Services.CreateScope();
        var idempotency = scope.ServiceProvider.GetRequiredService<IUploadIdempotencyService>();
        var replayed = await idempotency.FindCompletedResultAsync(owner, "sync-v1-restart-case");

        Assert.NotNull(summary);
        Assert.NotNull(replayed);
        Assert.Equal(summary!.Id, replayed!.Id);
    }

    [Fact]
    public async Task Different_Keys_Are_Different_Logical_Operations()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        var payload = "distinct-ops"u8.ToArray();

        foreach (var key in new[] { "sync-v1-op-a", "sync-v1-op-b" })
        {
            var response = await client.SendAsync(KeyedPost(payload, $"{key}.txt", key));
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }

        Assert.Equal(2, await CountFileItemsAsync());
    }

    [Fact]
    public async Task Same_Key_Under_Two_Owners_Stays_Isolated()
    {
        var (ownerA, clientA) = await _factory.CreateAuthenticatedClientAsync("owner-a@example.com");
        var (ownerB, clientB) = await _factory.CreateAuthenticatedClientAsync("owner-b@example.com");

        var responseA = await clientA.SendAsync(
            KeyedPost("content-A"u8.ToArray(), "shared-key.txt", "sync-v1-shared-key"));
        var responseB = await clientB.SendAsync(
            KeyedPost("content-B"u8.ToArray(), "shared-key.txt", "sync-v1-shared-key"));

        Assert.Equal(HttpStatusCode.Created, responseA.StatusCode);
        Assert.Equal(HttpStatusCode.Created, responseB.StatusCode);
        var summaryA = await responseA.Content.ReadFromJsonAsync<Api.Files.FileSummary>();
        var summaryB = await responseB.Content.ReadFromJsonAsync<Api.Files.FileSummary>();
        Assert.NotEqual(summaryA!.Id, summaryB!.Id);

        // Each owner's retry resolves ONLY to their own result.
        using var scope = _factory.Services.CreateScope();
        var idempotency = scope.ServiceProvider.GetRequiredService<IUploadIdempotencyService>();
        var replayA = await idempotency.FindCompletedResultAsync(ownerA, "sync-v1-shared-key");
        var replayB = await idempotency.FindCompletedResultAsync(ownerB, "sync-v1-shared-key");

        Assert.Equal(summaryA.Id, replayA!.Id);
        Assert.Equal(summaryB.Id, replayB!.Id);
    }

    [Fact]
    public async Task Concurrent_Claims_Of_One_Key_Admit_Exactly_One_Owner()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();

        using var scopeA = _factory.Services.CreateScope();
        using var scopeB = _factory.Services.CreateScope();
        var idempotencyA = scopeA.ServiceProvider.GetRequiredService<IUploadIdempotencyService>();
        var idempotencyB = scopeB.ServiceProvider.GetRequiredService<IUploadIdempotencyService>();

        var claims = await Task.WhenAll(
            idempotencyA.TryClaimAsync(owner, "sync-v1-race", TimeSpan.FromHours(1)),
            idempotencyB.TryClaimAsync(owner, "sync-v1-race", TimeSpan.FromHours(1)));

        // The unique (owner, key) index arbitrates: one Claimed, never two.
        Assert.Single(claims, c => c.Outcome == UploadClaimOutcome.Claimed);
        var winnerClaim = claims.Single(c => c.Outcome == UploadClaimOutcome.Claimed);
        var loser = claims.Single(c => c.Outcome != UploadClaimOutcome.Claimed);
        Assert.Equal(UploadClaimOutcome.InFlight, loser.Outcome);

        // Completing through the WINNER's token publishes the result; a later
        // claim of the same key observes completion rather than a second slot.
        var realFile = await client.PostAsync("/api/files",
            MultipartWithFile("race-winner-bytes"u8.ToArray(), "race-winner.txt", "text/plain"));
        Assert.Equal(HttpStatusCode.Created, realFile.StatusCode);
        var winnerSummary = await realFile.Content.ReadFromJsonAsync<Api.Files.FileSummary>();
        await idempotencyA.CompleteAsync(winnerClaim.Token, winnerSummary!.Id);
        var replayed = await idempotencyB.FindCompletedResultAsync(owner, "sync-v1-race");
        Assert.NotNull(replayed);
        Assert.Equal(winnerSummary.Id, replayed!.Id);
        var afterWin = await idempotencyB.TryClaimAsync(owner, "sync-v1-race", TimeSpan.FromHours(1));
        Assert.Equal(UploadClaimOutcome.AlreadyCompleted, afterWin.Outcome);
    }

    [Fact]
    public async Task Expired_Pending_Claim_Can_Be_Taken_Over_And_Dead_Tokens_Are_Impotent()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var staleKey = "sync-v1-crashed";
        Guid? seededFileId = null;

        // Simulate a crashed uploader: a pending claim whose lease has lapsed.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.UploadOperations.Add(new NubArca.Api.Domain.UploadOperation
            {
                Id = Guid.NewGuid(),
                OwnerUserId = owner,
                OperationKey = staleKey,
                Status = NubArca.Api.Domain.UploadOperationStatus.Pending,
                CreatedAt = DateTime.UtcNow.AddDays(-2),
                LeaseExpiresAt = DateTime.UtcNow.AddHours(-25),
            });
            await db.SaveChangesAsync();
        }

        using var takeoverScope = _factory.Services.CreateScope();
        var idempotency = takeoverScope.ServiceProvider.GetRequiredService<IUploadIdempotencyService>();
        var takeover = await idempotency.TryClaimAsync(owner, staleKey, TimeSpan.FromHours(24));
        Assert.Equal(UploadClaimOutcome.Claimed, takeover.Outcome);

        // The late original claimant can neither complete nor release with its
        // dead token: both statements match zero rows and change nothing.
        await idempotency.CompleteAsync(Guid.NewGuid(), Guid.NewGuid());
        await idempotency.ReleaseAsync(Guid.NewGuid());

        // Only the taken-over claim's token can publish a result — here with a
        // REAL ingested file id, exactly as the endpoint would.
        var realFile = await client.PostAsync("/api/files",
            MultipartWithFile("recovered-bytes"u8.ToArray(), "sync-v1-crashed-recovered.txt", "text/plain"));
        Assert.Equal(HttpStatusCode.Created, realFile.StatusCode);
        var recovered = await realFile.Content.ReadFromJsonAsync<Api.Files.FileSummary>();
        seededFileId = recovered!.Id;

        await idempotency.CompleteAsync(takeover.Token, seededFileId.Value);

        using var verify = _factory.Services.CreateScope();
        var db2 = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Single(await db2.UploadOperations.ToListAsync());
        var row = await db2.UploadOperations.SingleAsync(o => o.OperationKey == staleKey);
        Assert.Equal(NubArca.Api.Domain.UploadOperationStatus.Completed, row.Status);
        Assert.Equal(seededFileId, row.FileItemId);
    }

    [Fact]
    public async Task Failed_Upload_Is_Not_Cached_As_Successful()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();

        // Pre-existing sibling blocks the keyed attempt by name collision.
        var blocker = await client.PostAsync("/api/files",
            MultipartWithFile("blocker"u8.ToArray(), "taken.txt", "text/plain"));
        Assert.Equal(HttpStatusCode.Created, blocker.StatusCode);

        var conflict = await client.SendAsync(
            KeyedPost("doomed"u8.ToArray(), "taken.txt", "sync-v1-doomed"));
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);

        // The failure released the claim: a later attempt with the SAME key
        // starts clean instead of replaying the failed operation.
        var fresh = await client.SendAsync(
            KeyedPost("fresh"u8.ToArray(), "fresh.txt", "sync-v1-doomed"));

        Assert.Equal(HttpStatusCode.Created, fresh.StatusCode);
    }

    [Fact]
    public async Task Keyed_Identical_Content_Under_Two_Names_Still_Dedupes_Physically()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        var payload = "identical-bytes"u8.ToArray();

        var responseA = await client.SendAsync(
            KeyedPost(payload, "one.txt", "sync-v1-content-one"));
        var responseB = await client.SendAsync(
            KeyedPost(payload, "two.txt", "sync-v1-content-two"));
        Assert.Equal(HttpStatusCode.Created, responseA.StatusCode);
        Assert.Equal(HttpStatusCode.Created, responseB.StatusCode);

        // Two logical files, ONE physical content-addressed blob: the key never
        // bypasses or duplicates the server's SHA-256 storage model.
        var (blobs, refs) = await BlobStatsAsync();
        Assert.Equal(1, blobs);
        Assert.Equal(2, refs);
        Assert.Equal(2, await CountFileItemsAsync());
    }

    [Fact]
    public async Task Anonymous_Keyed_Upload_Still_Unauthorized()
    {
        var anonymous = _factory.CreateClient();

        var response = await anonymous.SendAsync(
            KeyedPost("x"u8.ToArray(), "x.txt", "sync-v1-anonymous"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}