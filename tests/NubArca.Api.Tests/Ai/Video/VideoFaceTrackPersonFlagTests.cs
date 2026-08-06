using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NubArca.Api.Ai;
using NubArca.Api.Ai.Backends;
using NubArca.Api.Ai.Jobs;
using NubArca.Api.Ai.Resolution;
using NubArca.Api.Ai.Video;
using NubArca.Api.Ai.Video.Faces;
using NubArca.Api.Data;
using NubArca.Api.Domain.Ai;
using NubArca.Api.Jobs;
using Xunit;
using static NubArca.Api.Tests.Ai.Video.VideoFacePeopleTestHarness;

namespace NubArca.Api.Tests.Ai.Video;

// VFACE-02C: Ai:VideoFaceAnalysis:Enabled governs GENERATION, not readability.
//
// The flag is an operator's brake on new processing — post-segmentation
// scheduling and backfill execution. It is emphatically NOT a kill switch for
// People data: an owner who has already named faces in their videos must not see
// that work vanish because an administrator paused analysis, and they must still
// be able to correct a decision.
//
// These tests pin BOTH halves of that contract: nothing new is generated, and
// everything already persisted stays readable and decidable.
public sealed class VideoFaceTrackPersonFlagTests
{
    // ---- persisted data stays readable while generation is off --------------

    [Fact]
    public async Task The_Review_Queue_Still_Returns_Visible_Tracks()
    {
        using var f = FactoryWithAnalysisDisabled();
        var profileId = await SeedProfileAsync(f);
        var (ownerId, client) = await f.CreateAuthenticatedClientAsync();
        var video = await SeedVideoAsync(f, ownerId, profileId);
        var trackId = await AddTrackAsync(f, video.AnalysisId, OneHot(0), 1_000, 9_000);

        var page = await client.GetFromJsonAsync<ReviewPage>("/api/people/video-tracks/undecided");

        var item = Assert.Single(page!.Items);
        Assert.Equal(trackId, item.TrackId);
        Assert.Equal(video.FileId, item.FileItemId);
    }

    [Fact]
    public async Task An_Existing_Assignment_Is_Still_Returned()
    {
        // Assigned while enabled, read back while disabled.
        using var f = FactoryWithAnalysisDisabled();
        var profileId = await SeedProfileAsync(f);
        var (ownerId, client) = await f.CreateAuthenticatedClientAsync();
        var video = await SeedVideoAsync(f, ownerId, profileId);
        var trackId = await AddTrackAsync(f, video.AnalysisId, OneHot(0), 4_000, 9_000);
        var alice = await CreatePersonAsync(f, ownerId, "Alice");
        await client.PostAsJsonAsync(
            $"/api/people/video-tracks/{trackId}/assign", new { personId = alice });

        var decision = await LoadDecisionAsync(f, ownerId, trackId);
        Assert.Equal(VideoFaceTrackDecisions.Assigned, decision!.Decision);
        Assert.Equal(alice, decision.PersonId);
    }

    [Fact]
    public async Task Person_Video_Results_Are_Still_Returned()
    {
        using var f = FactoryWithAnalysisDisabled();
        var profileId = await SeedProfileAsync(f);
        var (ownerId, client) = await f.CreateAuthenticatedClientAsync();
        var video = await SeedVideoAsync(f, ownerId, profileId);
        var trackId = await AddTrackAsync(f, video.AnalysisId, OneHot(0), 4_000, 9_000);
        var alice = await CreatePersonAsync(f, ownerId, "Alice");
        await client.PostAsJsonAsync(
            $"/api/people/video-tracks/{trackId}/assign", new { personId = alice });

        var videos = await client.GetFromJsonAsync<List<PersonVideo>>($"/api/people/{alice}/videos");

        var item = Assert.Single(videos!);
        Assert.Equal(video.FileId, item.FileItemId);
        Assert.Equal(4_000, item.BestMatch.StartMilliseconds);
    }

    [Fact]
    public async Task Co_Presence_Is_Still_Returned()
    {
        using var f = FactoryWithAnalysisDisabled();
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

    // ---- decisions remain fully available -----------------------------------

    [Fact]
    public async Task Assign_Ignore_And_Clear_All_Still_Work()
    {
        using var f = FactoryWithAnalysisDisabled();
        var profileId = await SeedProfileAsync(f);
        var (ownerId, client) = await f.CreateAuthenticatedClientAsync();
        var video = await SeedVideoAsync(f, ownerId, profileId);
        var trackId = await AddTrackAsync(f, video.AnalysisId, OneHot(0));
        var alice = await CreatePersonAsync(f, ownerId, "Alice");
        var bob = await CreatePersonAsync(f, ownerId, "Bob");

        var assign = await client.PostAsJsonAsync(
            $"/api/people/video-tracks/{trackId}/assign", new { personId = alice });
        Assert.Equal(HttpStatusCode.NoContent, assign.StatusCode);
        Assert.Equal(alice, (await LoadDecisionAsync(f, ownerId, trackId))!.PersonId);

        // Changing one's mind must keep working while generation is paused.
        var reassign = await client.PostAsJsonAsync(
            $"/api/people/video-tracks/{trackId}/assign", new { personId = bob });
        Assert.Equal(HttpStatusCode.NoContent, reassign.StatusCode);
        Assert.Equal(bob, (await LoadDecisionAsync(f, ownerId, trackId))!.PersonId);

        var ignore = await client.PostAsync($"/api/people/video-tracks/{trackId}/ignore", null);
        Assert.Equal(HttpStatusCode.NoContent, ignore.StatusCode);
        Assert.Equal(
            VideoFaceTrackDecisions.Ignored,
            (await LoadDecisionAsync(f, ownerId, trackId))!.Decision);

        var clear = await client.DeleteAsync($"/api/people/video-tracks/{trackId}/decision");
        Assert.Equal(HttpStatusCode.NoContent, clear.StatusCode);
        Assert.Equal(0, await DecisionCountAsync(f, trackId));
    }

    [Fact]
    public async Task Suggestions_Use_Persisted_Evidence_Only()
    {
        using var f = FactoryWithAnalysisDisabled();
        var profileId = await SeedProfileAsync(f);
        var (ownerId, client) = await f.CreateAuthenticatedClientAsync();
        var video = await SeedVideoAsync(f, ownerId, profileId);
        var trackId = await AddTrackAsync(f, video.AnalysisId, OneHot(0));
        var alice = await CreatePersonAsync(f, ownerId, "Alice");
        await SeedConfirmedFaceAsync(f, ownerId, profileId, alice, OneHot(0));

        var response = await client.GetAsync($"/api/people/video-tracks/{trackId}/suggestions");
        var page = await response.Content.ReadFromJsonAsync<Suggestions>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var candidate = Assert.Single(page!.Items);
        Assert.Equal(alice, candidate.PersonId);
        // Advisory only: still no decision was written.
        Assert.Equal(0, await DecisionCountAsync(f, trackId));
    }

    // ---- privacy is unchanged by the flag -----------------------------------

    [Fact]
    public async Task Disabling_Generation_Does_Not_Weaken_Visibility_Checks()
    {
        using var f = FactoryWithAnalysisDisabled();
        var profileId = await SeedProfileAsync(f);
        var (ownerA, clientA) = await f.CreateAuthenticatedClientAsync();
        var (ownerB, clientB) = await f.CreateAuthenticatedClientAsync("other@example.com");

        // A vault-only video of owner A, and a normal one.
        var vaultId = await CreateVaultAsync(f, ownerA);
        var vaulted = await SeedVideoAsync(f, ownerA, profileId, vaultId: vaultId);
        var vaultedTrack = await AddTrackAsync(f, vaulted.AnalysisId, OneHot(0));
        var normal = await SeedVideoAsync(f, ownerA, profileId);
        var normalTrack = await AddTrackAsync(f, normal.AnalysisId, OneHot(1));

        var personA = await CreatePersonAsync(f, ownerA, "Alice");
        var personB = await CreatePersonAsync(f, ownerB, "Bob");

        // Vault-only exposes nothing, even with generation off.
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await clientA.PostAsJsonAsync(
                $"/api/people/video-tracks/{vaultedTrack}/assign", new { personId = personA })).StatusCode);

        // Owner B cannot reach owner A's track.
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await clientB.PostAsJsonAsync(
                $"/api/people/video-tracks/{normalTrack}/assign", new { personId = personB })).StatusCode);

        // Owner A cannot name owner B's person.
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await clientA.PostAsJsonAsync(
                $"/api/people/video-tracks/{normalTrack}/assign", new { personId = personB })).StatusCode);

        // A shared blob still yields independent decisions.
        await AddFileReferenceAsync(f, ownerB, normal.BlobId);
        await clientA.PostAsJsonAsync(
            $"/api/people/video-tracks/{normalTrack}/assign", new { personId = personA });
        await clientB.PostAsync($"/api/people/video-tracks/{normalTrack}/ignore", null);
        Assert.Equal(2, await DecisionCountAsync(f, normalTrack));
    }

    [Fact]
    public async Task No_Embedding_Or_Storage_Internal_Leaks_While_Disabled()
    {
        using var f = FactoryWithAnalysisDisabled();
        var profileId = await SeedProfileAsync(f);
        var (ownerId, client) = await f.CreateAuthenticatedClientAsync();
        var video = await SeedVideoAsync(f, ownerId, profileId);
        var trackId = await AddTrackAsync(f, video.AnalysisId, OneHot(0));
        var alice = await CreatePersonAsync(f, ownerId, "Alice");
        await SeedConfirmedFaceAsync(f, ownerId, profileId, alice, OneHot(0));
        await client.PostAsJsonAsync(
            $"/api/people/video-tracks/{trackId}/assign", new { personId = alice });

        foreach (var route in new[]
        {
            "/api/people/video-tracks/undecided",
            $"/api/people/video-tracks/{trackId}/suggestions",
            $"/api/people/{alice}/videos",
        })
        {
            AssertNoLeak(await (await client.GetAsync(route)).Content.ReadAsStringAsync());
        }
    }

    // ---- generation really is off -------------------------------------------

    [Fact]
    public async Task Segmentation_Completion_Schedules_Nothing_While_Disabled()
    {
        var queue = new RecordingJobQueue();
        using var f = FactoryWithAnalysisDisabled();
        var profileId = await SeedProfileAsync(f);

        using var scope = f.Services.CreateScope();
        var scheduler = new VideoFaceAnalysisScheduler(
            queue,
            scope.ServiceProvider.GetRequiredService<IAiProfileRegistry>(),
            Options.Create(new AiOptions { Enabled = true, FaceProfileKey = FaceProfileKey }),
            scope.ServiceProvider.GetRequiredService<IOptions<VideoFaceAnalysisOptions>>(),
            NullLogger<VideoFaceAnalysisScheduler>.Instance);

        Assert.False(await scheduler.TryScheduleForBlobAsync(Guid.NewGuid(), 1));
        Assert.Empty(queue.Enqueued);
        Assert.NotEqual(Guid.Empty, profileId);
    }

    [Fact]
    public async Task Segmentation_Completion_Does_Schedule_While_Enabled()
    {
        // The mirror case: the gate really is the flag and nothing else.
        var queue = new RecordingJobQueue();
        using var f = FactoryWithAnalysisEnabled();
        await SeedProfileAsync(f);

        using var scope = f.Services.CreateScope();
        var scheduler = new VideoFaceAnalysisScheduler(
            queue,
            scope.ServiceProvider.GetRequiredService<IAiProfileRegistry>(),
            Options.Create(new AiOptions { Enabled = true, FaceProfileKey = FaceProfileKey }),
            scope.ServiceProvider.GetRequiredService<IOptions<VideoFaceAnalysisOptions>>(),
            NullLogger<VideoFaceAnalysisScheduler>.Instance);

        Assert.True(await scheduler.TryScheduleForBlobAsync(Guid.NewGuid(), 1));
        Assert.Single(queue.Enqueued);
    }

    [Fact]
    public async Task A_Real_Backfill_Runs_No_Inference_And_No_Ffmpeg_While_Disabled()
    {
        using var f = FactoryWithAnalysisDisabled();
        var profileId = await SeedProfileAsync(f);
        var (ownerId, _) = await f.CreateAuthenticatedClientAsync();
        var video = await SeedVideoAsync(f, ownerId, profileId);

        using var scope = f.Services.CreateScope();
        var extractor = new CountingFrameExtractor();
        var backend = new CountingFaceBackend();
        var handler = new AiVideosFacesBackfillJobHandler(
            Options.Create(new AiOptions { Enabled = true, FaceProfileKey = FaceProfileKey }),
            scope.ServiceProvider.GetRequiredService<IOptions<VideoFaceAnalysisOptions>>(),
            new StubResolver(backend, profileId, scope.ServiceProvider),
            scope.ServiceProvider.GetRequiredService<IAiProfileRegistry>(),
            new NoopDiagnostics(),
            new VideoFaceAnalysisBackfillService(
                scope.ServiceProvider.GetRequiredService<AppDbContext>(),
                new VideoFaceAnalysisService(
                    scope.ServiceProvider.GetRequiredService<AppDbContext>(),
                    scope.ServiceProvider.GetRequiredService<NubArca.Api.Storage.IBlobService>(),
                    extractor,
                    scope.ServiceProvider.GetRequiredService<IAiVectorSerializer>(),
                    scope.ServiceProvider.GetRequiredService<IOptions<VideoFaceAnalysisOptions>>(),
                    TimeProvider.System,
                    NullLogger<VideoFaceAnalysisService>.Instance),
                scope.ServiceProvider.GetRequiredService<IOptions<VideoSemanticSegmentationOptions>>(),
                scope.ServiceProvider.GetRequiredService<IOptions<VideoFaceAnalysisOptions>>()));

        var context = new JobContext(
            Guid.NewGuid(), JsonSerializer.Serialize(new VideoFaceAnalysisJobPayload()),
            _ => { }, CancellationToken.None, (_, _, _, _) => Task.CompletedTask,
            TimeProvider.System, JobScheduling.Compute, null,
            sliceNumber: 0, sliceDeadline: null, sliceItemBudget: null);

        await handler.ExecuteAsync(context, CancellationToken.None);

        // No frame extraction, no detection, no recognition, no continuation.
        Assert.Equal(0, extractor.Runs);
        Assert.Equal(0, backend.DetectCalls);
        Assert.Equal(0, backend.EmbedCalls);
        Assert.False(context.ContinuationRequested);

        // Nothing was produced either. The seeded analysis row is left exactly as
        // it was (a real run would bump AttemptCount), and no track was written.
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var analysis = await db.VideoFaceAnalysisStatuses.AsNoTracking()
            .SingleAsync(s => s.VideoSemanticIndexId == video.IndexId);
        Assert.Equal(1, analysis.AttemptCount);
        Assert.Equal(0, await db.VideoFaceTracks
            .CountAsync(t => t.VideoFaceAnalysisStatusId == analysis.Id));
    }

    // ---- fakes / shapes -----------------------------------------------------

    private sealed class RecordingJobQueue : IJobQueue
    {
        public List<string> Enqueued { get; } = [];

        public Task<NubArca.Api.Domain.BackgroundJob> EnqueueAsync<TPayload>(
            string type, TPayload payload, int? maxAttempts = null, int? priority = null,
            string? idempotencyKey = null, CancellationToken cancellationToken = default)
        {
            Enqueued.Add(type);
            return Task.FromResult(new NubArca.Api.Domain.BackgroundJob
            {
                Id = Guid.NewGuid(), Type = type,
            });
        }

        public Task<JobQueueSnapshot> GetSnapshotAsync(int recentLimit = 20, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<bool> RequestCancellationAsync(Guid jobId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<AdminJobPage> ListAdminJobsAsync(
            AdminJobFilter filter, int page, int pageSize, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<AdminJobSummary?> GetAdminJobAsync(Guid jobId, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private sealed class CountingFrameExtractor : IVideoSemanticFrameStreamExtractor
    {
        public int Runs { get; private set; }

        public Task<string?> ExtractFramesStreamingAsync(
            Func<CancellationToken, Task<Stream>> openBlobContent,
            IReadOnlyList<VideoSemanticFrameRequest> requests,
            int frameMaxEdge,
            Func<VideoSemanticFrameResult, CancellationToken, Task> onFrame,
            CancellationToken cancellationToken)
        {
            Runs++;
            return Task.FromResult<string?>(null);
        }
    }

    private sealed class CountingFaceBackend : IFaceDetector, IFaceEmbedder
    {
        public int DetectCalls { get; private set; }
        public int EmbedCalls { get; private set; }

        public string Provider => AiProviders.Deterministic;

        public bool Supports(string capability)
            => capability is AiCapabilities.FaceDetection or AiCapabilities.FaceEmbedding;

        public Task<AiFaceDetectionResult> DetectFacesAsync(
            ReadOnlyMemory<byte> imageBytes, AiProfile profile,
            CancellationToken cancellationToken = default)
        {
            DetectCalls++;
            return Task.FromResult(new AiFaceDetectionResult(Array.Empty<DetectedFace>()));
        }

        public Task<AiEmbeddingResult> EmbedFaceAsync(
            ReadOnlyMemory<byte> faceCropBytes, AiProfile profile,
            CancellationToken cancellationToken = default)
        {
            EmbedCalls++;
            return Task.FromResult(new AiEmbeddingResult(OneHot(0), Dim, "cosine"));
        }
    }

    // Always resolves successfully, so the ONLY thing that can stop the backfill
    // is the capability flag itself.
    private sealed class StubResolver : IAiBackendResolver
    {
        private readonly CountingFaceBackend _backend;
        private readonly Guid _profileId;
        private readonly IServiceProvider _services;

        public StubResolver(CountingFaceBackend backend, Guid profileId, IServiceProvider services)
        {
            _backend = backend;
            _profileId = profileId;
            _services = services;
        }

        public async Task<AiBackendResolution<T>> ResolveForCapabilityAsync<T>(
            string capability, CancellationToken cancellationToken = default) where T : class, IAiBackend
            => await ResolveAsync<T>(capability, cancellationToken);

        public async Task<AiBackendResolution<T>> ResolveForProfileKeyAsync<T>(
            string profileKey, CancellationToken cancellationToken = default) where T : class, IAiBackend
            => await ResolveAsync<T>(AiCapabilities.FaceEmbedding, cancellationToken);

        public Task<AiResolution> GetCapabilityAvailabilityAsync(
            string capability, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        private async Task<AiBackendResolution<T>> ResolveAsync<T>(
            string capability, CancellationToken cancellationToken) where T : class, IAiBackend
        {
            var registry = _services.GetRequiredService<IAiProfileRegistry>();
            var profile = await registry.GetProfileByKeyAsync(FaceProfileKey, cancellationToken);
            return _backend is T backend && profile is not null
                ? AiBackendResolution<T>.Available(
                    backend, AiResolution.Available(capability, AiProviders.Deterministic, profile))
                : AiBackendResolution<T>.Unavailable(
                    AiResolution.Unavailable(capability, AiUnavailableReasons.ProviderUnavailable));
        }
    }

    private sealed class NoopDiagnostics : NubArca.Api.Ai.Diagnostics.IAiDiagnosticsWriter
    {
        public Task RecordProviderUnavailableAsync(
            string capability, Guid? profileId, string reasonCode,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed record ReviewItem(
        Guid TrackId, Guid FileItemId, string Name,
        long StartMilliseconds, long EndMilliseconds, long RepresentativeMilliseconds,
        int DetectionCount, double QualityScore);

    private sealed record ReviewPage(IReadOnlyList<ReviewItem> Items, bool HasMore);

    private sealed record PersonVideoMatch(
        string EvidenceType, long StartMilliseconds, long EndMilliseconds,
        long RepresentativeMilliseconds);

    private sealed record PersonVideo(
        Guid FileItemId, string Name, PersonVideoMatch BestMatch,
        IReadOnlyList<PersonVideoMatch> AdditionalMatches);

    private sealed record Suggestion(
        Guid PersonId, string? Name, double Similarity, int SupportingEvidenceCount);

    private sealed record Suggestions(
        double Threshold, IReadOnlyList<Suggestion> Items, string? UnavailableReason);
}
