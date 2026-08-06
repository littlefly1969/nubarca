using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Files;
using NubArca.Api.Media;
using NubArca.Api.Media.Semantic;
using NubArca.Api.Tests.Endpoints;
using Xunit;
using static NubArca.Api.Tests.Media.MediaSemanticTestHarness;

namespace NubArca.Api.Tests.Media;

// "Solo da organizzare" (AlbumMembershipFilter.Unassigned) on the SEMANTIC
// route. The ordinary gallery predicate has its own coverage in
// Files/AlbumMembershipFilterTests; what is new here is that album membership
// is a PHYSICAL filter, so it must shrink the candidate scope BEFORE ranking
// and must bind the ranking cache and the msv2 cursor.
//
// The distinction that matters: filtering an already-ranked page would still
// "work" visually while quietly returning fewer than a page of results and
// letting a cached ranking leak across filter states.
public sealed class UnassignedMediaFilterTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory = Factory();

    public void Dispose() => _factory.Dispose();

    private static ImageFilters Unassigned() =>
        new() { AlbumMembership = AlbumMembershipFilter.Unassigned };

    [Fact]
    public async Task Semantic_Search_Excludes_Media_Already_In_An_Active_Album()
    {
        var profile = await SeedProfileAsync(_factory);
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var q = QueryVector(profile);

        var (filedId, filedBlob) = await UploadPhotoAsync(_factory, owner, 40);
        await SeedPhotoEmbeddingAsync(_factory, profile, filedBlob, WithSimilarity(q, 0.99));
        var (looseId, looseBlob) = await UploadPhotoAsync(_factory, owner, 80);
        await SeedPhotoEmbeddingAsync(_factory, profile, looseBlob, WithSimilarity(q, 0.50));

        // Unfiltered, the filed photo is the better match and ranks first.
        var all = await SearchAsync(_factory, owner);
        Assert.Equal(2, all.Items.Count);

        await AddToAlbumAsync(client, "Vacanze", filedId);

        var filtered = await SearchAsync(_factory, owner, filters: Unassigned());

        // The stronger match is gone because it is already organised — and the
        // weaker, unfiled one survives.
        Assert.Single(filtered.Items);
        Assert.Equal(looseId, filtered.Items[0].Media.Id);
        Assert.Equal(1, filtered.Total);
    }

    [Fact]
    public async Task Media_In_Several_Albums_Is_Excluded_Once_Not_Duplicated()
    {
        var profile = await SeedProfileAsync(_factory);
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var q = QueryVector(profile);

        var (filedId, filedBlob) = await UploadPhotoAsync(_factory, owner, 40);
        await SeedPhotoEmbeddingAsync(_factory, profile, filedBlob, WithSimilarity(q, 0.9));
        var (looseId, looseBlob) = await UploadPhotoAsync(_factory, owner, 80);
        await SeedPhotoEmbeddingAsync(_factory, profile, looseBlob, WithSimilarity(q, 0.8));

        await AddToAlbumAsync(client, "Uno", filedId);
        await AddToAlbumAsync(client, "Due", filedId);

        var filtered = await SearchAsync(_factory, owner, filters: Unassigned());

        // A NOT EXISTS excludes once regardless of how many memberships exist;
        // a join would have produced duplicates or a wrong total.
        Assert.Single(filtered.Items);
        Assert.Equal(looseId, filtered.Items[0].Media.Id);
    }

    [Fact]
    public async Task Deleting_The_Album_Makes_Its_Media_Unassigned_Again()
    {
        var profile = await SeedProfileAsync(_factory);
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var q = QueryVector(profile);

        var (fileId, blob) = await UploadPhotoAsync(_factory, owner, 40);
        await SeedPhotoEmbeddingAsync(_factory, profile, blob, WithSimilarity(q, 0.9));
        var albumId = await AddToAlbumAsync(client, "Temporaneo", fileId);

        Assert.Empty((await SearchAsync(_factory, owner, filters: Unassigned())).Items);

        (await client.DeleteAsync($"/api/albums/{albumId}")).EnsureSuccessStatusCode();

        // Album deletion removes its album_items, so the media is genuinely
        // unfiled again — membership in a deleted album must not linger.
        var after = await SearchAsync(_factory, owner, filters: Unassigned());
        Assert.Single(after.Items);
        Assert.Equal(fileId, after.Items[0].Media.Id);
    }

    [Fact]
    public async Task Removing_The_Membership_Makes_The_Media_Unassigned_Again()
    {
        var profile = await SeedProfileAsync(_factory);
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var q = QueryVector(profile);

        var (fileId, blob) = await UploadPhotoAsync(_factory, owner, 40);
        await SeedPhotoEmbeddingAsync(_factory, profile, blob, WithSimilarity(q, 0.9));
        var albumId = await AddToAlbumAsync(client, "Ripensamento", fileId);

        Assert.Empty((await SearchAsync(_factory, owner, filters: Unassigned())).Items);

        (await client.DeleteAsync($"/api/albums/{albumId}/items/{fileId}"))
            .EnsureSuccessStatusCode();

        Assert.Single((await SearchAsync(_factory, owner, filters: Unassigned())).Items);
    }

    [Fact]
    public async Task Another_Owners_Album_Cannot_Hide_This_Owners_Media()
    {
        var profile = await SeedProfileAsync(_factory);
        var (owner, _) = await _factory.CreateAuthenticatedClientAsync("a@example.com");
        var (stranger, strangerClient) = await _factory.CreateAuthenticatedClientAsync("b@example.com");
        var q = QueryVector(profile);

        var (mineId, mineBlob) = await UploadPhotoAsync(_factory, owner, 40);
        await SeedPhotoEmbeddingAsync(_factory, profile, mineBlob, WithSimilarity(q, 0.9));

        // The stranger files THEIR OWN media. Nothing about that may touch this
        // owner's view.
        var (theirsId, theirsBlob) = await UploadPhotoAsync(_factory, stranger, 90);
        await SeedPhotoEmbeddingAsync(_factory, profile, theirsBlob, WithSimilarity(q, 0.95));
        await AddToAlbumAsync(strangerClient, "Loro", theirsId);

        var filtered = await SearchAsync(_factory, owner, filters: Unassigned());

        Assert.Single(filtered.Items);
        Assert.Equal(mineId, filtered.Items[0].Media.Id);
    }

    [Fact]
    public async Task Videos_Are_Filtered_By_The_Same_Rule_As_Photos()
    {
        var profile = await SeedProfileAsync(_factory);
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var q = QueryVector(profile);

        var (filedVideo, filedBlob) = await UploadVideoAsync(_factory, owner);
        await SeedVideoManifestAsync(_factory, profile, filedBlob, q,
            [new SeedSample(0, 8_000, 4_000, 0.95)]);
        var (looseVideo, looseBlob) = await UploadVideoAsync(_factory, owner);
        await SeedVideoManifestAsync(_factory, profile, looseBlob, q,
            [new SeedSample(0, 8_000, 4_000, 0.85)]);

        await AddToAlbumAsync(client, "Clip organizzate", filedVideo);

        var filtered = await SearchAsync(
            _factory, owner, kind: MediaKindScope.Video, filters: Unassigned());

        Assert.Single(filtered.Items);
        Assert.Equal(looseVideo, filtered.Items[0].Media.Id);
        // The surviving video keeps its temporal evidence.
        Assert.Equal(4_000, filtered.Items[0].BestMatch.RepresentativeMilliseconds);
    }

    [Fact]
    public async Task An_Assigned_Post_Cutoff_Candidate_Is_Filtered_Before_Ranking()
    {
        // The two SEARCH-SEM-01 guarantees meeting: full coverage means a
        // candidate past the former 20,000 boundary is reachable, and album
        // membership is physical, so an ASSIGNED one is removed from the
        // candidate scope rather than ranked and then hidden.
        var profile = await SeedProfileAsync(_factory);
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var q = QueryVector(profile);

        var (strongId, strongBlob) = await UploadPhotoAsync(_factory, owner, 40);
        await SeedPhotoEmbeddingAsync(_factory, profile, strongBlob, WithSimilarity(q, 0.99));
        var (weakId, weakBlob) = await UploadPhotoAsync(_factory, owner, 80);
        await SeedPhotoEmbeddingAsync(_factory, profile, weakBlob, WithSimilarity(q, 0.30));

        // Unfiltered the strong one wins.
        Assert.Equal(strongId, (await SearchAsync(_factory, owner)).Items[0].Media.Id);

        await AddToAlbumAsync(client, "Gia organizzate", strongId);

        var filtered = await SearchAsync(_factory, owner, filters: Unassigned());
        Assert.Single(filtered.Items);
        Assert.Equal(weakId, filtered.Items[0].Media.Id);
        // Filtered BEFORE ranking: the total is the filtered candidate count,
        // not "ranked then trimmed".
        Assert.Equal(1, filtered.Total);
    }

    [Fact]
    public async Task The_Ranking_Cache_Cannot_Serve_One_Filter_State_To_The_Other()
    {
        var profile = await SeedProfileAsync(_factory);
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var q = QueryVector(profile);

        var (filedId, filedBlob) = await UploadPhotoAsync(_factory, owner, 40);
        await SeedPhotoEmbeddingAsync(_factory, profile, filedBlob, WithSimilarity(q, 0.9));
        var (_, looseBlob) = await UploadPhotoAsync(_factory, owner, 80);
        await SeedPhotoEmbeddingAsync(_factory, profile, looseBlob, WithSimilarity(q, 0.8));
        await AddToAlbumAsync(client, "Organizzate", filedId);

        // Warm the cache with the filter OFF…
        var unfiltered = await SearchAsync(_factory, owner);
        Assert.Equal(2, unfiltered.Total);

        // …then ask with it ON. The filter is part of the ImageFilters
        // fingerprint, so this is a different ranking identity and must not be
        // served the cached two-result list.
        var filtered = await SearchAsync(_factory, owner, filters: Unassigned());
        Assert.Equal(1, filtered.Total);
    }

    [Fact]
    public async Task A_Cursor_Issued_Without_The_Filter_Is_Rejected_With_It()
    {
        var profile = await SeedProfileAsync(_factory);
        var (owner, _) = await _factory.CreateAuthenticatedClientAsync();
        var q = QueryVector(profile);

        for (byte i = 0; i < 6; i++)
        {
            var (_, blob) = await UploadPhotoAsync(_factory, owner, (byte)(40 + i));
            await SeedPhotoEmbeddingAsync(_factory, profile, blob, WithSimilarity(q, 0.9 - (i * 0.05)));
        }

        var first = await SearchAsync(_factory, owner, limit: 2);
        Assert.NotNull(first.NextCursor);

        // The msv2 cursor binds the whole filter fingerprint, so replaying it
        // under a different filter state fails loudly instead of paging a
        // ranking that no longer exists.
        await Assert.ThrowsAsync<NubArca.Api.Ai.Photos.SemanticSearchCursorException>(
            () => SearchAsync(_factory, owner, limit: 2,
                cursor: first.NextCursor, filters: Unassigned()));
    }

    [Fact]
    public async Task Omitting_The_Filter_Preserves_Existing_Results_Exactly()
    {
        var profile = await SeedProfileAsync(_factory);
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var q = QueryVector(profile);

        var (filedId, filedBlob) = await UploadPhotoAsync(_factory, owner, 40);
        await SeedPhotoEmbeddingAsync(_factory, profile, filedBlob, WithSimilarity(q, 0.9));
        var (_, looseBlob) = await UploadPhotoAsync(_factory, owner, 80);
        await SeedPhotoEmbeddingAsync(_factory, profile, looseBlob, WithSimilarity(q, 0.8));
        await AddToAlbumAsync(client, "Organizzate", filedId);

        // Default (Any) behaviour is untouched — organised media still appears.
        var page = await SearchAsync(_factory, owner);
        Assert.Equal(2, page.Total);
        Assert.Contains(page.Items, i => i.Media.Id == filedId);
    }

    // ---- helpers -----------------------------------------------------------

    private static async Task<Guid> AddToAlbumAsync(
        HttpClient client, string albumName, Guid fileItemId)
    {
        var created = await client.PostAsJsonAsync("/api/albums", new { name = albumName });
        created.EnsureSuccessStatusCode();
        var albumId = (await created.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>())
            .GetProperty("id").GetGuid();
        (await client.PostAsJsonAsync($"/api/albums/{albumId}/items", new { fileItemId }))
            .EnsureSuccessStatusCode();
        return albumId;
    }
}
