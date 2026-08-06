using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Ai.Faces.Video;
using NubArca.Api.Data;
using NubArca.Api.Domain.Ai;
using Xunit;
using static NubArca.Api.Tests.Ai.Video.VideoFacePeopleTestHarness;

namespace NubArca.Api.Tests.Ai.Video;

// VFACE-02C: co-presence semantics.
//
// Co-presence means two of the owner's people were confirmed on tracks that
// genuinely OVERLAP IN TIME inside one canonical analysis. The predicate is
// strict half-open overlap of [Start, End):
//
//     A.Start < B.End && B.Start < A.End
//
// It is deliberately CONFIGURATION-FREE. An earlier version widened each interval
// by one sampling interval, which meant retuning
// Ai:VideoFaceAnalysis:FrameIntervalMilliseconds silently changed the answer to a
// question about already-persisted, byte-identical evidence. These tests pin both
// the predicate and that stability.
public sealed class VideoFaceCoPresenceTests
{
    // ---- the predicate, directly --------------------------------------------

    private static bool Overlaps(long aStart, long aEnd, long bStart, long bEnd)
        => VideoFaceTrackPeopleService.Overlaps(
            new ConfirmedTrackRow(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), aStart, aEnd, aStart),
            new ConfirmedTrackRow(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), bStart, bEnd, bStart));

    [Fact]
    public void Adjacent_Intervals_Are_Not_Co_Present()
    {
        // The spec's canonical example: [0, 1000) and [1000, 2000) touch but never
        // coexist, because End is exclusive.
        Assert.False(Overlaps(0, 1_000, 1_000, 2_000));
        Assert.False(Overlaps(1_000, 2_000, 0, 1_000));
    }

    [Fact]
    public void An_Overlap_Of_One_Millisecond_Is_Co_Present()
    {
        Assert.True(Overlaps(0, 1_001, 1_000, 2_000));
        Assert.True(Overlaps(1_000, 2_000, 0, 1_001));
    }

    [Fact]
    public void Separated_Intervals_Are_Not_Co_Present()
    {
        Assert.False(Overlaps(0, 900, 1_000, 2_000));
        Assert.False(Overlaps(1_000, 2_000, 0, 900));
    }

    [Fact]
    public void Complete_Containment_Is_Co_Present()
    {
        Assert.True(Overlaps(0, 10_000, 4_000, 5_000));
        Assert.True(Overlaps(4_000, 5_000, 0, 10_000));
    }

    [Fact]
    public void Identical_Intervals_Are_Co_Present()
    {
        Assert.True(Overlaps(3_000, 7_000, 3_000, 7_000));
    }

    [Fact]
    public void A_Degenerate_Zero_Length_Interval_Reads_As_A_Contained_Instant()
    {
        // Documenting the predicate honestly rather than wishfully. Read as sets,
        // [x, x) is empty and "should" overlap nothing; but the contract is
        // exactly `A.Start < B.End && B.Start < A.End`, which treats a zero-length
        // interval as an instant and reports containment. No special case is added
        // for it, because none is needed:
        //
        // VFACE-01 cannot produce such a track. A track needs at least
        // MinimumTrackDetections accepted detections, the tracker takes at most ONE
        // detection per sampled frame, and Start/End are the first and last
        // detection timestamps — so a real track always spans >= 2 distinct
        // timestamps and End > Start strictly.
        Assert.True(Overlaps(5_000, 5_000, 0, 10_000));
        Assert.True(Overlaps(0, 10_000, 5_000, 5_000));

        // Outside the other interval it is correctly not co-present.
        Assert.False(Overlaps(20_000, 20_000, 0, 10_000));
    }

    [Fact]
    public void The_Predicate_Is_Symmetric()
    {
        var cases = new (long AStart, long AEnd, long BStart, long BEnd)[]
        {
            (0, 1_000, 1_000, 2_000),
            (0, 1_001, 1_000, 2_000),
            (0, 900, 1_000, 2_000),
            (0, 10_000, 4_000, 5_000),
            (3_000, 7_000, 3_000, 7_000),
        };

        foreach (var (aStart, aEnd, bStart, bEnd) in cases)
        {
            Assert.Equal(
                Overlaps(aStart, aEnd, bStart, bEnd),
                Overlaps(bStart, bEnd, aStart, aEnd));
        }
    }

    // ---- stability across configuration -------------------------------------

    [Theory]
    [InlineData(1)]
    [InlineData(1_000)]
    [InlineData(5_000)]
    [InlineData(60_000)]
    public async Task Changing_The_Sampling_Interval_Does_Not_Change_The_Answer(int frameIntervalMs)
    {
        // Adjacent tracks: 400 ms apart, which the OLD tolerance-based rule would
        // have called co-present at a 1000 ms sampling interval and not co-present
        // at 1 ms. Now the answer is the same at every setting, because it is a
        // function of the persisted evidence alone.
        using var f = FactoryWithFrameInterval(frameIntervalMs);
        var profileId = await SeedProfileAsync(f);
        var (ownerId, client) = await f.CreateAuthenticatedClientAsync();
        var video = await SeedVideoAsync(f, ownerId, profileId);
        var alice = await CreatePersonAsync(f, ownerId, "Alice");
        var bob = await CreatePersonAsync(f, ownerId, "Bob");

        var aliceTrack = await AddTrackAsync(f, video.AnalysisId, OneHot(0), 1_000, 5_000, trackIndex: 0);
        var bobTrack = await AddTrackAsync(f, video.AnalysisId, OneHot(1), 5_400, 9_000, trackIndex: 1);
        await client.PostAsJsonAsync($"/api/people/video-tracks/{aliceTrack}/assign", new { personId = alice });
        await client.PostAsJsonAsync($"/api/people/video-tracks/{bobTrack}/assign", new { personId = bob });

        var videos = await client.GetFromJsonAsync<List<PersonVideo>>(
            $"/api/people/{alice}/co-present/{bob}");

        Assert.Empty(videos!);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(60_000)]
    public async Task A_Genuine_Overlap_Is_Co_Present_At_Every_Sampling_Interval(int frameIntervalMs)
    {
        using var f = FactoryWithFrameInterval(frameIntervalMs);
        var profileId = await SeedProfileAsync(f);
        var (ownerId, client) = await f.CreateAuthenticatedClientAsync();
        var video = await SeedVideoAsync(f, ownerId, profileId);
        var alice = await CreatePersonAsync(f, ownerId, "Alice");
        var bob = await CreatePersonAsync(f, ownerId, "Bob");

        var aliceTrack = await AddTrackAsync(f, video.AnalysisId, OneHot(0), 5_000, 15_000, trackIndex: 0);
        var bobTrack = await AddTrackAsync(f, video.AnalysisId, OneHot(1), 10_000, 20_000, trackIndex: 1);
        await client.PostAsJsonAsync($"/api/people/video-tracks/{aliceTrack}/assign", new { personId = alice });
        await client.PostAsJsonAsync($"/api/people/video-tracks/{bobTrack}/assign", new { personId = bob });

        var videos = await client.GetFromJsonAsync<List<PersonVideo>>(
            $"/api/people/{alice}/co-present/{bob}");

        Assert.Single(videos!);
    }

    // ---- scoping to one canonical analysis ----------------------------------

    [Fact]
    public async Task Tracks_From_Different_Analyses_Of_One_Blob_Are_Not_Compared()
    {
        using var f = Factory();
        var profileId = await SeedProfileAsync(f);
        var (ownerId, client) = await f.CreateAuthenticatedClientAsync();
        var video = await SeedVideoAsync(f, ownerId, profileId);
        var alice = await CreatePersonAsync(f, ownerId, "Alice");
        var bob = await CreatePersonAsync(f, ownerId, "Bob");

        // A SECOND analysis of the same manifest at a different version. Identical
        // overlapping intervals, but they describe two separate analyses, so they
        // are not comparable evidence.
        var secondAnalysisId = await AddAnalysisVersionAsync(f, video.IndexId, profileId, version: 2);

        var aliceTrack = await AddTrackAsync(f, video.AnalysisId, OneHot(0), 5_000, 15_000);
        var bobTrack = await AddTrackAsync(f, secondAnalysisId, OneHot(1), 5_000, 15_000);
        await client.PostAsJsonAsync($"/api/people/video-tracks/{aliceTrack}/assign", new { personId = alice });
        await client.PostAsJsonAsync($"/api/people/video-tracks/{bobTrack}/assign", new { personId = bob });

        var videos = await client.GetFromJsonAsync<List<PersonVideo>>(
            $"/api/people/{alice}/co-present/{bob}");

        Assert.Empty(videos!);
    }

    [Fact]
    public async Task Tracks_From_Different_Profile_Analyses_Are_Not_Compared()
    {
        using var f = Factory();
        var profileId = await SeedProfileAsync(f);
        var (ownerId, client) = await f.CreateAuthenticatedClientAsync();
        var video = await SeedVideoAsync(f, ownerId, profileId);
        var alice = await CreatePersonAsync(f, ownerId, "Alice");
        var bob = await CreatePersonAsync(f, ownerId, "Bob");

        var otherProfileId = await AddFaceProfileAsync(f);
        var otherAnalysisId = await AddAnalysisVersionAsync(
            f, video.IndexId, otherProfileId, version: 1);

        var aliceTrack = await AddTrackAsync(f, video.AnalysisId, OneHot(0), 5_000, 15_000);
        var bobTrack = await AddTrackAsync(f, otherAnalysisId, OneHot(1), 5_000, 15_000);
        await client.PostAsJsonAsync($"/api/people/video-tracks/{aliceTrack}/assign", new { personId = alice });
        await client.PostAsJsonAsync($"/api/people/video-tracks/{bobTrack}/assign", new { personId = bob });

        var videos = await client.GetFromJsonAsync<List<PersonVideo>>(
            $"/api/people/{alice}/co-present/{bob}");

        Assert.Empty(videos!);
    }

    [Fact]
    public async Task Tracks_From_Different_Videos_Are_Not_Compared()
    {
        using var f = Factory();
        var profileId = await SeedProfileAsync(f);
        var (ownerId, client) = await f.CreateAuthenticatedClientAsync();
        var first = await SeedVideoAsync(f, ownerId, profileId);
        var second = await SeedVideoAsync(f, ownerId, profileId);
        var alice = await CreatePersonAsync(f, ownerId, "Alice");
        var bob = await CreatePersonAsync(f, ownerId, "Bob");

        var aliceTrack = await AddTrackAsync(f, first.AnalysisId, OneHot(0), 5_000, 15_000);
        var bobTrack = await AddTrackAsync(f, second.AnalysisId, OneHot(1), 5_000, 15_000);
        await client.PostAsJsonAsync($"/api/people/video-tracks/{aliceTrack}/assign", new { personId = alice });
        await client.PostAsJsonAsync($"/api/people/video-tracks/{bobTrack}/assign", new { personId = bob });

        var videos = await client.GetFromJsonAsync<List<PersonVideo>>(
            $"/api/people/{alice}/co-present/{bob}");

        Assert.Empty(videos!);
    }

    [Fact]
    public async Task Non_Overlapping_Tracks_In_One_Video_Are_Not_Co_Present()
    {
        using var f = Factory();
        var profileId = await SeedProfileAsync(f);
        var (ownerId, client) = await f.CreateAuthenticatedClientAsync();
        var video = await SeedVideoAsync(f, ownerId, profileId);
        var alice = await CreatePersonAsync(f, ownerId, "Alice");
        var bob = await CreatePersonAsync(f, ownerId, "Bob");

        var aliceTrack = await AddTrackAsync(f, video.AnalysisId, OneHot(0), 1_000, 5_000, trackIndex: 0);
        var bobTrack = await AddTrackAsync(f, video.AnalysisId, OneHot(1), 60_000, 70_000, trackIndex: 1);
        await client.PostAsJsonAsync($"/api/people/video-tracks/{aliceTrack}/assign", new { personId = alice });
        await client.PostAsJsonAsync($"/api/people/video-tracks/{bobTrack}/assign", new { personId = bob });

        var videos = await client.GetFromJsonAsync<List<PersonVideo>>(
            $"/api/people/{alice}/co-present/{bob}");

        Assert.Empty(videos!);
    }

    [Fact]
    public async Task Containment_In_One_Video_Is_Co_Present()
    {
        using var f = Factory();
        var profileId = await SeedProfileAsync(f);
        var (ownerId, client) = await f.CreateAuthenticatedClientAsync();
        var video = await SeedVideoAsync(f, ownerId, profileId);
        var alice = await CreatePersonAsync(f, ownerId, "Alice");
        var bob = await CreatePersonAsync(f, ownerId, "Bob");

        var aliceTrack = await AddTrackAsync(f, video.AnalysisId, OneHot(0), 0, 30_000, trackIndex: 0);
        var bobTrack = await AddTrackAsync(f, video.AnalysisId, OneHot(1), 10_000, 12_000, trackIndex: 1);
        await client.PostAsJsonAsync($"/api/people/video-tracks/{aliceTrack}/assign", new { personId = alice });
        await client.PostAsJsonAsync($"/api/people/video-tracks/{bobTrack}/assign", new { personId = bob });

        var videos = await client.GetFromJsonAsync<List<PersonVideo>>(
            $"/api/people/{alice}/co-present/{bob}");

        var item = Assert.Single(videos!);
        Assert.Equal(video.FileId, item.FileItemId);
        // The interval returned is the QUERIED person's own evidence.
        Assert.Equal(0, item.BestMatch.StartMilliseconds);
        Assert.Equal(30_000, item.BestMatch.EndMilliseconds);
    }

    // ---- decision state -----------------------------------------------------

    [Fact]
    public async Task Undecided_And_Ignored_Tracks_Are_Excluded()
    {
        using var f = Factory();
        var profileId = await SeedProfileAsync(f);
        var (ownerId, client) = await f.CreateAuthenticatedClientAsync();
        var video = await SeedVideoAsync(f, ownerId, profileId);
        var alice = await CreatePersonAsync(f, ownerId, "Alice");
        var bob = await CreatePersonAsync(f, ownerId, "Bob");

        var aliceTrack = await AddTrackAsync(f, video.AnalysisId, OneHot(0), 5_000, 15_000, trackIndex: 0);
        var bobTrack = await AddTrackAsync(f, video.AnalysisId, OneHot(1), 10_000, 20_000, trackIndex: 1);
        await client.PostAsJsonAsync($"/api/people/video-tracks/{aliceTrack}/assign", new { personId = alice });

        // Bob's overlapping track is UNDECIDED.
        Assert.Empty((await client.GetFromJsonAsync<List<PersonVideo>>(
            $"/api/people/{alice}/co-present/{bob}"))!);

        // …and then IGNORED. Still excluded.
        await client.PostAsync($"/api/people/video-tracks/{bobTrack}/ignore", null);
        Assert.Empty((await client.GetFromJsonAsync<List<PersonVideo>>(
            $"/api/people/{alice}/co-present/{bob}"))!);

        // Confirming it is what makes them co-present.
        await client.PostAsJsonAsync($"/api/people/video-tracks/{bobTrack}/assign", new { personId = bob });
        Assert.Single((await client.GetFromJsonAsync<List<PersonVideo>>(
            $"/api/people/{alice}/co-present/{bob}"))!);
    }

    [Fact]
    public async Task Clearing_A_Decision_Removes_The_Co_Presence()
    {
        using var f = Factory();
        var profileId = await SeedProfileAsync(f);
        var (ownerId, client) = await f.CreateAuthenticatedClientAsync();
        var video = await SeedVideoAsync(f, ownerId, profileId);
        var alice = await CreatePersonAsync(f, ownerId, "Alice");
        var bob = await CreatePersonAsync(f, ownerId, "Bob");

        var aliceTrack = await AddTrackAsync(f, video.AnalysisId, OneHot(0), 5_000, 15_000, trackIndex: 0);
        var bobTrack = await AddTrackAsync(f, video.AnalysisId, OneHot(1), 10_000, 20_000, trackIndex: 1);
        await client.PostAsJsonAsync($"/api/people/video-tracks/{aliceTrack}/assign", new { personId = alice });
        await client.PostAsJsonAsync($"/api/people/video-tracks/{bobTrack}/assign", new { personId = bob });
        Assert.Single((await client.GetFromJsonAsync<List<PersonVideo>>(
            $"/api/people/{alice}/co-present/{bob}"))!);

        await client.DeleteAsync($"/api/people/video-tracks/{bobTrack}/decision");

        Assert.Empty((await client.GetFromJsonAsync<List<PersonVideo>>(
            $"/api/people/{alice}/co-present/{bob}"))!);
    }

    // ---- ownership + visibility ---------------------------------------------

    [Fact]
    public async Task Co_Presence_Never_Crosses_Owners()
    {
        using var f = Factory();
        var profileId = await SeedProfileAsync(f);
        var (ownerA, clientA) = await f.CreateAuthenticatedClientAsync();
        var (ownerB, clientB) = await f.CreateAuthenticatedClientAsync("other@example.com");
        var video = await SeedVideoAsync(f, ownerA, profileId);
        await AddFileReferenceAsync(f, ownerB, video.BlobId);   // the same blob
        var alice = await CreatePersonAsync(f, ownerA, "Alice");
        var foreignBob = await CreatePersonAsync(f, ownerB, "Bob");

        var aliceTrack = await AddTrackAsync(f, video.AnalysisId, OneHot(0), 5_000, 15_000, trackIndex: 0);
        var bobTrack = await AddTrackAsync(f, video.AnalysisId, OneHot(1), 10_000, 20_000, trackIndex: 1);
        await clientA.PostAsJsonAsync($"/api/people/video-tracks/{aliceTrack}/assign", new { personId = alice });
        // Owner B confirms the overlapping track for THEIR person.
        await clientB.PostAsJsonAsync($"/api/people/video-tracks/{bobTrack}/assign", new { personId = foreignBob });

        // A cannot even name B's person.
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await clientA.GetAsync($"/api/people/{alice}/co-present/{foreignBob}")).StatusCode);
    }

    [Fact]
    public async Task A_Vault_Only_Video_Yields_No_Co_Presence()
    {
        using var f = Factory();
        var profileId = await SeedProfileAsync(f);
        var (ownerId, client) = await f.CreateAuthenticatedClientAsync();
        var video = await SeedVideoAsync(f, ownerId, profileId);
        var alice = await CreatePersonAsync(f, ownerId, "Alice");
        var bob = await CreatePersonAsync(f, ownerId, "Bob");

        var aliceTrack = await AddTrackAsync(f, video.AnalysisId, OneHot(0), 5_000, 15_000, trackIndex: 0);
        var bobTrack = await AddTrackAsync(f, video.AnalysisId, OneHot(1), 10_000, 20_000, trackIndex: 1);
        await client.PostAsJsonAsync($"/api/people/video-tracks/{aliceTrack}/assign", new { personId = alice });
        await client.PostAsJsonAsync($"/api/people/video-tracks/{bobTrack}/assign", new { personId = bob });

        // The only visible reference is then vaulted: the decisions survive, the
        // media stops surfacing.
        var vaultId = await CreateVaultAsync(f, ownerId);
        await MoveToVaultAsync(f, video.FileId, vaultId);

        Assert.Empty((await client.GetFromJsonAsync<List<PersonVideo>>(
            $"/api/people/{alice}/co-present/{bob}"))!);
    }

    [Fact]
    public async Task The_Same_Person_Twice_Is_Rejected()
    {
        using var f = Factory();
        await SeedProfileAsync(f);
        var (ownerId, client) = await f.CreateAuthenticatedClientAsync();
        var alice = await CreatePersonAsync(f, ownerId, "Alice");

        Assert.Equal(
            HttpStatusCode.NotFound,
            (await client.GetAsync($"/api/people/{alice}/co-present/{alice}")).StatusCode);
    }

    [Fact]
    public async Task An_Archived_Person_Yields_A_Generic_404()
    {
        using var f = Factory();
        var profileId = await SeedProfileAsync(f);
        var (ownerId, client) = await f.CreateAuthenticatedClientAsync();
        var alice = await CreatePersonAsync(f, ownerId, "Alice");
        var bob = await CreatePersonAsync(f, ownerId, "Bob");

        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            (await db.People.SingleAsync(p => p.Id == bob)).IsArchived = true;
            await db.SaveChangesAsync();
        }

        Assert.Equal(
            HttpStatusCode.NotFound,
            (await client.GetAsync($"/api/people/{alice}/co-present/{bob}")).StatusCode);
        Assert.NotEqual(Guid.Empty, profileId);
    }

    [Fact]
    public async Task The_Response_Exposes_No_Internal_Identifier()
    {
        using var f = Factory();
        var profileId = await SeedProfileAsync(f);
        var (ownerId, client) = await f.CreateAuthenticatedClientAsync();
        var video = await SeedVideoAsync(f, ownerId, profileId);
        var alice = await CreatePersonAsync(f, ownerId, "Alice");
        var bob = await CreatePersonAsync(f, ownerId, "Bob");

        var aliceTrack = await AddTrackAsync(f, video.AnalysisId, OneHot(0), 5_000, 15_000, trackIndex: 0);
        var bobTrack = await AddTrackAsync(f, video.AnalysisId, OneHot(1), 10_000, 20_000, trackIndex: 1);
        await client.PostAsJsonAsync($"/api/people/video-tracks/{aliceTrack}/assign", new { personId = alice });
        await client.PostAsJsonAsync($"/api/people/video-tracks/{bobTrack}/assign", new { personId = bob });

        var body = await (await client.GetAsync($"/api/people/{alice}/co-present/{bob}"))
            .Content.ReadAsStringAsync();

        AssertNoLeak(body);
        Assert.DoesNotContain(aliceTrack.ToString(), body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(bobTrack.ToString(), body, StringComparison.OrdinalIgnoreCase);
    }

    private sealed record PersonVideoMatch(
        string EvidenceType,
        long StartMilliseconds,
        long EndMilliseconds,
        long RepresentativeMilliseconds);

    private sealed record PersonVideo(
        Guid FileItemId,
        string Name,
        PersonVideoMatch BestMatch,
        IReadOnlyList<PersonVideoMatch> AdditionalMatches);
}
