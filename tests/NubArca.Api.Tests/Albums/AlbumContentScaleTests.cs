using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Files;
using NubArca.Api.Tests.Endpoints;
using NubArca.Api.Tests.Metadata;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace NubArca.Api.Tests.Albums;

// The album curation surface at SCALE: a paged read of the content in the
// album's own order (date taken, in the owner's direction, FileItemId breaking
// ties), and a thumbnail sized for a 56 px row.
//
// The datasets are realistic — 0, 1, 81 and 520 items — because the defect
// these tests exist for only shows at size: the whole album read on open.
//
// The invariant that runs through all of it: a page is never read at one
// version and continued at another — the version is the consistency boundary
// of curation, exactly as for edits, and turning the album around moves it.
public sealed class AlbumContentScaleTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory = new();

    public AlbumContentScaleTests() => _factory.EnsureDatabaseCreated();

    public void Dispose() => _factory.Dispose();

    private const string OwnerEmail = "alice@example.com";
    private const string EditorEmail = "bob@example.com";
    private const string ContributorEmail = "carol@example.com";
    private const string ViewerEmail = "dave@example.com";
    private const string StrangerEmail = "erin@example.com";
    private const int PageSize = 40;

    // ── Paged read ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(81)]
    [InlineData(520)]
    public async Task Paging_Visits_Every_Item_Once_In_The_Album_Order(int size)
    {
        var (ownerId, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var albumId = await CreateAlbumAsync(owner, "Big");
        await SeedItemsAsync(albumId, ownerId, size);

        var walk = await WalkAsync(owner, albumId, PageSize);

        // Nothing skipped, nothing repeated, and newest first.
        Assert.Equal(await DbOrderAsync(albumId), walk.Ids);
        Assert.Equal(size, walk.Total);
        Assert.Equal(Math.Max(1, (size + PageSize - 1) / PageSize), walk.Pages);

        // Turned around by the owner, the same walk is the exact reverse.
        await SetDirectionAsync(owner, albumId, "asc");
        var reversed = await WalkAsync(owner, albumId, PageSize);
        Assert.Equal(walk.Ids.AsEnumerable().Reverse(), reversed.Ids);
        Assert.Equal(await DbOrderAsync(albumId), reversed.Ids);
    }

    [Fact]
    public async Task Removing_From_A_Large_Album_Rewrites_No_Other_Row_And_Keeps_The_Order()
    {
        // The visible order is by date, so a removal no longer renumbers the
        // album's append order: no other row is read back and rewritten.
        var (ownerId, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var albumId = await CreateAlbumAsync(owner, "Big");
        await SeedItemsAsync(albumId, ownerId, 520);
        var victim = await FileOfAsync((await DbOrderAsync(albumId))[260]);
        var before = await AppendOrdersAsync(albumId);

        (await owner.DeleteAsync($"/api/albums/{albumId}/content/{victim}")).EnsureSuccessStatusCode();

        var after = await AppendOrdersAsync(albumId);
        before.Remove(victim);
        Assert.Equal(before, after);
        var walk = await WalkAsync(owner, albumId, PageSize);
        Assert.Equal(await DbOrderAsync(albumId), walk.Ids);
        Assert.Equal(519, walk.Total);
    }

    [Fact]
    public async Task Opening_A_Large_Album_Reads_One_Page_Not_The_Album()
    {
        // The regression this slice exists for: the content endpoint used to
        // materialise every row whatever the caller asked for.
        var (ownerId, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var albumId = await CreateAlbumAsync(owner, "Big");
        await SeedItemsAsync(albumId, ownerId, 520);

        var page = await ReadPageAsync(await owner.GetAsync($"/api/albums/{albumId}/content?limit={PageSize}"));

        Assert.Equal(PageSize, page.Items.Count);
        // …while still describing the whole album, so a row can say "1 of 520".
        Assert.Equal(520, page.TotalCount);
        Assert.Equal(ItemId(page.Items[^1]).ToString(), page.NextCursor);
    }

    [Fact]
    public async Task A_Page_Is_Never_Larger_Than_The_Ceiling()
    {
        var (ownerId, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var albumId = await CreateAlbumAsync(owner, "Big");
        await SeedItemsAsync(albumId, ownerId, 520);

        var page = await ReadPageAsync(await owner.GetAsync($"/api/albums/{albumId}/content?limit=100000"));
        Assert.Equal(100, page.Items.Count);
    }

    [Fact]
    public async Task The_Legacy_Read_Without_Paging_Parameters_Still_Returns_The_Whole_Album()
    {
        // A client that predates paging names none of the parameters. It keeps
        // working, and learns nothing is left to page through.
        var (ownerId, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var albumId = await CreateAlbumAsync(owner, "Big");
        await SeedItemsAsync(albumId, ownerId, 81);

        var page = await ReadPageAsync(await owner.GetAsync($"/api/albums/{albumId}/content"));

        Assert.Equal(81, page.Items.Count);
        Assert.Equal(81, page.TotalCount);
        Assert.Null(page.NextCursor);
        Assert.Equal(await DbOrderAsync(albumId), page.Items.Select(ItemId).ToList());
    }

    [Theory]
    [InlineData("desc")]
    [InlineData("asc")]
    public async Task Photos_Taken_At_The_Same_Moment_Still_Page_Without_Duplicates_Or_Gaps(string direction)
    {
        // A burst shares its date taken; FileItemId is the final tie-break, and
        // the keyset must honour it in both directions or a page boundary
        // between two equal rows would drop one of them.
        var (ownerId, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var albumId = await CreateAlbumAsync(owner, "Ties");
        await SeedItemsAsync(albumId, ownerId, 81, takenAt: i => BaseTakenAt.AddMinutes(i / 3));
        await SetDirectionAsync(owner, albumId, direction);

        var walk = await WalkAsync(owner, albumId, limit: 10);

        Assert.Equal(await DbOrderAsync(albumId), walk.Ids);
        Assert.Equal(81, walk.Ids.Distinct().Count());
    }

    [Fact]
    public async Task The_Order_Is_The_Date_Taken_Not_The_Order_Items_Were_Added()
    {
        var (ownerId, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var albumId = await CreateAlbumAsync(owner, "Shuffled");
        // Added in one order, taken in a scrambled one.
        await SeedItemsAsync(albumId, ownerId, 81, takenAt: i => BaseTakenAt.AddDays((i * 37) % 81));

        var walk = await WalkAsync(owner, albumId, PageSize);
        var taken = await TakenAtAsync(albumId);
        var dates = walk.Ids.Select(id => taken[id]).ToList();
        Assert.Equal(dates.OrderByDescending(d => d), dates);
        Assert.NotEqual(await AddedOrderAsync(albumId), walk.Ids);
    }

    [Fact]
    public async Task A_Continuation_Is_Bound_To_The_Version_It_Was_Read_At()
    {
        var (ownerId, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var albumId = await CreateAlbumAsync(owner, "Big");
        await SeedItemsAsync(albumId, ownerId, 81);

        var first = await ReadPageAsync(await owner.GetAsync($"/api/albums/{albumId}/content?limit={PageSize}"));

        // The owner turns the album around while the first page is on screen.
        await SetDirectionAsync(owner, albumId, "asc");

        // Continuing at the old version is a conflict that names the current
        // one — never a page of the new album appended to rows of the old.
        var stale = await owner.GetAsync(
            $"/api/albums/{albumId}/content?limit={PageSize}&cursor={first.NextCursor}&expectedVersion={first.Version}");
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var body = await stale.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(first.Version + 1, body.GetProperty("version").GetInt32());

        // At the current version the same cursor is a valid continuation.
        var fresh = await owner.GetAsync(
            $"/api/albums/{albumId}/content?limit={PageSize}&cursor={first.NextCursor}&expectedVersion={first.Version + 1}");
        Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
    }

    [Fact]
    public async Task A_Malformed_Unversioned_Or_Foreign_Cursor_Is_Refused()
    {
        var (ownerId, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var albumId = await CreateAlbumAsync(owner, "Big");
        var otherAlbum = await CreateAlbumAsync(owner, "Other");
        await SeedItemsAsync(albumId, ownerId, 81);
        await SeedItemsAsync(otherAlbum, ownerId, 3);
        var first = await ReadPageAsync(await owner.GetAsync($"/api/albums/{albumId}/content?limit={PageSize}"));
        var foreign = (await DbOrderAsync(otherAlbum))[0];

        foreach (var query in new[]
                 {
                     $"limit={PageSize}&cursor=not-a-guid&expectedVersion={first.Version}",
                     // A continuation without a version could silently span two.
                     $"limit={PageSize}&cursor={first.NextCursor}",
                     $"limit={PageSize}&cursor={foreign}&expectedVersion={first.Version}",
                 })
        {
            var response = await owner.GetAsync($"/api/albums/{albumId}/content?{query}");
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }

    [Fact]
    public async Task Only_A_Curator_May_Page()
    {
        var (ownerId, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var (_, editor) = await _factory.CreateAuthenticatedClientAsync(EditorEmail);
        var (_, contributor) = await _factory.CreateAuthenticatedClientAsync(ContributorEmail);
        var (_, viewer) = await _factory.CreateAuthenticatedClientAsync(ViewerEmail);
        var (_, stranger) = await _factory.CreateAuthenticatedClientAsync(StrangerEmail);
        var albumId = await CreateAlbumAsync(owner, "Big");
        await SeedItemsAsync(albumId, ownerId, 81);
        await InviteAcceptAsync(owner, editor, albumId, EditorEmail, "editor");
        await InviteAcceptAsync(owner, contributor, albumId, ContributorEmail, "contributor");
        await InviteAcceptAsync(owner, viewer, albumId, ViewerEmail, "viewer");

        // The Owner and an Editor read through the same route.
        foreach (var curator in new[] { owner, editor })
        {
            var page = await curator.GetAsync($"/api/albums/{albumId}/content?limit={PageSize}");
            Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        }

        // A member who may not curate reads nothing here, and a stranger cannot
        // tell the album exists.
        foreach (var member in new[] { contributor, viewer, stranger })
        {
            Assert.Equal(HttpStatusCode.NotFound,
                (await member.GetAsync($"/api/albums/{albumId}/content?limit={PageSize}")).StatusCode);
        }
    }

    [Fact]
    public async Task An_Unavailable_Item_Keeps_Its_Place_In_The_Date_Order()
    {
        var (ownerId, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var albumId = await CreateAlbumAsync(owner, "Big");
        await SeedItemsAsync(albumId, ownerId, 81, unavailableAt: 50);
        var ids = await DbOrderAsync(albumId);

        var walk = await WalkAsync(owner, albumId, PageSize);
        // Moderation still reaches it, where its date puts it: seeded 51st of 81,
        // newest first it is the 31st row.
        Assert.Equal(ids, walk.Ids);
        var unavailable = walk.Rows.FindIndex(r => r.GetProperty("sourceState").GetString() == "unavailable");
        Assert.Equal(30, unavailable);
        Assert.Single(walk.Rows, r => r.GetProperty("sourceState").GetString() == "unavailable");
    }

    [Fact]
    public async Task The_Cover_Survives_A_Turnaround_And_Is_Flagged_On_Whichever_Page_Holds_It()
    {
        var (ownerId, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var albumId = await CreateAlbumAsync(owner, "Big");
        await SeedItemsAsync(albumId, ownerId, 81);
        var ids = await DbOrderAsync(albumId);
        var coverFile = await FileOfAsync(ids[45]);

        (await owner.PutAsJsonAsync($"/api/shared-albums/{albumId}/cover",
            new { expectedVersion = await VersionAsync(owner, albumId), fileItemId = coverFile }))
            .EnsureSuccessStatusCode();

        var walk = await WalkAsync(owner, albumId, PageSize);
        Assert.Equal(new[] { ids[45] }, walk.Rows.Where(r => r.GetProperty("isCover").GetBoolean()).Select(ItemId));

        Assert.Equal(1, walk.Rows.FindIndex(r => r.GetProperty("isCover").GetBoolean()) / PageSize);

        // Turning the album around leaves the choice alone; the cover is now on
        // the first page instead of the second.
        await SetDirectionAsync(owner, albumId, "asc");
        var first = await ReadPageAsync(await owner.GetAsync($"/api/albums/{albumId}/content?limit={PageSize}"));
        Assert.Equal(coverFile, first.CoverFileItemId);
        Assert.Equal(new[] { ids[45] }, first.Items.Where(r => r.GetProperty("isCover").GetBoolean()).Select(ItemId));
    }

    // ── Curation thumbnails ─────────────────────────────────────────────────

    [Fact]
    public async Task Curation_Rows_Address_A_Micro_Icon_For_Photos_And_Videos()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var albumId = await CreateAlbumAsync(owner, "Mixed");
        var photo = await UploadPngAsync(owner, "wide.png", 400, 300);
        var video = await UploadAsync(owner, ImageFixtures.MinimalMp4(), "clip.mp4", "video/mp4");
        await AddAsync(owner, albumId, photo);
        await AddAsync(owner, albumId, video);

        var page = await ReadPageAsync(await owner.GetAsync($"/api/albums/{albumId}/content?limit={PageSize}"));
        var urls = page.Items.ToDictionary(
            i => i.GetProperty("fileItemId").GetGuid(),
            i => i.GetProperty("thumbnailUrl").GetString()!);
        Assert.Equal($"/api/files/{photo}/thumbnail?size=micro", urls[photo]);
        Assert.Equal($"/api/files/{video}/thumbnail?size=micro", urls[video]);

        // Each is an icon, not the grid thumbnail: at most 96 px on its long
        // edge, and far lighter than the small derivative of the same photo.
        var photoIcon = await FetchImageAsync(owner, urls[photo]);
        Assert.True(Math.Max(photoIcon.Width, photoIcon.Height) <= ThumbnailSizes.DefaultMicroMaxEdge);
        var small = await owner.GetByteArrayAsync($"/api/files/{photo}/thumbnail?size=small");
        Assert.True(photoIcon.Bytes < small.Length);

        var videoIcon = await FetchImageAsync(owner, urls[video]);
        Assert.True(Math.Max(videoIcon.Width, videoIcon.Height) <= ThumbnailSizes.DefaultMicroMaxEdge);

        // An ordinary cached derivative, regenerable like any other.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(2, await db.FileThumbnails.CountAsync(
            t => (t.FileItemId == photo || t.FileItemId == video) && t.Size == ThumbnailSizes.Micro));
    }

    [Fact]
    public async Task A_Contribution_Row_Uses_The_Album_Scoped_Micro_Icon()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var (_, editor) = await _factory.CreateAuthenticatedClientAsync(EditorEmail);
        var (_, contributor) = await _factory.CreateAuthenticatedClientAsync(ContributorEmail);
        var (_, stranger) = await _factory.CreateAuthenticatedClientAsync(StrangerEmail);
        var albumId = await CreateAlbumAsync(owner, "Shared");
        await InviteAcceptAsync(owner, editor, albumId, EditorEmail, "editor");
        await InviteAcceptAsync(owner, contributor, albumId, ContributorEmail, "contributor");
        var theirs = await UploadPngAsync(contributor, "theirs.png", 400, 300);
        (await contributor.PostAsJsonAsync($"/api/shared-albums/{albumId}/contributions",
            new { fileItemId = theirs })).EnsureSuccessStatusCode();

        var page = await ReadPageAsync(await owner.GetAsync($"/api/albums/{albumId}/content?limit={PageSize}"));
        var url = page.Items.Single().GetProperty("thumbnailUrl").GetString()!;
        // The owner does not own these bytes, so the icon comes through the album.
        Assert.Equal($"/api/shared-albums/{albumId}/media/{theirs}/thumbnail?size=micro", url);

        foreach (var curator in new[] { owner, editor })
        {
            var icon = await FetchImageAsync(curator, url);
            Assert.True(Math.Max(icon.Width, icon.Height) <= ThumbnailSizes.DefaultMicroMaxEdge);
        }
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync(url)).StatusCode);
        // The route still offers only the sizes it names.
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.GetAsync(
            $"/api/shared-albums/{albumId}/media/{theirs}/thumbnail?size=medium")).StatusCode);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private sealed record ContentPage(
        int Version, int TotalCount, string? NextCursor, Guid? CoverFileItemId, List<JsonElement> Items);

    private sealed record Walk(List<Guid> Ids, List<JsonElement> Rows, int Pages, int Version, int Total);

    private static Guid ItemId(JsonElement item) => item.GetProperty("albumItemId").GetGuid();

    private static async Task<ContentPage> ReadPageAsync(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var cover = body.GetProperty("coverFileItemId");
        var cursor = body.GetProperty("nextCursor");
        return new ContentPage(
            body.GetProperty("version").GetInt32(),
            body.GetProperty("totalCount").GetInt32(),
            cursor.ValueKind == JsonValueKind.Null ? null : cursor.GetString(),
            cover.ValueKind == JsonValueKind.Null ? null : cover.GetGuid(),
            body.GetProperty("items").EnumerateArray().ToList());
    }

    // Reads the whole album the way the curation list does: the first page,
    // then each continuation after the last row held, at the version the first
    // page was read at — asserting every page stays inside the contract.
    private static async Task<Walk> WalkAsync(HttpClient client, Guid albumId, int limit)
    {
        var page = await ReadPageAsync(await client.GetAsync($"/api/albums/{albumId}/content?limit={limit}"));
        var version = page.Version;
        var total = page.TotalCount;
        var rows = new List<JsonElement>();
        var pages = 1;
        while (true)
        {
            Assert.True(page.Items.Count <= limit);
            Assert.Equal(version, page.Version);
            Assert.Equal(total, page.TotalCount);
            rows.AddRange(page.Items);
            if (page.NextCursor is null)
            {
                break;
            }
            Assert.Equal(ItemId(page.Items[^1]).ToString(), page.NextCursor);
            page = await ReadPageAsync(await client.GetAsync(
                $"/api/albums/{albumId}/content?limit={limit}&cursor={page.NextCursor}&expectedVersion={version}"));
            pages++;
        }
        return new Walk(rows.Select(ItemId).ToList(), rows, pages, version, total);
    }

    private static async Task SetDirectionAsync(HttpClient owner, Guid albumId, string direction) =>
        (await owner.PutAsJsonAsync($"/api/albums/{albumId}/sort", new { direction })).EnsureSuccessStatusCode();

    private static async Task<int> VersionAsync(HttpClient curator, Guid albumId) =>
        (await ReadPageAsync(await curator.GetAsync($"/api/albums/{albumId}/content?limit=1"))).Version;

    private static async Task<(int Width, int Height, long Bytes)> FetchImageAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        response.EnsureSuccessStatusCode();
        Assert.Equal("image/jpeg", response.Content.Headers.ContentType?.MediaType);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        var info = Image.Identify(bytes);
        return (info.Width, info.Height, bytes.Length);
    }

    // Realistic volume without hundreds of uploads: one stored image, shared by
    // every file the way exact dedup shares it, each file its own album row.
    private async Task SeedItemsAsync(
        Guid albumId, Guid ownerId, int count, Func<int, DateTime>? takenAt = null, int unavailableAt = -1)
    {
        if (count == 0)
        {
            return;
        }

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTime.UtcNow;
        var blobId = Guid.NewGuid();
        db.BlobObjects.Add(new BlobObject
        {
            Id = blobId,
            Sha256 = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant(),
            StorageKey = $"seed/{blobId:N}",
            SizeBytes = 1024,
            ReferenceCount = count,
            CreatedAt = now,
        });
        db.BlobMetadata.Add(new BlobMetadata
        {
            Id = Guid.NewGuid(),
            BlobObjectId = blobId,
            SizeBytes = 1024,
            MediaCategory = MediaCategories.Image,
            DetectedContentType = "image/jpeg",
            DetectedFormat = "JPEG",
            Width = 4000,
            Height = 3000,
            PixelCount = 12_000_000,
        });
        for (var i = 0; i < count; i++)
        {
            var fileId = Guid.NewGuid();
            db.FileItems.Add(new FileItem
            {
                Id = fileId,
                OwnerUserId = ownerId,
                BlobObjectId = blobId,
                Name = $"seed-{albumId:N}-{i:D4}.jpg",
                MimeType = "image/jpeg",
                SizeBytes = 1024,
                Width = 4000,
                Height = 3000,
                CreatedAt = now,
                // Distinct by default, the later the seed index the newer.
                EffectiveDateTaken = takenAt?.Invoke(i) ?? BaseTakenAt.AddMinutes(i),
                DeletedAt = i == unavailableAt ? now : null,
            });
            db.AlbumItems.Add(new AlbumItem
            {
                Id = Guid.NewGuid(),
                AlbumId = albumId,
                FileItemId = fileId,
                AddedAt = now.AddSeconds(i),
                AddedByUserId = ownerId,
                SortOrder = i + 1,
            });
        }
        await db.SaveChangesAsync();
    }

    private static readonly DateTime BaseTakenAt = new(2020, 6, 1, 12, 0, 0, DateTimeKind.Utc);

    // The order every surface serves: date taken in the album's direction, then
    // FileItemId in the same direction.
    private async Task<List<Guid>> DbOrderAsync(Guid albumId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var oldestFirst = await db.Albums.Where(a => a.Id == albumId).Select(a => a.SortOldestFirst).SingleAsync();
        var rows = db.AlbumItems.AsNoTracking()
            .Where(ai => ai.AlbumId == albumId)
            .Join(db.FileItems.IgnoreQueryFilters(), ai => ai.FileItemId, f => f.Id,
                (ai, f) => new { ai.Id, ai.FileItemId, f.EffectiveDateTaken });
        var ordered = oldestFirst
            ? rows.OrderBy(x => x.EffectiveDateTaken).ThenBy(x => x.FileItemId)
            : rows.OrderByDescending(x => x.EffectiveDateTaken).ThenByDescending(x => x.FileItemId);
        return await ordered.Select(x => x.Id).ToListAsync();
    }

    private async Task<List<Guid>> AddedOrderAsync(Guid albumId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.AlbumItems.AsNoTracking()
            .Where(ai => ai.AlbumId == albumId)
            .OrderBy(ai => ai.SortOrder)
            .Select(ai => ai.Id)
            .ToListAsync();
    }

    private async Task<Dictionary<Guid, int>> AppendOrdersAsync(Guid albumId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.AlbumItems.AsNoTracking()
            .Where(ai => ai.AlbumId == albumId)
            .ToDictionaryAsync(ai => ai.FileItemId, ai => ai.SortOrder);
    }

    private async Task<Dictionary<Guid, DateTime>> TakenAtAsync(Guid albumId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.AlbumItems.AsNoTracking()
            .Where(ai => ai.AlbumId == albumId)
            .Join(db.FileItems.IgnoreQueryFilters(), ai => ai.FileItemId, f => f.Id,
                (ai, f) => new { ai.Id, f.EffectiveDateTaken })
            .ToDictionaryAsync(x => x.Id, x => x.EffectiveDateTaken);
    }

    private async Task<Guid> FileOfAsync(Guid albumItemId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.AlbumItems.AsNoTracking()
            .Where(ai => ai.Id == albumItemId)
            .Select(ai => ai.FileItemId)
            .SingleAsync();
    }

    private static async Task<Guid> CreateAlbumAsync(HttpClient owner, string name)
    {
        var response = await owner.PostAsJsonAsync("/api/albums", new { name });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task<Guid> UploadPngAsync(HttpClient client, string name, int width, int height)
    {
        using var img = new Image<Rgba32>(width, height);
        var tint = (byte)(name.Aggregate(17, (acc, c) => (acc * 31 + c) & 0xFF));
        img[0, 0] = new Rgba32(tint, tint, tint, 255);
        using var ms = new MemoryStream();
        img.Save(ms, new PngEncoder());
        return await UploadAsync(client, ms.ToArray(), name, "image/png");
    }

    private static async Task<Guid> UploadAsync(HttpClient client, byte[] bytes, string name, string mime)
    {
        var part = new ByteArrayContent(bytes);
        part.Headers.ContentType = new MediaTypeHeaderValue(mime);
        var multipart = new MultipartFormDataContent { { part, "file", name } };
        var response = await client.PostAsync("/api/files", multipart);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task AddAsync(HttpClient owner, Guid albumId, Guid fileId) =>
        (await owner.PostAsJsonAsync($"/api/albums/{albumId}/items", new { fileItemId = fileId }))
            .EnsureSuccessStatusCode();

    private static async Task InviteAcceptAsync(
        HttpClient owner, HttpClient member, Guid albumId, string email, string role)
    {
        var invite = await owner.PostAsJsonAsync($"/api/albums/{albumId}/members", new { email, role });
        invite.EnsureSuccessStatusCode();
        var membershipId = (await invite.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("membershipId").GetGuid();
        (await member.PostAsync($"/api/shared-albums/invitations/{membershipId}/accept", null))
            .EnsureSuccessStatusCode();
    }
}
