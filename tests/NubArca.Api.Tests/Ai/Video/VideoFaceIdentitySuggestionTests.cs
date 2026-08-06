using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Data;
using NubArca.Api.Domain.Ai;
using Xunit;
using static NubArca.Api.Tests.Ai.Video.VideoFacePeopleTestHarness;

namespace NubArca.Api.Tests.Ai.Video;

// VFACE-02: identity SUGGESTIONS for a canonical video face track.
//
// The contract being pinned is that a suggestion is advisory and owner-private:
// it is drawn only from the caller's own confirmed evidence, only within one
// embedding space, never persisted, and never able to override what the owner
// already decided.
public sealed class VideoFaceIdentitySuggestionTests
{
    private const string SuggestionsRoute = "/api/people/video-tracks/{0}/suggestions";

    private static Task<HttpResponseMessage> GetSuggestions(
        HttpClient client, Guid trackId, int? limit = null)
        => client.GetAsync(string.Format(SuggestionsRoute, trackId) + (limit is null ? "" : $"?limit={limit}"));

    // ---- the candidate pool -------------------------------------------------

    [Fact]
    public async Task A_Confirmed_Static_Face_Makes_Its_Person_A_Candidate()
    {
        using var f = Factory();
        var profileId = await SeedProfileAsync(f);
        var (ownerId, client) = await f.CreateAuthenticatedClientAsync();
        var video = await SeedVideoAsync(f, ownerId, profileId);
        var trackId = await AddTrackAsync(f, video.AnalysisId, OneHot(0));
        var alice = await CreatePersonAsync(f, ownerId, "Alice");
        await SeedConfirmedFaceAsync(f, ownerId, profileId, alice, OneHot(0));

        var page = await (await GetSuggestions(client, trackId))
            .Content.ReadFromJsonAsync<Suggestions>();

        var candidate = Assert.Single(page!.Items);
        Assert.Equal(alice, candidate.PersonId);
        Assert.Equal("Alice", candidate.Name);
        Assert.Equal(1.0, candidate.Similarity, 3);
        Assert.Equal(1, candidate.SupportingEvidenceCount);
    }

    [Fact]
    public async Task A_Confirmed_Video_Track_Also_Contributes_Evidence()
    {
        using var f = Factory();
        var profileId = await SeedProfileAsync(f);
        var (ownerId, client) = await f.CreateAuthenticatedClientAsync();
        var video = await SeedVideoAsync(f, ownerId, profileId);
        var confirmed = await AddTrackAsync(f, video.AnalysisId, OneHot(0), trackIndex: 0);
        var pending = await AddTrackAsync(f, video.AnalysisId, OneHot(0), trackIndex: 1);
        var alice = await CreatePersonAsync(f, ownerId, "Alice");

        // No static face at all: the ONLY evidence is the confirmed track.
        await client.PostAsJsonAsync(
            $"/api/people/video-tracks/{confirmed}/assign", new { personId = alice });

        var page = await (await GetSuggestions(client, pending))
            .Content.ReadFromJsonAsync<Suggestions>();

        var candidate = Assert.Single(page!.Items);
        Assert.Equal(alice, candidate.PersonId);
    }

    [Fact]
    public async Task A_Track_Never_Suggests_Itself()
    {
        using var f = Factory();
        var profileId = await SeedProfileAsync(f);
        var (ownerId, client) = await f.CreateAuthenticatedClientAsync();
        var video = await SeedVideoAsync(f, ownerId, profileId);
        var trackId = await AddTrackAsync(f, video.AnalysisId, OneHot(0));
        var alice = await CreatePersonAsync(f, ownerId, "Alice");
        await client.PostAsJsonAsync(
            $"/api/people/video-tracks/{trackId}/assign", new { personId = alice });

        var page = await (await GetSuggestions(client, trackId))
            .Content.ReadFromJsonAsync<Suggestions>();

        Assert.Empty(page!.Items);
    }

    [Fact]
    public async Task An_Ignored_Face_Contributes_Nothing()
    {
        using var f = Factory();
        var profileId = await SeedProfileAsync(f);
        var (ownerId, client) = await f.CreateAuthenticatedClientAsync();
        var video = await SeedVideoAsync(f, ownerId, profileId);
        var trackId = await AddTrackAsync(f, video.AnalysisId, OneHot(0));
        var alice = await CreatePersonAsync(f, ownerId, "Alice");
        await SeedConfirmedFaceAsync(f, ownerId, profileId, alice, OneHot(0), ignored: true);

        var page = await (await GetSuggestions(client, trackId))
            .Content.ReadFromJsonAsync<Suggestions>();

        Assert.Empty(page!.Items);
    }

    [Fact]
    public async Task An_Ignored_Track_Contributes_Nothing()
    {
        using var f = Factory();
        var profileId = await SeedProfileAsync(f);
        var (ownerId, client) = await f.CreateAuthenticatedClientAsync();
        var video = await SeedVideoAsync(f, ownerId, profileId);
        var dismissed = await AddTrackAsync(f, video.AnalysisId, OneHot(0), trackIndex: 0);
        var pending = await AddTrackAsync(f, video.AnalysisId, OneHot(0), trackIndex: 1);
        await CreatePersonAsync(f, ownerId, "Alice");
        await client.PostAsync($"/api/people/video-tracks/{dismissed}/ignore", null);

        var page = await (await GetSuggestions(client, pending))
            .Content.ReadFromJsonAsync<Suggestions>();

        Assert.Empty(page!.Items);
    }

    [Fact]
    public async Task An_Archived_Person_Is_Never_Suggested()
    {
        using var f = Factory();
        var profileId = await SeedProfileAsync(f);
        var (ownerId, client) = await f.CreateAuthenticatedClientAsync();
        var video = await SeedVideoAsync(f, ownerId, profileId);
        var trackId = await AddTrackAsync(f, video.AnalysisId, OneHot(0));
        var alice = await CreatePersonAsync(f, ownerId, "Alice");
        await SeedConfirmedFaceAsync(f, ownerId, profileId, alice, OneHot(0));

        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var person = await db.People.SingleAsync(p => p.Id == alice);
            person.IsArchived = true;
            await db.SaveChangesAsync();
        }

        var page = await (await GetSuggestions(client, trackId))
            .Content.ReadFromJsonAsync<Suggestions>();

        Assert.Empty(page!.Items);
    }

    // ---- cross-owner isolation ----------------------------------------------

    [Fact]
    public async Task Another_Owners_Confirmed_Identity_Is_Never_Used()
    {
        using var f = Factory();
        var profileId = await SeedProfileAsync(f);
        var (ownerA, clientA) = await f.CreateAuthenticatedClientAsync();
        var (ownerB, _) = await f.CreateAuthenticatedClientAsync("other@example.com");
        var video = await SeedVideoAsync(f, ownerA, profileId);
        // The very same deduplicated blob is in BOTH libraries.
        await AddFileReferenceAsync(f, ownerB, video.BlobId);
        var trackId = await AddTrackAsync(f, video.AnalysisId, OneHot(0));

        // Owner B has named this face; owner A has named nobody.
        var bobsPerson = await CreatePersonAsync(f, ownerB, "Known To B");
        await SeedConfirmedFaceAsync(f, ownerB, profileId, bobsPerson, OneHot(0));

        var page = await (await GetSuggestions(clientA, trackId))
            .Content.ReadFromJsonAsync<Suggestions>();

        Assert.Empty(page!.Items);
    }

    [Fact]
    public async Task Suggestions_For_An_Invisible_Track_Are_A_Generic_404()
    {
        using var f = Factory();
        var profileId = await SeedProfileAsync(f);
        var (ownerId, client) = await f.CreateAuthenticatedClientAsync();
        var vaultId = await CreateVaultAsync(f, ownerId);
        var video = await SeedVideoAsync(f, ownerId, profileId, vaultId: vaultId);
        var trackId = await AddTrackAsync(f, video.AnalysisId, OneHot(0));

        Assert.Equal(HttpStatusCode.NotFound, (await GetSuggestions(client, trackId)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await GetSuggestions(client, Guid.NewGuid())).StatusCode);
    }

    // ---- profile compatibility ----------------------------------------------

    [Fact]
    public async Task A_Track_From_Another_Model_Space_Yields_No_Candidates()
    {
        using var f = Factory();
        var profileId = await SeedProfileAsync(f);
        var (ownerId, client) = await f.CreateAuthenticatedClientAsync();
        var video = await SeedVideoAsync(f, ownerId, profileId);
        var trackId = await AddTrackAsync(f, video.AnalysisId, OneHot(0));
        var alice = await CreatePersonAsync(f, ownerId, "Alice");
        await SeedConfirmedFaceAsync(f, ownerId, profileId, alice, OneHot(0));

        // Retag the analysis as belonging to a DIFFERENT embedding profile: the
        // vectors are no longer comparable and nothing may be suggested.
        var otherProfileId = await SeedOtherFaceProfileAsync(f);
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var analysis = await db.VideoFaceAnalysisStatuses.SingleAsync(a => a.Id == video.AnalysisId);
            analysis.EmbeddingProfileId = otherProfileId;
            analysis.DetectionProfileId = otherProfileId;
            await db.SaveChangesAsync();
        }

        var page = await (await GetSuggestions(client, trackId))
            .Content.ReadFromJsonAsync<Suggestions>();

        Assert.Empty(page!.Items);
        Assert.Equal("profile-mismatch", page.UnavailableReason);
    }

    // ---- ranking + bounds ----------------------------------------------------

    [Fact]
    public async Task Candidates_Are_Ranked_Bounded_And_Deterministic()
    {
        using var f = Factory();
        var profileId = await SeedProfileAsync(f);
        var (ownerId, client) = await f.CreateAuthenticatedClientAsync();
        var video = await SeedVideoAsync(f, ownerId, profileId);
        var trackId = await AddTrackAsync(f, video.AnalysisId, OneHot(0));

        var exact = await CreatePersonAsync(f, ownerId, "Exact");
        await SeedConfirmedFaceAsync(f, ownerId, profileId, exact, OneHot(0));
        var close = await CreatePersonAsync(f, ownerId, "Close");
        await SeedConfirmedFaceAsync(f, ownerId, profileId, close, Tilted(0, 1, 0.5f));
        var closer = await CreatePersonAsync(f, ownerId, "Closer");
        await SeedConfirmedFaceAsync(f, ownerId, profileId, closer, Tilted(0, 2, 0.2f));

        var first = await (await GetSuggestions(client, trackId)).Content.ReadFromJsonAsync<Suggestions>();
        var second = await (await GetSuggestions(client, trackId)).Content.ReadFromJsonAsync<Suggestions>();

        Assert.Equal(new[] { exact, closer, close }, first!.Items.Select(i => i.PersonId));
        Assert.Equal(
            first.Items.Select(i => (i.PersonId, i.Similarity)),
            second!.Items.Select(i => (i.PersonId, i.Similarity)));

        var limited = await (await GetSuggestions(client, trackId, limit: 2))
            .Content.ReadFromJsonAsync<Suggestions>();
        Assert.Equal(2, limited!.Items.Count);
        Assert.Equal(exact, limited.Items[0].PersonId);
    }

    [Fact]
    public async Task A_Below_Threshold_Person_Is_Not_Suggested()
    {
        using var f = Factory();
        var profileId = await SeedProfileAsync(f);
        var (ownerId, client) = await f.CreateAuthenticatedClientAsync();
        var video = await SeedVideoAsync(f, ownerId, profileId);
        var trackId = await AddTrackAsync(f, video.AnalysisId, OneHot(0));
        var stranger = await CreatePersonAsync(f, ownerId, "Stranger");
        // Orthogonal: cosine 0, far below any sane candidate threshold.
        await SeedConfirmedFaceAsync(f, ownerId, profileId, stranger, OneHot(7));

        var page = await (await GetSuggestions(client, trackId))
            .Content.ReadFromJsonAsync<Suggestions>();

        Assert.Empty(page!.Items);
        Assert.Null(page.UnavailableReason);
        Assert.True(page.Threshold > 0);
    }

    [Fact]
    public async Task No_Evidence_At_All_Yields_An_Empty_List_Not_An_Error()
    {
        using var f = Factory();
        var profileId = await SeedProfileAsync(f);
        var (ownerId, client) = await f.CreateAuthenticatedClientAsync();
        var video = await SeedVideoAsync(f, ownerId, profileId);
        var trackId = await AddTrackAsync(f, video.AnalysisId, OneHot(0));

        var response = await GetSuggestions(client, trackId);
        var page = await response.Content.ReadFromJsonAsync<Suggestions>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(page!.Items);
    }

    // ---- suggestions are advisory --------------------------------------------

    [Fact]
    public async Task Asking_For_Suggestions_Never_Writes_A_Decision()
    {
        using var f = Factory();
        var profileId = await SeedProfileAsync(f);
        var (ownerId, client) = await f.CreateAuthenticatedClientAsync();
        var video = await SeedVideoAsync(f, ownerId, profileId);
        var trackId = await AddTrackAsync(f, video.AnalysisId, OneHot(0));
        var alice = await CreatePersonAsync(f, ownerId, "Alice");
        await SeedConfirmedFaceAsync(f, ownerId, profileId, alice, OneHot(0));

        // A perfect 1.0 match, asked for three times.
        for (var i = 0; i < 3; i++)
        {
            await GetSuggestions(client, trackId);
        }

        Assert.Equal(0, await DecisionCountAsync(f, trackId));
    }

    [Fact]
    public async Task An_Explicit_Decision_Is_Never_Overwritten_By_A_Stronger_Suggestion()
    {
        using var f = Factory();
        var profileId = await SeedProfileAsync(f);
        var (ownerId, client) = await f.CreateAuthenticatedClientAsync();
        var video = await SeedVideoAsync(f, ownerId, profileId);
        var trackId = await AddTrackAsync(f, video.AnalysisId, OneHot(0));

        // The model would say "Exact"; the owner says "Chosen".
        var exact = await CreatePersonAsync(f, ownerId, "Exact");
        await SeedConfirmedFaceAsync(f, ownerId, profileId, exact, OneHot(0));
        var chosen = await CreatePersonAsync(f, ownerId, "Chosen");

        await client.PostAsJsonAsync(
            $"/api/people/video-tracks/{trackId}/assign", new { personId = chosen });
        await GetSuggestions(client, trackId);

        var decision = await LoadDecisionAsync(f, ownerId, trackId);
        Assert.Equal(chosen, decision!.PersonId);
        Assert.Equal(VideoFaceTrackDecisionSources.User, decision.Source);
    }

    [Fact]
    public async Task No_Embedding_Or_Model_Internal_Is_Ever_Returned()
    {
        using var f = Factory();
        var profileId = await SeedProfileAsync(f);
        var (ownerId, client) = await f.CreateAuthenticatedClientAsync();
        var video = await SeedVideoAsync(f, ownerId, profileId);
        var trackId = await AddTrackAsync(f, video.AnalysisId, OneHot(0));
        var alice = await CreatePersonAsync(f, ownerId, "Alice");
        await SeedConfirmedFaceAsync(f, ownerId, profileId, alice, OneHot(0));

        var body = await (await GetSuggestions(client, trackId)).Content.ReadAsStringAsync();

        AssertNoLeak(body);
        Assert.DoesNotContain("det-face-embedding", body, StringComparison.Ordinal);
    }

    // ---- helpers -------------------------------------------------------------

    private static async Task<Guid> SeedOtherFaceProfileAsync(NubArca.Api.Tests.Endpoints.SqliteWebApplicationFactory f)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var model = new AiModel
        {
            Id = Guid.NewGuid(), Key = $"m-{Guid.NewGuid():N}", Provider = AiProviders.Deterministic,
            Capability = AiCapabilities.FaceEmbedding, Modality = AiModalities.Face,
            Dimension = Dim, DistanceMetric = "cosine", Enabled = true, CreatedAt = DateTime.UtcNow,
        };
        var profile = new AiProfile
        {
            Id = Guid.NewGuid(), Key = $"p-{Guid.NewGuid():N}", AiModelId = model.Id,
            Capability = AiCapabilities.FaceEmbedding, Modality = AiModalities.Face,
            Dimension = Dim, DistanceMetric = "cosine", Enabled = true, CreatedAt = DateTime.UtcNow,
        };
        db.AiModels.Add(model);
        db.AiProfiles.Add(profile);
        await db.SaveChangesAsync();
        return profile.Id;
    }

    private sealed record Suggestion(
        Guid PersonId, string? Name, double Similarity, int SupportingEvidenceCount);

    private sealed record Suggestions(
        double Threshold, IReadOnlyList<Suggestion> Items, string? UnavailableReason);
}
