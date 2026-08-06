using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Files;
using NubArca.Api.Folders;
using NubArca.Api.MediaLibrary;
using NubArca.Api.Metadata;
using NubArca.Api.Tests.Endpoints;
using NubArca.Api.Tests.Metadata;
using Xunit;

namespace NubArca.Api.Tests.MediaLibrary;

// Slice 94 — media-library scope: folder rules decide gallery/media-job
// membership (opt-out, nearest rule wins, child re-include under excluded
// parent) without ever touching file-browser visibility, downloads, or
// previews; plus the rules API surface and its no-leak posture.
public sealed class MediaLibraryTests
{
    private static async Task<(Guid UserId, HttpClient Client)> AuthAsync(
        SqliteWebApplicationFactory factory, string email = "owner@example.com")
    {
        factory.EnsureDatabaseCreated();
        return await factory.CreateAuthenticatedClientAsync(email);
    }

    private static async Task<Folder> CreateFolderAsync(
        SqliteWebApplicationFactory factory, Guid ownerId, Guid? parentId, string name)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var folders = scope.ServiceProvider.GetRequiredService<IFolderService>();
        return await folders.CreateAsync(ownerId, parentId, name);
    }

    private static async Task<FileItem> UploadAsync(
        SqliteWebApplicationFactory factory, Guid ownerId, Guid? folderId, string name, byte[] bytes, string mime)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var files = scope.ServiceProvider.GetRequiredService<IFileItemService>();
        return await files.CreateAsync(ownerId, folderId, name, mime, new MemoryStream(bytes));
    }

    private static Task<FileItem> UploadImageAsync(
        SqliteWebApplicationFactory factory, Guid ownerId, Guid? folderId, string name)
        => UploadAsync(factory, ownerId, folderId, name, ImageFixtures.PlainPng(), "image/png");

    private static async Task<IReadOnlyList<string>> GalleryNamesAsync(HttpClient client, string url = "/api/images?limit=100")
    {
        var page = await client.GetFromJsonAsync<GalleryPageShape>(url);
        return page!.Items.Select(i => i.Name).ToList();
    }

    private sealed record GalleryItemShape(Guid Id, string Name);
    private sealed record GalleryPageShape(List<GalleryItemShape> Items);

    private static async Task<HttpResponseMessage> PutRuleAsync(
        HttpClient client, Guid folderId, string ruleType = "exclude",
        bool photos = true, bool videos = true, bool children = true)
        => await client.PutAsJsonAsync("/api/media-library/rules", new
        {
            folderId,
            ruleType,
            appliesToPhotos = photos,
            appliesToVideos = videos,
            appliesToChildren = children,
        });

    // ---- default + exclusion behaviour ------------------------------------

    [Fact]
    public async Task Default_Without_Rules_All_Media_Is_In_The_Gallery()
    {
        using var factory = new SqliteWebApplicationFactory();
        var (userId, client) = await AuthAsync(factory);
        var folder = await CreateFolderAsync(factory, userId, null, "Documents");
        await UploadImageAsync(factory, userId, folder.Id, "in-folder.png");
        await UploadImageAsync(factory, userId, null, "at-root.png");

        var names = await GalleryNamesAsync(client);
        Assert.Contains("in-folder.png", names);
        Assert.Contains("at-root.png", names);
    }

    [Fact]
    public async Task Excluding_A_Folder_Hides_Its_Media_From_Gallery_But_Not_From_Browser()
    {
        using var factory = new SqliteWebApplicationFactory();
        var (userId, client) = await AuthAsync(factory);
        var docs = await CreateFolderAsync(factory, userId, null, "Documents");
        var sub = await CreateFolderAsync(factory, userId, docs.Id, "Scans");
        await UploadImageAsync(factory, userId, docs.Id, "doc.png");
        await UploadImageAsync(factory, userId, sub.Id, "scan.png");
        await UploadImageAsync(factory, userId, null, "holiday.png");

        (await PutRuleAsync(client, docs.Id)).EnsureSuccessStatusCode();

        // Gallery: the excluded subtree is gone, the rest stays.
        var names = await GalleryNamesAsync(client);
        Assert.DoesNotContain("doc.png", names);
        Assert.DoesNotContain("scan.png", names);
        Assert.Contains("holiday.png", names);

        // Legacy offset mode applies the same rule.
        var legacyBody = await (await client.GetAsync("/api/images?limit=100&offset=0"))
            .Content.ReadAsStringAsync();
        Assert.DoesNotContain("doc.png", legacyBody);
        Assert.Contains("holiday.png", legacyBody);

        // File browser: completely unaffected.
        var browser = await client.GetAsync($"/api/folders/{docs.Id}/children");
        browser.EnsureSuccessStatusCode();
        var body = await browser.Content.ReadAsStringAsync();
        Assert.Contains("doc.png", body);
    }

    [Fact]
    public async Task Child_Include_Overrides_Parent_Exclude_And_Most_Specific_Wins()
    {
        using var factory = new SqliteWebApplicationFactory();
        var (userId, client) = await AuthAsync(factory);
        var docs = await CreateFolderAsync(factory, userId, null, "Documents");
        var family = await CreateFolderAsync(factory, userId, docs.Id, "FamilyPhotos");
        var nested = await CreateFolderAsync(factory, userId, family.Id, "Trip");
        await UploadImageAsync(factory, userId, docs.Id, "doc.png");
        await UploadImageAsync(factory, userId, family.Id, "family.png");
        await UploadImageAsync(factory, userId, nested.Id, "trip.png");

        (await PutRuleAsync(client, docs.Id, "exclude")).EnsureSuccessStatusCode();
        (await PutRuleAsync(client, family.Id, "include")).EnsureSuccessStatusCode();

        var names = await GalleryNamesAsync(client);
        Assert.DoesNotContain("doc.png", names);     // excluded by /Documents
        Assert.Contains("family.png", names);        // re-included
        Assert.Contains("trip.png", names);          // inherits the re-include
    }

    [Fact]
    public async Task Video_Only_Rule_Keeps_Photos_Visible()
    {
        using var factory = new SqliteWebApplicationFactory();
        var (userId, client) = await AuthAsync(factory);
        var movies = await CreateFolderAsync(factory, userId, null, "Movies");
        await UploadImageAsync(factory, userId, movies.Id, "cover.png");
        await UploadAsync(factory, userId, movies.Id, "clip.mp4", ImageFixtures.MinimalMp4(), "video/mp4");

        (await PutRuleAsync(client, movies.Id, "exclude", photos: false, videos: true))
            .EnsureSuccessStatusCode();

        Assert.Contains("cover.png", await GalleryNamesAsync(client));
        var videos = await client.GetFromJsonAsync<GalleryPageShape>("/api/videos?limit=100");
        Assert.DoesNotContain(videos!.Items, v => v.Name == "clip.mp4");
    }

    [Fact]
    public async Task AppliesToChildren_False_Excludes_Only_Direct_Files()
    {
        using var factory = new SqliteWebApplicationFactory();
        var (userId, client) = await AuthAsync(factory);
        var top = await CreateFolderAsync(factory, userId, null, "Mixed");
        var sub = await CreateFolderAsync(factory, userId, top.Id, "Keep");
        await UploadImageAsync(factory, userId, top.Id, "direct.png");
        await UploadImageAsync(factory, userId, sub.Id, "nested.png");

        (await PutRuleAsync(client, top.Id, "exclude", children: false)).EnsureSuccessStatusCode();

        var names = await GalleryNamesAsync(client);
        Assert.DoesNotContain("direct.png", names);
        Assert.Contains("nested.png", names);
    }

    [Fact]
    public async Task New_Subfolder_Under_Excluded_Parent_Inherits_The_Exclusion()
    {
        using var factory = new SqliteWebApplicationFactory();
        var (userId, client) = await AuthAsync(factory);
        var docs = await CreateFolderAsync(factory, userId, null, "Documents");
        (await PutRuleAsync(client, docs.Id)).EnsureSuccessStatusCode();

        // Folder created AFTER the rule inherits the exclusion at creation.
        var later = await CreateFolderAsync(factory, userId, docs.Id, "Later");
        await UploadImageAsync(factory, userId, later.Id, "late.png");
        Assert.DoesNotContain("late.png", await GalleryNamesAsync(client));
    }

    [Fact]
    public async Task Moving_A_Folder_Into_An_Excluded_Parent_Recomputes_Eligibility()
    {
        using var factory = new SqliteWebApplicationFactory();
        var (userId, client) = await AuthAsync(factory);
        var docs = await CreateFolderAsync(factory, userId, null, "Documents");
        var pics = await CreateFolderAsync(factory, userId, null, "Pics");
        await UploadImageAsync(factory, userId, pics.Id, "pic.png");
        (await PutRuleAsync(client, docs.Id)).EnsureSuccessStatusCode();

        Assert.Contains("pic.png", await GalleryNamesAsync(client));

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var folders = scope.ServiceProvider.GetRequiredService<IFolderService>();
            await folders.MoveAsync(userId, pics.Id, docs.Id);
        }
        Assert.DoesNotContain("pic.png", await GalleryNamesAsync(client));

        // And moving it back restores visibility.
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var folders = scope.ServiceProvider.GetRequiredService<IFolderService>();
            await folders.MoveAsync(userId, pics.Id, null);
        }
        Assert.Contains("pic.png", await GalleryNamesAsync(client));
    }

    // ---- excluded files keep working outside media surfaces -----------------

    [Fact]
    public async Task Excluded_File_Still_Downloads_And_Previews_On_Demand()
    {
        using var factory = new SqliteWebApplicationFactory();
        var (userId, client) = await AuthAsync(factory);
        var docs = await CreateFolderAsync(factory, userId, null, "Documents");
        var file = await UploadImageAsync(factory, userId, docs.Id, "doc.png");
        (await PutRuleAsync(client, docs.Id)).EnsureSuccessStatusCode();

        // Manual open from the file browser: download + lazy thumbnail still work.
        Assert.Equal(HttpStatusCode.OK,
            (await client.GetAsync($"/api/files/{file.Id}/content")).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await client.GetAsync($"/api/files/{file.Id}/thumbnail?size=small")).StatusCode);
    }

    [Fact]
    public async Task Derivative_Backfill_Skips_Excluded_Media()
    {
        using var factory = new SqliteWebApplicationFactory();
        var (userId, client) = await AuthAsync(factory);
        var docs = await CreateFolderAsync(factory, userId, null, "Documents");

        FileItem excluded, included;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var files = scope.ServiceProvider.GetRequiredService<IFileItemService>();
            // generateSmallThumbnail:false so the backfill has work to find.
            excluded = await files.CreateAsync(userId, docs.Id, "ex.png", "image/png",
                new MemoryStream(ImageFixtures.PlainPng()), generateSmallThumbnail: false);
            included = await files.CreateAsync(userId, null, "in.png", "image/png",
                new MemoryStream(ImageFixtures.PlainPng(width: 17)), generateSmallThumbnail: false);
        }
        (await PutRuleAsync(client, docs.Id)).EnsureSuccessStatusCode();

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var backfill = scope.ServiceProvider.GetRequiredService<MediaDerivativesBackfillService>();
            await backfill.RunAsync(new MediaDerivativesBackfillOptions());
        }

        await using var verify = factory.Services.CreateAsyncScope();
        var db = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.True(await db.FileThumbnails.AnyAsync(t => t.FileItemId == included.Id));
        // The batch job never touched the excluded file…
        Assert.False(await db.FileThumbnails.AnyAsync(t => t.FileItemId == excluded.Id));

        // …but the lazy on-request path still generates for it (manual open).
        Assert.Equal(HttpStatusCode.OK,
            (await client.GetAsync($"/api/files/{excluded.Id}/thumbnail?size=small")).StatusCode);
    }

    // ---- ownership + API surface ------------------------------------------------

    [Fact]
    public async Task Rules_Are_Owner_Scoped_And_Never_Apply_Across_Users()
    {
        using var factory = new SqliteWebApplicationFactory();
        var (aliceId, alice) = await AuthAsync(factory, "alice@example.com");
        var aliceDocs = await CreateFolderAsync(factory, aliceId, null, "Docs");
        await UploadImageAsync(factory, aliceId, aliceDocs.Id, "alice.png");

        await factory.SeedUserAsync("bob@example.com");
        var bob = await factory.LoginAsync("bob@example.com");

        // Bob cannot create/see rules on Alice's folder (no-leak 404).
        Assert.Equal(HttpStatusCode.NotFound, (await PutRuleAsync(bob, aliceDocs.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await bob.GetAsync($"/api/media-library/effective?folderId={aliceDocs.Id}")).StatusCode);

        // Alice's own rule never leaks into Bob's rule list, and Bob's rules
        // cannot affect Alice's gallery.
        (await PutRuleAsync(alice, aliceDocs.Id)).EnsureSuccessStatusCode();
        var bobRules = await bob.GetFromJsonAsync<MediaLibraryRulesResponse>("/api/media-library/rules");
        Assert.Empty(bobRules!.Rules);
        Assert.DoesNotContain("alice.png", await GalleryNamesAsync(alice));

        // Unauthenticated → 401.
        var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.GetAsync("/api/media-library/rules")).StatusCode);
    }

    [Fact]
    public async Task Effective_Endpoint_Reports_Default_Explicit_And_Inherited()
    {
        using var factory = new SqliteWebApplicationFactory();
        var (userId, client) = await AuthAsync(factory);
        var docs = await CreateFolderAsync(factory, userId, null, "Documents");
        var sub = await CreateFolderAsync(factory, userId, docs.Id, "Scans");

        // Default before any rule.
        var initial = await client.GetFromJsonAsync<MediaLibraryEffectiveResponse>(
            $"/api/media-library/effective?folderId={sub.Id}");
        Assert.False(initial!.Photos.Excluded);
        Assert.Equal("default", initial.Photos.Source);
        Assert.Null(initial.Rule);

        (await PutRuleAsync(client, docs.Id)).EnsureSuccessStatusCode();

        // The rule's own folder reports an explicit rule…
        var onDocs = await client.GetFromJsonAsync<MediaLibraryEffectiveResponse>(
            $"/api/media-library/effective?folderId={docs.Id}");
        Assert.True(onDocs!.Photos.Excluded);
        Assert.Equal("rule", onDocs.Photos.Source);
        Assert.NotNull(onDocs.Rule);

        // …and the child reports inheritance, naming the source folder.
        var onSub = await client.GetFromJsonAsync<MediaLibraryEffectiveResponse>(
            $"/api/media-library/effective?folderId={sub.Id}");
        Assert.True(onSub!.Photos.Excluded);
        Assert.Equal("inherited", onSub.Photos.Source);
        Assert.Equal(docs.Id, onSub.Photos.SourceFolderId);
        Assert.Equal("Documents", onSub.Photos.SourceFolderName);
    }

    [Fact]
    public async Task Deleting_A_Rule_Restores_Visibility_And_Invalid_Rules_Are_400()
    {
        using var factory = new SqliteWebApplicationFactory();
        var (userId, client) = await AuthAsync(factory);
        var docs = await CreateFolderAsync(factory, userId, null, "Documents");
        await UploadImageAsync(factory, userId, docs.Id, "doc.png");

        var put = await PutRuleAsync(client, docs.Id);
        put.EnsureSuccessStatusCode();
        var rule = await put.Content.ReadFromJsonAsync<MediaLibraryRuleDto>();
        Assert.DoesNotContain("doc.png", await GalleryNamesAsync(client));

        Assert.Equal(HttpStatusCode.NoContent,
            (await client.DeleteAsync($"/api/media-library/rules/{rule!.Id}")).StatusCode);
        Assert.Contains("doc.png", await GalleryNamesAsync(client));

        // Unknown rule id → 404; bad input → 400.
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.DeleteAsync($"/api/media-library/rules/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await PutRuleAsync(client, docs.Id, "banana")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await PutRuleAsync(client, docs.Id, photos: false, videos: false)).StatusCode);
    }

    [Fact]
    public async Task Stats_Count_Eligible_And_Excluded_Media()
    {
        using var factory = new SqliteWebApplicationFactory();
        var (userId, client) = await AuthAsync(factory);
        var docs = await CreateFolderAsync(factory, userId, null, "Documents");
        // Distinct bytes so the two photos do NOT dedup onto one blob.
        await UploadAsync(factory, userId, docs.Id, "doc.png", ImageFixtures.PlainPng(width: 18), "image/png");
        await UploadImageAsync(factory, userId, null, "root.png");
        (await PutRuleAsync(client, docs.Id)).EnsureSuccessStatusCode();

        var stats = await client.GetFromJsonAsync<MediaLibraryStatsResponse>("/api/media-library/stats");
        Assert.Equal(1, stats!.PhotosEligible);
        Assert.Equal(1, stats.PhotosExcluded);
        Assert.Equal(1, stats.RuleCount);
        Assert.True(stats.BlobsTotal >= 2);
    }

    [Fact]
    public async Task MediaLibrary_Responses_Do_Not_Leak_Internals()
    {
        using var factory = new SqliteWebApplicationFactory();
        var (userId, client) = await AuthAsync(factory);
        var docs = await CreateFolderAsync(factory, userId, null, "Documents");
        await UploadImageAsync(factory, userId, docs.Id, "gps.jpg");
        (await PutRuleAsync(client, docs.Id)).EnsureSuccessStatusCode();

        var bodies = new[]
        {
            await (await client.GetAsync("/api/media-library/rules")).Content.ReadAsStringAsync(),
            await (await client.GetAsync($"/api/media-library/effective?folderId={docs.Id}")).Content.ReadAsStringAsync(),
            await (await client.GetAsync("/api/media-library/stats")).Content.ReadAsStringAsync(),
        };
        foreach (var body in bodies)
        {
            foreach (var needle in MetadataExposurePolicy.ForbiddenInResponses)
            {
                Assert.DoesNotContain(needle, body, StringComparison.OrdinalIgnoreCase);
            }
            // GPS coordinates must never appear in any media-library payload.
            Assert.DoesNotContain("latitude", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("longitude", body, StringComparison.OrdinalIgnoreCase);
        }
    }
}
