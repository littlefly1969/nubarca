using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Data;
using NubArca.Api.Domain.Ai;
using Xunit;
using static NubArca.Api.Tests.Ai.Video.VideoFacePeopleTestHarness;

namespace NubArca.Api.Tests.Ai.Video;

// VFACE-02: the owner-level decision surface over canonical video face tracks —
// assign / ignore / clear, the review queue, and the ownership and privacy
// boundaries that make them safe.
//
// Everything runs through the real HTTP endpoints, so authentication, owner
// scoping and DTO sanitization are exercised exactly as a client would hit them.
public sealed class VideoFaceTrackPersonDecisionTests
{
    // ---- assign ------------------------------------------------------------

    [Fact]
    public async Task Assign_Records_An_Owner_Decision_Without_Touching_The_Track()
    {
        using var f = Factory();
        var profileId = await SeedProfileAsync(f);
        var (ownerId, client) = await f.CreateAuthenticatedClientAsync();
        var video = await SeedVideoAsync(f, ownerId, profileId);
        var trackId = await AddTrackAsync(f, video.AnalysisId, OneHot(0));
        var personId = await CreatePersonAsync(f, ownerId, "Alice");

        var response = await client.PostAsJsonAsync(
            $"/api/people/video-tracks/{trackId}/assign", new { personId });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var decision = await LoadDecisionAsync(f, ownerId, trackId);
        Assert.Equal(VideoFaceTrackDecisions.Assigned, decision!.Decision);
        Assert.Equal(personId, decision.PersonId);
        Assert.Equal(VideoFaceTrackDecisionSources.User, decision.Source);
        Assert.NotNull(decision.ConfirmedAt);

        // The canonical evidence is untouched — no PersonId lives on it and none
        // ever will.
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var track = await db.VideoFaceTracks.AsNoTracking().SingleAsync(t => t.Id == trackId);
        Assert.Equal(4, track.DetectionCount);
        Assert.Null(track.RepresentativeCropBlobObjectId);
    }

    [Fact]
    public async Task Assign_Is_Idempotent_And_Preserves_The_Original_Confirmation()
    {
        using var f = Factory();
        var profileId = await SeedProfileAsync(f);
        var (ownerId, client) = await f.CreateAuthenticatedClientAsync();
        var video = await SeedVideoAsync(f, ownerId, profileId);
        var trackId = await AddTrackAsync(f, video.AnalysisId, OneHot(0));
        var personId = await CreatePersonAsync(f, ownerId, "Alice");

        await client.PostAsJsonAsync($"/api/people/video-tracks/{trackId}/assign", new { personId });
        var first = await LoadDecisionAsync(f, ownerId, trackId);

        await client.PostAsJsonAsync($"/api/people/video-tracks/{trackId}/assign", new { personId });
        var second = await LoadDecisionAsync(f, ownerId, trackId);

        Assert.Equal(1, await DecisionCountAsync(f, trackId));
        Assert.Equal(first!.Id, second!.Id);
        Assert.Equal(first.ConfirmedAt, second.ConfirmedAt);
    }

    [Fact]
    public async Task Assign_Moves_The_Track_To_Another_Person()
    {
        using var f = Factory();
        var profileId = await SeedProfileAsync(f);
        var (ownerId, client) = await f.CreateAuthenticatedClientAsync();
        var video = await SeedVideoAsync(f, ownerId, profileId);
        var trackId = await AddTrackAsync(f, video.AnalysisId, OneHot(0));
        var alice = await CreatePersonAsync(f, ownerId, "Alice");
        var bob = await CreatePersonAsync(f, ownerId, "Bob");

        await client.PostAsJsonAsync($"/api/people/video-tracks/{trackId}/assign", new { personId = alice });
        await client.PostAsJsonAsync($"/api/people/video-tracks/{trackId}/assign", new { personId = bob });

        Assert.Equal(1, await DecisionCountAsync(f, trackId));
        Assert.Equal(bob, (await LoadDecisionAsync(f, ownerId, trackId))!.PersonId);
    }

    [Fact]
    public async Task Assign_Replaces_A_Previous_Ignore()
    {
        using var f = Factory();
        var profileId = await SeedProfileAsync(f);
        var (ownerId, client) = await f.CreateAuthenticatedClientAsync();
        var video = await SeedVideoAsync(f, ownerId, profileId);
        var trackId = await AddTrackAsync(f, video.AnalysisId, OneHot(0));
        var personId = await CreatePersonAsync(f, ownerId, "Alice");

        await client.PostAsync($"/api/people/video-tracks/{trackId}/ignore", null);
        await client.PostAsJsonAsync($"/api/people/video-tracks/{trackId}/assign", new { personId });

        var decision = await LoadDecisionAsync(f, ownerId, trackId);
        Assert.Equal(VideoFaceTrackDecisions.Assigned, decision!.Decision);
        Assert.Equal(personId, decision.PersonId);
        Assert.Equal(1, await DecisionCountAsync(f, trackId));
    }

    [Fact]
    public async Task Assign_Without_A_Person_Is_A_Bad_Request()
    {
        using var f = Factory();
        var profileId = await SeedProfileAsync(f);
        var (ownerId, client) = await f.CreateAuthenticatedClientAsync();
        var video = await SeedVideoAsync(f, ownerId, profileId);
        var trackId = await AddTrackAsync(f, video.AnalysisId, OneHot(0));

        var response = await client.PostAsJsonAsync(
            $"/api/people/video-tracks/{trackId}/assign", new { personId = Guid.Empty });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await DecisionCountAsync(f, trackId));
    }

    // ---- ignore / clear ----------------------------------------------------

    [Fact]
    public async Task Ignore_Stores_A_Person_Free_Decision_And_Keeps_The_Track()
    {
        using var f = Factory();
        var profileId = await SeedProfileAsync(f);
        var (ownerId, client) = await f.CreateAuthenticatedClientAsync();
        var video = await SeedVideoAsync(f, ownerId, profileId);
        var trackId = await AddTrackAsync(f, video.AnalysisId, OneHot(0));

        var response = await client.PostAsync($"/api/people/video-tracks/{trackId}/ignore", null);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var decision = await LoadDecisionAsync(f, ownerId, trackId);
        Assert.Equal(VideoFaceTrackDecisions.Ignored, decision!.Decision);
        Assert.Null(decision.PersonId);
        Assert.Null(decision.ConfirmedAt);

        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.True(await db.VideoFaceTracks.AnyAsync(t => t.Id == trackId));
    }

    [Fact]
    public async Task Ignore_Replaces_A_Previous_Assignment()
    {
        using var f = Factory();
        var profileId = await SeedProfileAsync(f);
        var (ownerId, client) = await f.CreateAuthenticatedClientAsync();
        var video = await SeedVideoAsync(f, ownerId, profileId);
        var trackId = await AddTrackAsync(f, video.AnalysisId, OneHot(0));
        var personId = await CreatePersonAsync(f, ownerId, "Alice");

        await client.PostAsJsonAsync($"/api/people/video-tracks/{trackId}/assign", new { personId });
        await client.PostAsync($"/api/people/video-tracks/{trackId}/ignore", null);

        var decision = await LoadDecisionAsync(f, ownerId, trackId);
        Assert.Equal(VideoFaceTrackDecisions.Ignored, decision!.Decision);
        Assert.Null(decision.PersonId);
        Assert.Equal(1, await DecisionCountAsync(f, trackId));
    }

    [Fact]
    public async Task Clear_Returns_The_Track_To_Undecided_Without_Reassigning()
    {
        using var f = Factory();
        var profileId = await SeedProfileAsync(f);
        var (ownerId, client) = await f.CreateAuthenticatedClientAsync();
        var video = await SeedVideoAsync(f, ownerId, profileId);
        var trackId = await AddTrackAsync(f, video.AnalysisId, OneHot(0));
        var personId = await CreatePersonAsync(f, ownerId, "Alice");
        await client.PostAsJsonAsync($"/api/people/video-tracks/{trackId}/assign", new { personId });

        var response = await client.DeleteAsync($"/api/people/video-tracks/{trackId}/decision");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(0, await DecisionCountAsync(f, trackId));

        // Back in the review queue, and nothing was silently re-decided.
        var queue = await client.GetFromJsonAsync<ReviewPage>("/api/people/video-tracks/undecided");
        Assert.Contains(queue!.Items, i => i.TrackId == trackId);
    }

    [Fact]
    public async Task Clear_On_An_Undecided_Track_Is_Idempotent()
    {
        using var f = Factory();
        var profileId = await SeedProfileAsync(f);
        var (ownerId, client) = await f.CreateAuthenticatedClientAsync();
        var video = await SeedVideoAsync(f, ownerId, profileId);
        var trackId = await AddTrackAsync(f, video.AnalysisId, OneHot(0));

        var first = await client.DeleteAsync($"/api/people/video-tracks/{trackId}/decision");
        var second = await client.DeleteAsync($"/api/people/video-tracks/{trackId}/decision");

        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);
    }

    // ---- ownership + privacy ------------------------------------------------

    [Fact]
    public async Task Two_Owners_Sharing_A_Blob_Decide_Independently()
    {
        using var f = Factory();
        var profileId = await SeedProfileAsync(f);
        var (ownerA, clientA) = await f.CreateAuthenticatedClientAsync();
        var (ownerB, clientB) = await f.CreateAuthenticatedClientAsync("other@example.com");
        var video = await SeedVideoAsync(f, ownerA, profileId);
        await AddFileReferenceAsync(f, ownerB, video.BlobId);   // dedup: same blob
        var trackId = await AddTrackAsync(f, video.AnalysisId, OneHot(0));

        var alice = await CreatePersonAsync(f, ownerA, "Alice");
        await clientA.PostAsJsonAsync($"/api/people/video-tracks/{trackId}/assign", new { personId = alice });
        await clientB.PostAsync($"/api/people/video-tracks/{trackId}/ignore", null);

        var decisionA = await LoadDecisionAsync(f, ownerA, trackId);
        var decisionB = await LoadDecisionAsync(f, ownerB, trackId);
        Assert.Equal(VideoFaceTrackDecisions.Assigned, decisionA!.Decision);
        Assert.Equal(alice, decisionA.PersonId);
        Assert.Equal(VideoFaceTrackDecisions.Ignored, decisionB!.Decision);
        Assert.Equal(2, await DecisionCountAsync(f, trackId));
    }

    [Fact]
    public async Task Duplicate_Files_Of_One_Owner_Share_A_Single_Decision()
    {
        using var f = Factory();
        var profileId = await SeedProfileAsync(f);
        var (ownerId, client) = await f.CreateAuthenticatedClientAsync();
        var video = await SeedVideoAsync(f, ownerId, profileId);
        await AddFileReferenceAsync(f, ownerId, video.BlobId);   // same owner, 2nd copy
        var trackId = await AddTrackAsync(f, video.AnalysisId, OneHot(0));
        var personId = await CreatePersonAsync(f, ownerId, "Alice");

        await client.PostAsJsonAsync($"/api/people/video-tracks/{trackId}/assign", new { personId });

        Assert.Equal(1, await DecisionCountAsync(f, trackId));
    }

    [Fact]
    public async Task A_Foreign_Owners_Track_Is_A_Generic_404()
    {
        using var f = Factory();
        var profileId = await SeedProfileAsync(f);
        var (ownerA, _) = await f.CreateAuthenticatedClientAsync();
        var (ownerB, clientB) = await f.CreateAuthenticatedClientAsync("other@example.com");
        var video = await SeedVideoAsync(f, ownerA, profileId);
        var trackId = await AddTrackAsync(f, video.AnalysisId, OneHot(0));
        var personB = await CreatePersonAsync(f, ownerB, "Bob");

        var assign = await clientB.PostAsJsonAsync(
            $"/api/people/video-tracks/{trackId}/assign", new { personId = personB });
        var ignore = await clientB.PostAsync($"/api/people/video-tracks/{trackId}/ignore", null);
        var clear = await clientB.DeleteAsync($"/api/people/video-tracks/{trackId}/decision");
        var suggestions = await clientB.GetAsync($"/api/people/video-tracks/{trackId}/suggestions");

        Assert.Equal(HttpStatusCode.NotFound, assign.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, ignore.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, clear.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, suggestions.StatusCode);
        Assert.Equal(0, await DecisionCountAsync(f, trackId));
    }

    [Fact]
    public async Task Another_Owners_Person_Cannot_Be_Assigned()
    {
        using var f = Factory();
        var profileId = await SeedProfileAsync(f);
        var (ownerA, clientA) = await f.CreateAuthenticatedClientAsync();
        var (ownerB, _) = await f.CreateAuthenticatedClientAsync("other@example.com");
        var video = await SeedVideoAsync(f, ownerA, profileId);
        var trackId = await AddTrackAsync(f, video.AnalysisId, OneHot(0));
        var foreignPerson = await CreatePersonAsync(f, ownerB, "Bob");

        var response = await clientA.PostAsJsonAsync(
            $"/api/people/video-tracks/{trackId}/assign", new { personId = foreignPerson });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, await DecisionCountAsync(f, trackId));
    }

    [Fact]
    public async Task A_Vault_Only_Reference_Hides_The_Track_Entirely()
    {
        using var f = Factory();
        var profileId = await SeedProfileAsync(f);
        var (ownerId, client) = await f.CreateAuthenticatedClientAsync();
        var vaultId = await CreateVaultAsync(f, ownerId);
        var video = await SeedVideoAsync(f, ownerId, profileId, vaultId: vaultId);
        var trackId = await AddTrackAsync(f, video.AnalysisId, OneHot(0));
        var personId = await CreatePersonAsync(f, ownerId, "Alice");

        var assign = await client.PostAsJsonAsync(
            $"/api/people/video-tracks/{trackId}/assign", new { personId });
        var queue = await client.GetFromJsonAsync<ReviewPage>("/api/people/video-tracks/undecided");

        Assert.Equal(HttpStatusCode.NotFound, assign.StatusCode);
        Assert.Empty(queue!.Items);
    }

    [Fact]
    public async Task A_Mixed_Vault_And_Normal_Reference_Stays_Visible()
    {
        using var f = Factory();
        var profileId = await SeedProfileAsync(f);
        var (ownerId, client) = await f.CreateAuthenticatedClientAsync();
        var vaultId = await CreateVaultAsync(f, ownerId);
        var video = await SeedVideoAsync(f, ownerId, profileId);
        await AddFileReferenceAsync(f, ownerId, video.BlobId, vaultId);
        var trackId = await AddTrackAsync(f, video.AnalysisId, OneHot(0));
        var personId = await CreatePersonAsync(f, ownerId, "Alice");

        var response = await client.PostAsJsonAsync(
            $"/api/people/video-tracks/{trackId}/assign", new { personId });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task A_Deleted_Reference_Hides_The_Track()
    {
        using var f = Factory();
        var profileId = await SeedProfileAsync(f);
        var (ownerId, client) = await f.CreateAuthenticatedClientAsync();
        var video = await SeedVideoAsync(f, ownerId, profileId);
        var trackId = await AddTrackAsync(f, video.AnalysisId, OneHot(0));
        var personId = await CreatePersonAsync(f, ownerId, "Alice");
        await DeleteFileAsync(f, video.FileId);

        var response = await client.PostAsJsonAsync(
            $"/api/people/video-tracks/{trackId}/assign", new { personId });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Every_Video_Track_Endpoint_Requires_Authentication()
    {
        using var f = Factory();
        var anonymous = f.CreateClient();
        var trackId = Guid.NewGuid();

        foreach (var response in new[]
        {
            await anonymous.GetAsync("/api/people/video-tracks/undecided"),
            await anonymous.GetAsync($"/api/people/video-tracks/{trackId}/suggestions"),
            await anonymous.PostAsync($"/api/people/video-tracks/{trackId}/ignore", null),
            await anonymous.DeleteAsync($"/api/people/video-tracks/{trackId}/decision"),
            await anonymous.GetAsync($"/api/people/{Guid.NewGuid()}/videos"),
        })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    // ---- review queue -------------------------------------------------------

    [Fact]
    public async Task The_Queue_Lists_Only_Undecided_Tracks_And_Is_Sanitized()
    {
        using var f = Factory();
        var profileId = await SeedProfileAsync(f);
        var (ownerId, client) = await f.CreateAuthenticatedClientAsync();
        var video = await SeedVideoAsync(f, ownerId, profileId);
        var undecided = await AddTrackAsync(f, video.AnalysisId, OneHot(0), 1_000, 9_000, trackIndex: 0);
        var assigned = await AddTrackAsync(f, video.AnalysisId, OneHot(1), 2_000, 4_000, trackIndex: 1);
        var ignored = await AddTrackAsync(f, video.AnalysisId, OneHot(2), 3_000, 5_000, trackIndex: 2);
        var personId = await CreatePersonAsync(f, ownerId, "Alice");
        await client.PostAsJsonAsync($"/api/people/video-tracks/{assigned}/assign", new { personId });
        await client.PostAsync($"/api/people/video-tracks/{ignored}/ignore", null);

        var response = await client.GetAsync("/api/people/video-tracks/undecided");
        var body = await response.Content.ReadAsStringAsync();
        var page = await client.GetFromJsonAsync<ReviewPage>("/api/people/video-tracks/undecided");

        var item = Assert.Single(page!.Items);
        Assert.Equal(undecided, item.TrackId);
        Assert.Equal(video.FileId, item.FileItemId);
        Assert.Equal(1_000, item.StartMilliseconds);
        Assert.Equal(9_000, item.EndMilliseconds);
        Assert.InRange(item.RepresentativeMilliseconds, item.StartMilliseconds, item.EndMilliseconds);
        AssertNoLeak(body);
    }

    [Fact]
    public async Task The_Queue_Puts_The_Strongest_Evidence_First()
    {
        using var f = Factory();
        var profileId = await SeedProfileAsync(f);
        var (ownerId, client) = await f.CreateAuthenticatedClientAsync();
        var video = await SeedVideoAsync(f, ownerId, profileId);
        var brief = await AddTrackAsync(f, video.AnalysisId, OneHot(0), 1_000, 2_000, trackIndex: 0);
        var long_ = await AddTrackAsync(f, video.AnalysisId, OneHot(1), 5_000, 40_000, trackIndex: 1);

        var page = await client.GetFromJsonAsync<ReviewPage>("/api/people/video-tracks/undecided");

        Assert.Equal(new[] { long_, brief }, page!.Items.Select(i => i.TrackId));
    }

    [Fact]
    public async Task The_Queue_Respects_Its_Limit()
    {
        using var f = Factory();
        var profileId = await SeedProfileAsync(f);
        var (ownerId, client) = await f.CreateAuthenticatedClientAsync();
        var video = await SeedVideoAsync(f, ownerId, profileId);
        for (var i = 0; i < 4; i++)
        {
            await AddTrackAsync(f, video.AnalysisId, OneHot(i), 1_000, 2_000 + i, trackIndex: i);
        }

        var page = await client.GetFromJsonAsync<ReviewPage>("/api/people/video-tracks/undecided?limit=2");

        Assert.Equal(2, page!.Items.Count);
        Assert.True(page.HasMore);
    }

    [Fact]
    public async Task One_Owners_Queue_Never_Shows_Another_Owners_Video()
    {
        using var f = Factory();
        var profileId = await SeedProfileAsync(f);
        var (ownerA, _) = await f.CreateAuthenticatedClientAsync();
        var (_, clientB) = await f.CreateAuthenticatedClientAsync("other@example.com");
        var video = await SeedVideoAsync(f, ownerA, profileId);
        await AddTrackAsync(f, video.AnalysisId, OneHot(0));

        var page = await clientB.GetFromJsonAsync<ReviewPage>("/api/people/video-tracks/undecided");

        Assert.Empty(page!.Items);
    }

    // ---- response shapes ----------------------------------------------------

    private sealed record ReviewItem(
        Guid TrackId, Guid FileItemId, string Name,
        long StartMilliseconds, long EndMilliseconds, long RepresentativeMilliseconds,
        int DetectionCount, double QualityScore);

    private sealed record ReviewPage(IReadOnlyList<ReviewItem> Items, bool HasMore);
}
