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

// The album curation surface at SCALE: a paged read of the content, a
// single-item move, and a thumbnail sized for a 56 px row.
//
// The datasets are realistic — 0, 1, 81 and 520 items — because the defect
// these tests exist for only shows at size: the whole album read on open, and
// the whole id sequence re-sent on every move.
//
// Two invariants run through all of it:
//   * a page is never read at one version and continued at another — the
//     version is the consistency boundary of curation, exactly as for edits;
//   * a move renumbers only the rows between its two positions, and a stale one
//     changes nothing.
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
    public async Task Paging_Visits_Every_Item_Once_In_The_Curated_Order(int size)
    {
        var (ownerId, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var albumId = await CreateAlbumAsync(owner, "Big");
        await SeedItemsAsync(albumId, ownerId, size);

        var walk = await WalkAsync(owner, albumId, PageSize);

        // Nothing skipped, nothing repeated, and in exactly the stored order.
        Assert.Equal(await DbOrderAsync(albumId), walk.Ids);
        Assert.Equal(size, walk.Total);
        Assert.Equal(Math.Max(1, (size + PageSize - 1) / PageSize), walk.Pages);
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

    [Fact]
    public async Task Rows_That_Share_A_SortOrder_Still_Page_Without_Duplicates_Or_Gaps()
    {
        // SortOrder is never assumed unique; FileItemId is the final tie-break,
        // and the keyset must honour it or a page boundary between two equal
        // rows would drop one of them.
        var (ownerId, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var albumId = await CreateAlbumAsync(owner, "Ties");
        await SeedItemsAsync(albumId, ownerId, 81, sortOrder: i => i / 3);

        var walk = await WalkAsync(owner, albumId, limit: 10);

        Assert.Equal(await DbOrderAsync(albumId), walk.Ids);
        Assert.Equal(81, walk.Ids.Distinct().Count());
    }

    [Fact]
    public async Task A_Continuation_Is_Bound_To_The_Version_It_Was_Read_At()
    {
        var (ownerId, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var albumId = await CreateAlbumAsync(owner, "Big");
        await SeedItemsAsync(albumId, ownerId, 81);

        var first = await ReadPageAsync(await owner.GetAsync($"/api/albums/{albumId}/content?limit={PageSize}"));

        // Somebody changes the album while the first page is on screen.
        var ids = await DbOrderAsync(albumId);
        (await MoveAsync(owner, albumId, ids[70], first.Version, 0)).EnsureSuccessStatusCode();

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

    // ── Single-item move ────────────────────────────────────────────────────

    [Theory]
    [InlineData(10, 9)]   // up
    [InlineData(10, 11)]  // down
    [InlineData(10, 0)]   // to the start
    [InlineData(10, 80)]  // to the end
    [InlineData(39, 40)]  // down across the first page boundary
    [InlineData(40, 39)]  // up across it
    [InlineData(75, 3)]   // from the last page to the first
    public async Task A_Move_Lands_Where_Asked_And_Renumbers_Only_Its_Range(int from, int to)
    {
        var (ownerId, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var (_, editor) = await _factory.CreateAuthenticatedClientAsync(EditorEmail);
        var albumId = await CreateAlbumAsync(owner, "Big");
        await SeedItemsAsync(albumId, ownerId, 81);
        await InviteAcceptAsync(owner, editor, albumId, EditorEmail, "editor");

        var before = await DbOrderAsync(albumId);
        var ordersBefore = await DbSortOrdersAsync(albumId);
        var version = await VersionAsync(editor, albumId);

        var response = await MoveAsync(editor, albumId, before[from], version, to);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(to, body.GetProperty("position").GetInt32());
        Assert.Equal(81, body.GetProperty("totalCount").GetInt32());
        Assert.Equal(version + 1, body.GetProperty("version").GetInt32());

        var expected = before.ToList();
        expected.RemoveAt(from);
        expected.Insert(to, before[from]);
        Assert.Equal(expected, (await WalkAsync(editor, albumId, PageSize)).Ids);

        // Still dense, and nothing outside the range between the two positions
        // was touched.
        var ordersAfter = await DbSortOrdersAsync(albumId);
        Assert.Equal(Enumerable.Range(1, 81), ordersAfter.Values.OrderBy(o => o));
        var (lo, hi) = (Math.Min(from, to), Math.Max(from, to));
        for (var i = 0; i < before.Count; i++)
        {
            if (i < lo || i > hi)
            {
                Assert.Equal(ordersBefore[before[i]], ordersAfter[before[i]]);
            }
        }
    }

    [Fact]
    public async Task Moving_To_The_End_Of_A_Large_Album_Needs_Only_The_Item_And_The_Position()
    {
        // "To the end" of 520 items from a list that has read one page: the
        // client never held the destination, and does not need to.
        var (ownerId, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var albumId = await CreateAlbumAsync(owner, "Big");
        await SeedItemsAsync(albumId, ownerId, 520);
        var first = await ReadPageAsync(await owner.GetAsync($"/api/albums/{albumId}/content?limit={PageSize}"));
        var moved = ItemId(first.Items[0]);

        var response = await MoveAsync(owner, albumId, moved, first.Version, first.TotalCount - 1);
        response.EnsureSuccessStatusCode();

        var order = await DbOrderAsync(albumId);
        Assert.Equal(moved, order[^1]);
        Assert.Equal(ItemId(first.Items[1]), order[0]);
    }

    [Fact]
    public async Task A_Stale_Move_Is_A_Conflict_And_Changes_Nothing()
    {
        var (ownerId, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var (_, editor) = await _factory.CreateAuthenticatedClientAsync(EditorEmail);
        var albumId = await CreateAlbumAsync(owner, "Big");
        await SeedItemsAsync(albumId, ownerId, 81);
        await InviteAcceptAsync(owner, editor, albumId, EditorEmail, "editor");
        var ids = await DbOrderAsync(albumId);
        var stale = await VersionAsync(editor, albumId);

        (await MoveAsync(owner, albumId, ids[5], stale, 60)).EnsureSuccessStatusCode();
        var afterWinner = await DbOrderAsync(albumId);
        var audits = await CountAuditAsync("album.edit_reorder");

        var conflict = await MoveAsync(editor, albumId, ids[20], stale, 0);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        var body = await conflict.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(stale + 1, body.GetProperty("version").GetInt32());

        // The losing writer wrote nothing and audited nothing.
        Assert.Equal(afterWinner, await DbOrderAsync(albumId));
        Assert.Equal(audits, await CountAuditAsync("album.edit_reorder"));
        Assert.Equal(stale + 1, await VersionAsync(editor, albumId));
    }

    [Fact]
    public async Task A_Move_Outside_The_Album_Or_Of_A_Foreign_Item_Is_Refused()
    {
        var (ownerId, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var albumId = await CreateAlbumAsync(owner, "Big");
        var otherAlbum = await CreateAlbumAsync(owner, "Other");
        await SeedItemsAsync(albumId, ownerId, 81);
        await SeedItemsAsync(otherAlbum, ownerId, 3);
        var ids = await DbOrderAsync(albumId);
        var version = await VersionAsync(owner, albumId);

        // Refused, not clamped: a destination nobody asked for is not a move.
        Assert.Equal(HttpStatusCode.BadRequest, (await MoveAsync(owner, albumId, ids[0], version, 81)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await MoveAsync(owner, albumId, ids[0], version, -1)).StatusCode);
        var missingTarget = await owner.PostAsJsonAsync(
            $"/api/shared-albums/{albumId}/items/{ids[0]}/move", new { expectedVersion = version });
        Assert.Equal(HttpStatusCode.BadRequest, missingTarget.StatusCode);
        var foreign = (await DbOrderAsync(otherAlbum))[0];
        Assert.Equal(HttpStatusCode.NotFound, (await MoveAsync(owner, albumId, foreign, version, 0)).StatusCode);

        // Every refusal left both the order and the version alone.
        Assert.Equal(ids, await DbOrderAsync(albumId));
        Assert.Equal(version, await VersionAsync(owner, albumId));
    }

    [Fact]
    public async Task Moving_An_Item_Onto_Its_Own_Position_Spends_No_Version()
    {
        var (ownerId, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var albumId = await CreateAlbumAsync(owner, "Small");
        await SeedItemsAsync(albumId, ownerId, 5);
        var ids = await DbOrderAsync(albumId);
        var version = await VersionAsync(owner, albumId);

        var response = await MoveAsync(owner, albumId, ids[2], version, 2);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(version, body.GetProperty("version").GetInt32());
        Assert.Equal(version, await VersionAsync(owner, albumId));
    }

    [Fact]
    public async Task An_Order_Numbered_From_Zero_Is_Renumbered_Once_And_Moves_Correctly()
    {
        // A detached copy numbers its items from zero. The first move on such an
        // album renumbers it in its visible order, then moves.
        var (ownerId, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var albumId = await CreateAlbumAsync(owner, "Copy");
        await SeedItemsAsync(albumId, ownerId, 81, sortOrder: i => i);
        var before = await DbOrderAsync(albumId);

        (await MoveAsync(owner, albumId, before[5], await VersionAsync(owner, albumId), 0))
            .EnsureSuccessStatusCode();

        var expected = before.ToList();
        expected.RemoveAt(5);
        expected.Insert(0, before[5]);
        Assert.Equal(expected, await DbOrderAsync(albumId));
        Assert.Equal(Enumerable.Range(1, 81), (await DbSortOrdersAsync(albumId)).Values.OrderBy(o => o));
    }

    [Fact]
    public async Task Only_A_Curator_May_Page_Or_Move()
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
        var ids = await DbOrderAsync(albumId);
        var version = await VersionAsync(owner, albumId);

        // The Owner and an Editor read and move through the same routes.
        foreach (var curator in new[] { owner, editor })
        {
            var page = await curator.GetAsync($"/api/albums/{albumId}/content?limit={PageSize}");
            Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        }
        (await MoveAsync(editor, albumId, ids[0], version, 1)).EnsureSuccessStatusCode();
        version += 1;

        // A member who may not curate reads nothing here and moves nothing.
        foreach (var member in new[] { contributor, viewer })
        {
            Assert.Equal(HttpStatusCode.NotFound,
                (await member.GetAsync($"/api/albums/{albumId}/content?limit={PageSize}")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden,
                (await MoveAsync(member, albumId, ids[2], version, 0)).StatusCode);
        }

        // A stranger cannot tell the album exists.
        Assert.Equal(HttpStatusCode.NotFound,
            (await stranger.GetAsync($"/api/albums/{albumId}/content?limit={PageSize}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await MoveAsync(stranger, albumId, ids[2], version, 0)).StatusCode);

        Assert.Equal(version, await VersionAsync(owner, albumId));
    }

    [Fact]
    public async Task An_Unavailable_Item_Stays_On_Its_Page_And_Can_Still_Be_Moved()
    {
        var (ownerId, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var albumId = await CreateAlbumAsync(owner, "Big");
        await SeedItemsAsync(albumId, ownerId, 81, unavailableAt: 50);
        var ids = await DbOrderAsync(albumId);

        var walk = await WalkAsync(owner, albumId, PageSize);
        var row = walk.Rows.Single(r => ItemId(r) == ids[50]);
        Assert.Equal("unavailable", row.GetProperty("sourceState").GetString());

        // Moderation still reaches it: moved to the front, it is the first row
        // of the first page.
        (await MoveAsync(owner, albumId, ids[50], walk.Version, 0)).EnsureSuccessStatusCode();
        var first = await ReadPageAsync(await owner.GetAsync($"/api/albums/{albumId}/content?limit={PageSize}"));
        Assert.Equal(ids[50], ItemId(first.Items[0]));
        Assert.Equal("unavailable", first.Items[0].GetProperty("sourceState").GetString());
    }

    [Fact]
    public async Task The_Cover_Survives_Moves_And_Is_Flagged_On_Whichever_Page_Holds_It()
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

        // Moving the cover item, and moving another item, leave the choice alone.
        (await MoveAsync(owner, albumId, ids[45], walk.Version, 0)).EnsureSuccessStatusCode();
        (await MoveAsync(owner, albumId, ids[3], walk.Version + 1, 80)).EnsureSuccessStatusCode();

        var first = await ReadPageAsync(await owner.GetAsync($"/api/albums/{albumId}/content?limit={PageSize}"));
        Assert.Equal(coverFile, first.CoverFileItemId);
        Assert.True(first.Items[0].GetProperty("isCover").GetBoolean());
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

    private static Task<HttpResponseMessage> MoveAsync(
        HttpClient client, Guid albumId, Guid albumItemId, int expectedVersion, int targetIndex) =>
        client.PostAsJsonAsync($"/api/shared-albums/{albumId}/items/{albumItemId}/move",
            new { expectedVersion, targetIndex });

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
        Guid albumId, Guid ownerId, int count, Func<int, int>? sortOrder = null, int unavailableAt = -1)
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
                EffectiveDateTaken = now,
                DeletedAt = i == unavailableAt ? now : null,
            });
            db.AlbumItems.Add(new AlbumItem
            {
                Id = Guid.NewGuid(),
                AlbumId = albumId,
                FileItemId = fileId,
                AddedAt = now.AddSeconds(i),
                AddedByUserId = ownerId,
                SortOrder = sortOrder?.Invoke(i) ?? i + 1,
            });
        }
        await db.SaveChangesAsync();
    }

    // The order every surface serves: SortOrder, then FileItemId.
    private async Task<List<Guid>> DbOrderAsync(Guid albumId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.AlbumItems.AsNoTracking()
            .Where(ai => ai.AlbumId == albumId)
            .OrderBy(ai => ai.SortOrder).ThenBy(ai => ai.FileItemId)
            .Select(ai => ai.Id)
            .ToListAsync();
    }

    private async Task<Dictionary<Guid, int>> DbSortOrdersAsync(Guid albumId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.AlbumItems.AsNoTracking()
            .Where(ai => ai.AlbumId == albumId)
            .ToDictionaryAsync(ai => ai.Id, ai => ai.SortOrder);
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

    private async Task<int> CountAuditAsync(string action)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.AuditLogs.CountAsync(a => a.Action == action);
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
