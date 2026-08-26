using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
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
    public async Task InFlight_Key_Answers_A_Structured_Retryable_409_Unlike_A_Name_Conflict()
    {
        // BLOCKER 2, server side of the contract. Both conditions answer 409,
        // so the ONLY thing separating them on the wire is the stable
        // structured marker — never the human-readable message.
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();

        // A genuine in-flight operation: a live claim on this key is held by
        // an attempt that has not finished yet.
        using (var scope = _factory.Services.CreateScope())
        {
            var idempotency = scope.ServiceProvider.GetRequiredService<IUploadIdempotencyService>();
            var claim = await idempotency.TryClaimAsync(
                owner, "sync-v1-inflight", UploadIdempotencyService.DefaultLease);
            Assert.Equal(UploadClaimOutcome.Claimed, claim.Outcome);
        }

        var inFlight = await client.SendAsync(
            KeyedPost("second-attempt"u8.ToArray(), "inflight.txt", "sync-v1-inflight"));

        Assert.Equal(HttpStatusCode.Conflict, inFlight.StatusCode);
        using var inFlightBody = JsonDocument.Parse(await inFlight.Content.ReadAsStringAsync());
        Assert.Equal("upload_in_progress", inFlightBody.RootElement.GetProperty("code").GetString());
        Assert.True(inFlightBody.RootElement.GetProperty("retryable").GetBoolean());
        // The concurrent attempt ingested NOTHING: no second logical file.
        Assert.Equal(0, await CountFileItemsAsync());

        // An ORDINARY duplicate-name conflict carries no such marker, so a
        // client following the contract keeps treating it as permanent.
        var payload = "same-name"u8.ToArray();
        var created = await client.PostAsync(
            "/api/files", MultipartWithFile(payload, "dup.txt", "text/plain"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var duplicate = await client.PostAsync(
            "/api/files", MultipartWithFile(payload, "dup.txt", "text/plain"));

        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        var duplicateText = await duplicate.Content.ReadAsStringAsync();
        var marked = false;
        if (!string.IsNullOrWhiteSpace(duplicateText))
        {
            using var duplicateBody = JsonDocument.Parse(duplicateText);
            marked = duplicateBody.RootElement.ValueKind == JsonValueKind.Object
                && (duplicateBody.RootElement.TryGetProperty("code", out _)
                    || duplicateBody.RootElement.TryGetProperty("retryable", out _));
        }
        Assert.False(marked, "a duplicate-name 409 must not carry the retryable marker");
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

        // The WINNER publishes through the ATOMIC ingestion path: a keyed file
        // creation completes the operation inside its own transaction. (This
        // is exactly what the endpoint does on behalf of a keyed request.)
        Guid fileId;
        using (var scope = _factory.Services.CreateScope())
        {
            var files = scope.ServiceProvider.GetRequiredService<NubArca.Api.Files.IFileItemService>();
            await using var content = new MemoryStream("race-winner-bytes"u8.ToArray());
            var created = await files.CreateAsync(
                owner, null, "race-winner.txt", "text/plain", content,
                uploadOperationClaimToken: winnerClaim.Token);
            fileId = created.Id;
        }

        // The loser now observes a completed operation — replay, never a
        // second slot or a second logical ingestion.
        var replayed = await idempotencyB.FindCompletedResultAsync(owner, "sync-v1-race");
        Assert.NotNull(replayed);
        Assert.Equal(fileId, replayed!.Id);
        var afterWin = await idempotencyB.TryClaimAsync(owner, "sync-v1-race", TimeSpan.FromHours(1));
        Assert.Equal(UploadClaimOutcome.AlreadyCompleted, afterWin.Outcome);
    }

    [Fact]
    public async Task Keyed_Creation_Completes_The_Operation_Atomically()
    {
        // THE crash-boundary invariant: once CreateAsync returns, there is no
        // observable state where the FileItem is durable while its keyed
        // operation is still pending — completion happens INSIDE the file
        // transaction, so both facts exist together or not at all.
        var (owner, _) = await _factory.CreateAuthenticatedClientAsync();

        Guid token;
        using (var scope = _factory.Services.CreateScope())
        {
            var idempotency = scope.ServiceProvider.GetRequiredService<IUploadIdempotencyService>();
            var claim = await idempotency.TryClaimAsync(
                owner, "sync-v1-atomic", UploadIdempotencyService.DefaultLease);
            Assert.Equal(UploadClaimOutcome.Claimed, claim.Outcome);
            token = claim.Token;
        }

        Guid createdFileId;
        using (var scope = _factory.Services.CreateScope())
        {
            var files = scope.ServiceProvider.GetRequiredService<NubArca.Api.Files.IFileItemService>();
            await using var content = new MemoryStream("atomic-bytes"u8.ToArray());
            var created = await files.CreateAsync(
                owner, null, "atomic.txt", "text/plain", content,
                uploadOperationClaimToken: token);
            createdFileId = created.Id;
        }

        using var verify = _factory.Services.CreateScope();
        var db = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.UploadOperations.SingleAsync(o => o.OperationKey == "sync-v1-atomic");
        Assert.Equal(NubArca.Api.Domain.UploadOperationStatus.Completed, row.Status);
        Assert.Equal(createdFileId, row.FileItemId);
        Assert.Equal(1, await db.FileItems.CountAsync());
    }

    [Fact]
    public async Task Claim_Lost_Mid_Ingestion_Commits_No_FileItem_And_No_Operation_State()
    {
        // Failure path: the pending claim vanishes between TryClaim and the
        // authoritative transaction (crash-recovery takeover / concurrent
        // completion). The keyed ingestion MUST abort instead of committing an
        // unassociated FileItem.
        var (owner, _) = await _factory.CreateAuthenticatedClientAsync();

        Guid staleToken;
        using (var scope = _factory.Services.CreateScope())
        {
            var idempotency = scope.ServiceProvider.GetRequiredService<IUploadIdempotencyService>();
            var claim = await idempotency.TryClaimAsync(
                owner, "sync-v1-lost", UploadIdempotencyService.DefaultLease);
            Assert.Equal(UploadClaimOutcome.Claimed, claim.Outcome);
            staleToken = claim.Token;

            // Simulate the takeover/crash that removes OUR pending row.
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.UploadOperations
                .Where(o => o.Id == staleToken)
                .ExecuteDeleteAsync();
        }

        using var actScope = _factory.Services.CreateScope();
        var files = actScope.ServiceProvider.GetRequiredService<NubArca.Api.Files.IFileItemService>();
        await using var content = new MemoryStream("doomed-bytes"u8.ToArray());
        await Assert.ThrowsAsync<UploadOperationClaimLostException>(() =>
            files.CreateAsync(
                owner, null, "doomed.txt", "text/plain", content,
                uploadOperationClaimToken: staleToken));

        var db2 = actScope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(0, await db2.FileItems.CountAsync());
        Assert.Equal(0, await db2.UploadOperations.CountAsync());
    }

    [Fact]
    public async Task Expired_Pending_Claim_Can_Be_Taken_Over_And_Dead_Tokens_Are_Impotent()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var staleKey = "sync-v1-crashed";

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

        // The late original claimant wakes up and tries to ingest with its
        // DEAD token: the atomic completion matches zero rows, the ingestion
        // ABORTS (UploadOperationClaimLostException) and commits nothing.
        using (var deadScope = _factory.Services.CreateScope())
        {
            var files = deadScope.ServiceProvider.GetRequiredService<NubArca.Api.Files.IFileItemService>();
            await using var content = new MemoryStream("late-crashed-bytes"u8.ToArray());
            await Assert.ThrowsAsync<UploadOperationClaimLostException>(() =>
                files.CreateAsync(
                    owner, null, "late-crashed.txt", "text/plain", content,
                    uploadOperationClaimToken: Guid.NewGuid()));
            var dbFiles = deadScope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(0, await dbFiles.FileItems.CountAsync());
        }

        // Only the taken-over claim's token can publish a result — here with a
        // REAL ingested file id, exactly as the endpoint would.
        var realFile = await client.PostAsync("/api/files",
            MultipartWithFile("recovered-bytes"u8.ToArray(), "sync-v1-crashed-recovered.txt", "text/plain"));
        Assert.Equal(HttpStatusCode.Created, realFile.StatusCode);
        await realFile.Content.ReadFromJsonAsync<Api.Files.FileSummary>();

        Guid takeoverFileId;
        using (var publishScope = _factory.Services.CreateScope())
        {
            var files = publishScope.ServiceProvider.GetRequiredService<NubArca.Api.Files.IFileItemService>();
            await using var content = new MemoryStream("takeover-published-bytes"u8.ToArray());
            var created = await files.CreateAsync(
                owner, null, "sync-v1-takeover.txt", "text/plain", content,
                uploadOperationClaimToken: takeover.Token);
            takeoverFileId = created.Id;
        }

        using var verify = _factory.Services.CreateScope();
        var db2 = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Single(await db2.UploadOperations.ToListAsync());
        var row = await db2.UploadOperations.SingleAsync(o => o.OperationKey == staleKey);
        Assert.Equal(NubArca.Api.Domain.UploadOperationStatus.Completed, row.Status);
        Assert.Equal(takeoverFileId, row.FileItemId);
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