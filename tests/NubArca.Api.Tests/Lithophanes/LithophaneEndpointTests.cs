using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Access;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Files;
using NubArca.Api.Jobs;
using NubArca.Api.Jobs.Handlers;
using NubArca.Api.Lithophanes;
using NubArca.Api.Storage;
using NubArca.Api.Tests.Endpoints;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace NubArca.Api.Tests.Lithophanes;

public sealed class LithophaneEndpointTests
{
    private static readonly LithophaneSettings Fast = new() { DiameterMm = 40, OpeningMm = 10, ResolutionMm = 1 };
    private const string Root = "/api/lithophanes";
    private static SqliteWebApplicationFactory Factory(bool sliced = false)
    {
        var f = new SqliteWebApplicationFactory(sliced ? new Dictionary<string, string?>
        {
            ["Jobs:MaintenanceSliceItemBudget"] = "1",
        } : null);
        f.EnsureDatabaseCreated(); return f;
    }

    private static async Task<Guid> Upload(HttpClient client, string name = "portrait.png")
    {
        using var image = new Image<Rgba32>(64, 40, new Rgba32(110, 70, 160));
        using var bytes = new MemoryStream(); await image.SaveAsPngAsync(bytes);
        using var part = new ByteArrayContent(bytes.ToArray());
        part.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        using var form = new MultipartFormDataContent { { part, "file", name } };
        var response = await client.PostAsync("/api/files", form); response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<FileSummary>())!.Id;
    }
    private static async Task<LithophaneSummary> Create(HttpClient c, Guid file, Guid? requestId = null)
    {
        var r = await c.PostAsJsonAsync(Root, new LithophaneCreateRequest(file, requestId ?? Guid.NewGuid(), Fast));
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<LithophaneSummary>())!;
    }
    private static async Task Step(SqliteWebApplicationFactory f)
    {
        using var scope = f.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<JobProcessor>().ProcessAvailableAsync(1);
    }
    private static async Task Advance(SqliteWebApplicationFactory f, Guid projectId, int slice)
    {
        for (var i = 0; i < 50; i++)
        {
            using var scope = f.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var jobId = await db.LithophaneProjects.Where(x => x.Id == projectId).Select(x => x.JobId).SingleAsync();
            var job = await db.BackgroundJobs.AsNoTracking().SingleAsync(x => x.Id == jobId);
            if (job.SliceNumber >= slice || JobStatuses.IsTerminal(job.Status)) return;
            await Step(f); // foreground post-upload jobs must be allowed to run first
        }
        Assert.Fail("Expected lithophane slice did not run.");
    }
    private static async Task<LithophaneSummary> Finish(SqliteWebApplicationFactory f, HttpClient c, Guid id)
    {
        for (var i = 0; i < 100; i++)
        {
            var result = await c.GetFromJsonAsync<LithophaneSummary>($"{Root}/{id}");
            if (result!.Status is "ready" or "failed" or "cancelled") return result;
            await Step(f);
        }
        throw new Exception("Lithophane did not finish within bounded test slices.");
    }

    [Fact]
    public async Task Real_Workflow_Resumes_Across_Scopes_And_Download_Matches_The_Preview_Map()
    {
        using var f = Factory(sliced: true);
        var (_, c) = await f.CreateAuthenticatedClientAsync();
        var file = await Upload(c);
        var previewResponse = await c.PostAsJsonAsync($"{Root}/preview", new LithophanePreviewRequest(file, Fast));
        previewResponse.EnsureSuccessStatusCode();
        var preview = (await previewResponse.Content.ReadFromJsonAsync<LithophanePreview>())!;
        var project = await Create(c, file);
        await Advance(f, project.Id, 1); // one preprocessing unit, then continuation
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var job = await db.BackgroundJobs.SingleAsync(x => x.Type == JobTypes.LithophaneGenerate);
            Assert.Equal(JobStatuses.Queued, job.Status); Assert.Equal(1, job.SliceNumber);
            Assert.NotNull(job.CheckpointJson);
            Assert.Equal(JobScheduling.Normal, job.Priority);
        }
        await Advance(f, project.Id, 2);
        using (var scope = f.Services.CreateScope())
        {
            // The strip committed, but the worker died before committing the
            // matching queue checkpoint. Recovery uses durable strip ownership.
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.BackgroundJobs.Where(x => x.Type == JobTypes.LithophaneGenerate)
                .ExecuteUpdateAsync(x => x.SetProperty(j => j.Status, JobStatuses.Running)
                    .SetProperty(j => j.LockOwner, "lost-test-worker")
                    .SetProperty(j => j.LeaseUntil, DateTime.UtcNow.AddMinutes(-5))
                    .SetProperty(j => j.CheckpointJson, (string?)null));
        }
        var ready = await Finish(f, c, project.Id);
        Assert.Equal("ready", ready.Status); Assert.True(ready.DownloadReady);
        Assert.Equal(ready.TotalRows, ready.CompletedRows);
        var download = await c.GetAsync($"{Root}/{project.Id}/download");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("model/stl", download.Content.Headers.ContentType!.MediaType);
        Assert.Equal("no-store", download.Headers.CacheControl!.ToString());
        var actual = await download.Content.ReadAsByteArrayAsync();
        using var map = Image.Load<L8>(Convert.FromBase64String(preview.ImageBase64));
        using var expected = new MemoryStream(); var grid = new LithophaneGrid(Fast);
        LithophaneMesh.WriteHeader(expected, grid); LithophaneMesh.WriteStrip(expected, Fast, map, 0, grid.Rows);
        Assert.Equal(expected.ToArray(), actual);
        var saved = (await c.GetFromJsonAsync<LithophanePreview>($"{Root}/{project.Id}/preview"))!;
        Assert.Equal(preview.ImageBase64, saved.ImageBase64);
        var raw = await c.GetStringAsync($"{Root}/{project.Id}");
        foreach (var forbidden in new[] { "storageKey", "blobObjectId", "sha256", "payloadJson", "checkpointJson", "/storage/", "lockOwner" })
            Assert.DoesNotContain(forbidden, raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Idempotency_Is_Owner_Scoped_And_Rejects_A_Changed_Request()
    {
        using var f = Factory(); var (_, c) = await f.CreateAuthenticatedClientAsync();
        var file = await Upload(c); var key = Guid.NewGuid();
        var first = await Create(c, file, key); var repeat = await Create(c, file, key);
        Assert.Equal(first.Id, repeat.Id);
        var conflict = await c.PostAsJsonAsync(Root, new LithophaneCreateRequest(file, key, Fast with { DiameterMm = 45 }));
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await db.LithophaneProjects.CountAsync());
        Assert.Equal(1, await db.BackgroundJobs.CountAsync(x => x.Type == JobTypes.LithophaneGenerate));
    }

    [Fact]
    public async Task Every_Surface_Is_Owner_Private_Including_Batch_Status_And_Source_Selection()
    {
        using var f = Factory(); var (_, owner) = await f.CreateAuthenticatedClientAsync();
        var file = await Upload(owner); var project = await Create(owner, file);
        var (_, other) = await f.CreateAuthenticatedClientAsync("other@example.com");
        foreach (var suffix in new[] { "", "/preview", "/download" })
            Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"{Root}/{project.Id}{suffix}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.DeleteAsync($"{Root}/{project.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsync($"{Root}/{project.Id}/cancel", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsJsonAsync($"{Root}/preview", new LithophanePreviewRequest(file, Fast))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsJsonAsync(Root, new LithophaneCreateRequest(file, Guid.NewGuid(), Fast))).StatusCode);
        var batch = await other.PostAsJsonAsync($"{Root}/statuses", new { ids = new[] { project.Id } });
        Assert.Empty((await batch.Content.ReadFromJsonAsync<List<LithophaneSummary>>())!);
        Assert.Empty((await other.GetFromJsonAsync<LithophanePage>(Root))!.Items);
    }

    [Fact]
    public async Task Both_Permissions_Are_Required_And_Worker_Rechecks_Revocation_Between_Slices()
    {
        using var f = Factory(sliced: true);
        using var anonymous = f.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(Root)).StatusCode);
        var (ownerId, c) = await f.CreatePermissionClientAsync("lab@example.com", Permissions.LaboratoryAccess, Permissions.LaboratoryLithophanes);
        var file = await Upload(c); var project = await Create(c, file);
        await Advance(f, project.Id, 1);
        string roleKey;
        // The user DTO's role is the role key; write malformed permission shapes
        // directly to prove the composite still holds even against stale data.
        using (var scope = f.Services.CreateScope())
            roleKey = (await scope.ServiceProvider.GetRequiredService<AppDbContext>().Users.SingleAsync(x => x.Id == ownerId)).RoleKey;
        await f.SetRolePermissionsRawAsync(roleKey, Permissions.LaboratoryLithophanes);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync(Root)).StatusCode);
        await Advance(f, project.Id, 2);
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var job = await db.BackgroundJobs.SingleAsync(x => x.Type == JobTypes.LithophaneGenerate);
            Assert.Equal(JobStatuses.Cancelled, job.Status);
            Assert.Null(job.LastErrorCode);
            Assert.Null((await db.LithophaneProjects.SingleAsync()).ArtifactStorageKey);
        }
        await f.SetRolePermissionsRawAsync(roleKey, Permissions.LaboratoryAccess);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.PostAsJsonAsync($"{Root}/preview", new LithophanePreviewRequest(file, Fast))).StatusCode);
    }

    [Fact]
    public async Task Cancellation_Is_Terminal_And_Deletion_Releases_Only_Unshared_Artifacts()
    {
        using var f = Factory(sliced: true); var (_, c) = await f.CreateAuthenticatedClientAsync();
        var file = await Upload(c); var cancelled = await Create(c, file);
        await Advance(f, cancelled.Id, 1);
        (await c.PostAsync($"{Root}/{cancelled.Id}/cancel", null)).EnsureSuccessStatusCode();
        Assert.Equal("cancelled", (await Finish(f, c, cancelled.Id)).Status);
        var first = await Create(c, file); var second = await Create(c, file);
        Assert.Equal("ready", (await Finish(f, c, first.Id)).Status);
        Assert.Equal("ready", (await Finish(f, c, second.Id)).Status);
        using (var scope = f.Services.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<StorageReconciliationService>();
            var result = await service.RunAsync(new() { DryRun = false, DeleteOrphans = true, MinimumOrphanAge = TimeSpan.Zero });
            Assert.True(result.ProtectedNonBlobObjects > 0);
        }
        (await c.DeleteAsync($"{Root}/{first.Id}")).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"{Root}/{first.Id}/download")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync($"{Root}/{second.Id}/download")).StatusCode);
        (await c.DeleteAsync($"{Root}/{second.Id}")).EnsureSuccessStatusCode();
        (await c.DeleteAsync($"{Root}/{cancelled.Id}")).EnsureSuccessStatusCode();
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Empty(await db.LithophaneProjects.ToListAsync()); Assert.Empty(await db.LithophaneStrips.ToListAsync());
        }
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync($"/api/files/{file}/content")).StatusCode);
    }

    [Fact]
    public async Task A_Deleted_Source_Cancels_Before_Preprocessing_And_A_Source_Purge_Is_Not_Pinned()
    {
        using var f = Factory(); var (_, c) = await f.CreateAuthenticatedClientAsync();
        var file = await Upload(c); var project = await Create(c, file);
        (await c.DeleteAsync($"/api/files/{file}")).EnsureSuccessStatusCode();
        Assert.Equal("cancelled", (await Finish(f, c, project.Id)).Status);
        var rejected = await c.PostAsJsonAsync(Root, new LithophaneCreateRequest(file, Guid.NewGuid(), Fast));
        Assert.Equal(HttpStatusCode.NotFound, rejected.StatusCode);
        (await c.DeleteAsync($"/api/trash/files/{file}")).EnsureSuccessStatusCode();
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Null((await db.LithophaneProjects.SingleAsync()).FileItemId);
    }

    [Fact]
    public async Task Bad_Settings_NonImages_And_Overlarge_Batches_Are_Rejected()
    {
        using var f = Factory(); var (_, c) = await f.CreateAuthenticatedClientAsync();
        var file = await Upload(c);
        foreach (var bad in new[] { Fast with { DiameterMm = 0 }, Fast with { ResolutionMm = 0 },
            Fast with { MaxThicknessMm = 0.5 }, Fast with { DiameterMm = 200, ResolutionMm = 0.15 } })
            Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync(Root, new LithophaneCreateRequest(file, Guid.NewGuid(), bad))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync($"{Root}/statuses", new { ids = Enumerable.Repeat(Guid.NewGuid(), 101) })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync(Root, new { fileItemId = file, requestId = Guid.NewGuid(), settings = (object?)null })).StatusCode);
        using (var form = new MultipartFormDataContent { { new StringContent("not a photograph"), "file", "notes.txt" } })
        {
            var text = await c.PostAsync("/api/files", form); text.EnsureSuccessStatusCode();
            var textId = (await text.Content.ReadFromJsonAsync<FileSummary>())!.Id;
            Assert.Equal(HttpStatusCode.NotFound, (await c.PostAsJsonAsync(Root,
                new LithophaneCreateRequest(textId, Guid.NewGuid(), Fast))).StatusCode);
        }
        using var scope = f.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var source = await db.FileItems.SingleAsync(x => x.Id == file);
        source.SizeBytes = 65 * 1024 * 1024; await db.SaveChangesAsync();
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await c.PostAsJsonAsync(Root, new LithophaneCreateRequest(file, Guid.NewGuid(), Fast))).StatusCode);
    }

    [Fact]
    public async Task History_Is_Keyset_Paged_And_Creation_Has_A_PerOwner_Hard_Limit()
    {
        using var f = Factory(); var (owner, c) = await f.CreateAuthenticatedClientAsync();
        var file = await Upload(c);
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var now = DateTime.UtcNow;
            for (var i = 0; i < LithophaneService.MaxProjectsPerOwner; i++)
                db.LithophaneProjects.Add(new LithophaneProject
                {
                    Id = Guid.NewGuid(), OwnerUserId = owner, RequestId = Guid.NewGuid(), FileItemId = file,
                    JobId = Guid.NewGuid(), Name = $"photo {i}", SettingsJson = JsonSerializer.Serialize(Fast),
                    CreatedAt = now.AddSeconds(-i),
                });
            await db.SaveChangesAsync();
        }
        var ids = new HashSet<Guid>(); Guid? cursor = null;
        do
        {
            var page = (await c.GetFromJsonAsync<LithophanePage>(Root + (cursor is null ? "" : $"?cursor={cursor}")))!;
            Assert.InRange(page.Items.Count, 1, 20);
            Assert.All(page.Items, x => Assert.True(ids.Add(x.Id)));
            cursor = page.NextCursor;
        } while (cursor is not null);
        Assert.Equal(LithophaneService.MaxProjectsPerOwner, ids.Count);
        var response = await c.PostAsJsonAsync(Root, new LithophaneCreateRequest(file, Guid.NewGuid(), Fast));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("project_limit", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public void Api_And_Cli_Use_One_Handler_And_Service_Registration()
    {
        var services = new ServiceCollection(); services.AddNubArcaLithophanes(); services.AddNubArcaJobHandlers();
        Assert.Single(services, x => x.ImplementationType == typeof(LithophaneGenerateJobHandler));
        Assert.Single(services, x => x.ServiceType == typeof(LithophaneService));
    }
}
