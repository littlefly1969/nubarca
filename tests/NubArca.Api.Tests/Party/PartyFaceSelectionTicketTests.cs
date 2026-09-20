using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Ai;
using NubArca.Api.Ai.Backends;
using NubArca.Api.Ai.Faces;
using NubArca.Api.Ai.Resolution;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Domain.Ai;
using NubArca.Api.Party;
using NubArca.Api.Tests.Endpoints;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace NubArca.Api.Tests.Party;

// PARTY-FACE-SELECTION-TICKET-01. The face in the frame is the face that was
// searched for.
//
// Detection and search are two requests, and a detector is not obliged to give
// the same answer twice: it may reorder its output, nudge a box, or report a
// second person who has come closer to the camera. Before the ticket, the
// search asked PartyFaceSelection again — so a legitimate change of the
// detector's mind between the two calls could send the guest results for
// somebody standing behind them, under a picture of their own face.
//
// What these defend:
//   * the choice is made ONCE, by the detection, and the search is handed it;
//   * a search whose confirmed face is no longer there refuses, and embeds
//     nothing — there is no "closest available face" fallback;
//   * a ticket is bound to the selfie, the face package and the minute, so one
//     cannot be presented with different bytes, after a model change, or later;
//   * a refusal issues no ticket, because a refusal confirms no face.
public sealed class PartyFaceSelectionTicketTests
{
    private const string ProfileKey = "det-face-embedding-v1";

    // A is what the guest is shown: big and centred, so PartyFaceSelection
    // picks it over the other one without hesitating.
    private static readonly DetectedFace FaceA = Face(0.33, 0.33, 0.34, 0.34);

    // B is somebody further back, off to one side — until the second detector
    // run, where it is the dominant, centred face and a fresh selection would
    // choose it instead. That is the whole scenario, in one constant.
    private static readonly DetectedFace FaceBDistant = Face(0.04, 0.12, 0.16, 0.16);
    // Big enough to clear PartyFaceSelection's dominance margin over A, and
    // centred — otherwise the rule would call the second run ambiguous and the
    // scenario would never reach the question being asked.
    private static readonly DetectedFace FaceBDominant = Face(0.28, 0.28, 0.44, 0.44);

    // ── The regression this exists for ──────────────────────────────────────

    [Fact]
    public async Task The_search_embeds_the_face_the_detection_confirmed_not_the_one_it_would_pick_now()
    {
        using var f = FacesEnabledFactory();
        await SeedProfilesAsync(f);
        var (ownerId, albumId) = await AlbumAsync(f);

        // Run 1 (detect): A dominates. Run 2 (search): A is still there,
        // unchanged, but B has come forward and now dominates — so asking
        // PartyFaceSelection again would answer B.
        var backend = new ShiftingFaceBackend(
            [FaceA, FaceBDistant],
            [FaceBDominant, FaceA]);
        using var scope = f.Services.CreateScope();
        var service = Service(scope, backend);

        var detected = await service.DetectAsync(Selfie(), "image/png");
        Assert.Equal("found", detected.Status);
        AssertSameBox(FaceA, detected.Face!);
        Assert.NotNull(detected.SelectionToken);

        // Proof that the second run really would have chosen B, so this test
        // fails for the right reason if the binding is ever removed.
        var wouldChooseNow = PartyFaceSelection.Choose(
            [.. backend.SecondRun.Select(x => new PartyFaceSelection.DetectedFaceBox(x.X, x.Y, x.Width, x.Height))]);
        AssertSameBox(FaceBDominant, wouldChooseNow.Face!.Value);

        var outcome = await service.SearchAsync(
            ownerId, albumId, null, Selfie(), "image/png", detected.SelectionToken);

        // A, and only A. The box the search reports is the same `chosen` face
        // it embedded, so this is not a cosmetic assertion about the response:
        // it is the face the query vector was computed from.
        Assert.Equal("ready", outcome.Status);
        AssertSameBox(FaceA, outcome.Face!);
        Assert.Equal(2, backend.DetectCalls);
    }

    [Fact]
    public async Task A_confirmed_face_that_is_no_longer_there_refuses_instead_of_embedding_its_neighbour()
    {
        using var f = FacesEnabledFactory();
        await SeedProfilesAsync(f);
        var (ownerId, albumId) = await AlbumAsync(f);

        // The second run reports ONE face, and it is not the one the guest was
        // shown. There is no honest answer here, so there is no answer.
        var backend = new ShiftingFaceBackend([FaceA, FaceBDistant], [FaceBDominant]);
        using var scope = f.Services.CreateScope();
        var service = Service(scope, backend);

        var detected = await service.DetectAsync(Selfie(), "image/png");
        AssertSameBox(FaceA, detected.Face!);

        var outcome = await service.SearchAsync(
            ownerId, albumId, null, Selfie(), "image/png", detected.SelectionToken);

        Assert.Equal("face_selection_changed", outcome.Status);
        Assert.Null(outcome.SearchId);
        Assert.Empty(outcome.FileItemIds);
        // NOTHING WAS EMBEDDED. Not a different vector — no vector: B never
        // reached the embedder at all.
        Assert.Equal(0, backend.EmbedCalls);
        Assert.Empty(await SessionsAsync(f));
    }

    // ── What a ticket is bound to ───────────────────────────────────────────

    [Fact]
    public async Task A_ticket_minted_for_one_selfie_does_not_open_another()
    {
        using var f = FacesEnabledFactory();
        await SeedProfilesAsync(f);
        var (ownerId, albumId) = await AlbumAsync(f);
        var backend = new ShiftingFaceBackend([FaceA], [FaceA]);
        using var scope = f.Services.CreateScope();
        var service = Service(scope, backend);

        var detected = await service.DetectAsync(Selfie(16), "image/png");
        Assert.NotNull(detected.SelectionToken);

        // A different photograph, with a perfectly good face in it. The
        // coordinates would even be valid — which is exactly why the binding
        // cannot be to the coordinates alone.
        var outcome = await service.SearchAsync(
            ownerId, albumId, null, Selfie(24), "image/png", detected.SelectionToken);

        Assert.Equal("invalid_selection", outcome.Status);
        Assert.Equal(0, backend.EmbedCalls);
    }

    [Fact]
    public async Task An_expired_ticket_is_not_a_ticket()
    {
        using var f = FacesEnabledFactory();
        await SeedProfilesAsync(f);
        var (ownerId, albumId) = await AlbumAsync(f);
        var backend = new ShiftingFaceBackend([FaceA], [FaceA]);
        using var scope = f.Services.CreateScope();
        var service = Service(scope, backend);

        var tickets = scope.ServiceProvider.GetRequiredService<IPartyFaceSelectionTickets>();
        var profile = await scope.ServiceProvider.GetRequiredService<IAiProfileRegistry>()
            .GetProfileByKeyAsync(ProfileKey);
        var selfie = Selfie();

        // Minted for a moment that has already passed, and signed properly — so
        // what is being tested is the expiry and not the signature.
        var stale = tickets.Issue(
            selfie, profile!.Id,
            new PartyFaceBox(FaceA.X, FaceA.Y, FaceA.Width, FaceA.Height),
            DateTime.UtcNow - TimeSpan.FromHours(1));

        var outcome = await service.SearchAsync(
            ownerId, albumId, null, selfie, "image/png", stale);

        Assert.Equal("invalid_selection", outcome.Status);
        Assert.Equal(0, backend.EmbedCalls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-ticket")]
    public async Task A_missing_or_invented_ticket_is_refused(string? ticket)
    {
        using var f = FacesEnabledFactory();
        await SeedProfilesAsync(f);
        var (ownerId, albumId) = await AlbumAsync(f);
        var backend = new ShiftingFaceBackend([FaceA], [FaceA]);
        using var scope = f.Services.CreateScope();
        var service = Service(scope, backend);

        var outcome = await service.SearchAsync(
            ownerId, albumId, null, Selfie(), "image/png", ticket);

        Assert.Equal("invalid_selection", outcome.Status);
        Assert.Equal(0, backend.EmbedCalls);
    }

    [Fact]
    public async Task A_tampered_ticket_is_refused_wherever_it_was_touched()
    {
        using var f = FacesEnabledFactory();
        await SeedProfilesAsync(f);
        var (ownerId, albumId) = await AlbumAsync(f);
        var backend = new ShiftingFaceBackend([FaceA], [FaceA]);
        using var scope = f.Services.CreateScope();
        var service = Service(scope, backend);

        var selfie = Selfie();
        var detected = await service.DetectAsync(selfie, "image/png");
        var ticket = detected.SelectionToken!;

        // Every position in turn: the version, the nonce, the expiry, the box
        // and the signature all sit under the same MAC, so moving any of them
        // is the same answer. Sampled rather than exhaustive to keep it quick.
        for (var at = 0; at < ticket.Length; at += 7)
        {
            var edited = ticket.ToCharArray();
            edited[at] = edited[at] == 'A' ? 'B' : 'A';
            var outcome = await service.SearchAsync(
                ownerId, albumId, null, selfie, "image/png", new string(edited));
            Assert.Equal("invalid_selection", outcome.Status);
        }

        Assert.Equal(0, backend.EmbedCalls);
    }

    // ── A refusal confirms nothing ──────────────────────────────────────────

    [Fact]
    public async Task No_face_and_several_faces_issue_no_ticket_at_all()
    {
        using var f = FacesEnabledFactory();
        await SeedProfilesAsync(f);
        using var scope = f.Services.CreateScope();

        var none = Service(scope, new ShiftingFaceBackend([], []));
        var empty = await none.DetectAsync(Selfie(), "image/png");
        Assert.Equal("no_face", empty.Status);
        Assert.Null(empty.SelectionToken);
        Assert.Null(empty.Face);

        // Two faces the rule cannot tell apart: same size, both near the
        // middle. Nothing was confirmed, so nothing is carried.
        var twins = Service(scope, new ShiftingFaceBackend(
            [Face(0.20, 0.33, 0.30, 0.30), Face(0.52, 0.33, 0.30, 0.30)], []));
        var ambiguous = await twins.DetectAsync(Selfie(), "image/png");
        Assert.Equal("multiple_faces", ambiguous.Status);
        Assert.Null(ambiguous.SelectionToken);
        Assert.Null(ambiguous.Face);
    }

    // ── Fixture ─────────────────────────────────────────────────────────────

    private static void AssertSameBox(DetectedFace expected, PartyFaceBox actual)
    {
        Assert.Equal(expected.X, actual.X, 6);
        Assert.Equal(expected.Y, actual.Y, 6);
        Assert.Equal(expected.Width, actual.Width, 6);
        Assert.Equal(expected.Height, actual.Height, 6);
    }

    private static void AssertSameBox(DetectedFace expected, PartyFaceSelection.DetectedFaceBox actual) =>
        AssertSameBox(expected, new PartyFaceBox(actual.X, actual.Y, actual.Width, actual.Height));

    private static DetectedFace Face(double x, double y, double w, double h) =>
        new(x, y, w, h, Confidence: 0.9, Landmarks:
        [
            new(x + (w * 0.27), y + (h * 0.33)),
            new(x + (w * 0.73), y + (h * 0.33)),
            new(x + (w * 0.50), y + (h * 0.57)),
            new(x + (w * 0.33), y + (h * 0.80)),
            new(x + (w * 0.67), y + (h * 0.80)),
        ]);

    private static PartyFaceSearchService Service(IServiceScope scope, IAiBackendResolver resolver)
    {
        var sp = scope.ServiceProvider;
        return new PartyFaceSearchService(
            sp.GetRequiredService<AppDbContext>(),
            resolver,
            sp.GetRequiredService<IAiProfileRegistry>(),
            sp.GetRequiredService<IFaceSettingsProvider>(),
            sp.GetRequiredService<IAiVectorSerializer>(),
            sp.GetRequiredService<NubArca.Api.Storage.IBlobService>(),
            sp.GetRequiredService<IPartyFaceSelectionTickets>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<IConfiguration>());
    }

    private static async Task<(Guid OwnerId, Guid AlbumId)> AlbumAsync(SqliteWebApplicationFactory f)
    {
        var (ownerId, _) = await f.CreateAuthenticatedClientAsync();
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var album = new Album
        {
            Id = Guid.NewGuid(),
            OwnerUserId = ownerId,
            Name = "Party",
            ShowOnTv = true,
            CreatedAt = DateTime.UtcNow,
        };
        db.Albums.Add(album);
        await db.SaveChangesAsync();
        return (ownerId, album.Id);
    }

    private static async Task<List<PartyFaceSearchSession>> SessionsAsync(SqliteWebApplicationFactory f)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.PartyFaceSearchSessions.AsNoTracking().ToListAsync();
    }

    private static SqliteWebApplicationFactory FacesEnabledFactory()
    {
        var f = new SqliteWebApplicationFactory(
            new Dictionary<string, string?>
            {
                ["Ai:Enabled"] = "true",
                ["Ai:FaceDetectionEnabled"] = "true",
                ["Ai:FaceEmbeddingsEnabled"] = "true",
                ["Ai:Face:SearchDefaultSimilarityThreshold"] = "0.9",
            },
            poolHost: true);
        f.EnsureDatabaseCreated();
        return f;
    }

    private static async Task SeedProfilesAsync(SqliteWebApplicationFactory f)
    {
        using var scope = f.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IAiProfileRegistry>()
            .SeedDeterministicProfilesAsync();
    }

    private static byte[] Selfie(int dim = 16)
    {
        using var img = new Image<Rgba32>(dim, dim);
        using var ms = new MemoryStream();
        img.Save(ms, new PngEncoder());
        return ms.ToArray();
    }

    /// <summary>
    /// A detector that deliberately changes its mind, which a real one is
    /// entitled to do. The first call answers one way and every call after it
    /// answers another — the one condition under which re-running the selection
    /// and honouring the confirmed face give different results.
    /// </summary>
    private sealed class ShiftingFaceBackend : IAiBackendResolver, IFaceDetector, IFaceEmbedder
    {
        private readonly IReadOnlyList<DetectedFace> _first;

        public ShiftingFaceBackend(IReadOnlyList<DetectedFace> first, IReadOnlyList<DetectedFace> rest)
        {
            _first = first;
            SecondRun = rest;
        }

        public IReadOnlyList<DetectedFace> SecondRun { get; }

        public int DetectCalls { get; private set; }

        public int EmbedCalls { get; private set; }

        public string Provider => "deterministic";

        public bool Supports(string capability) =>
            capability is AiCapabilities.FaceDetection or AiCapabilities.FaceEmbedding;

        public Task<AiFaceDetectionResult> DetectFacesAsync(
            ReadOnlyMemory<byte> imageBytes, AiProfile profile, CancellationToken cancellationToken = default)
        {
            DetectCalls++;
            return Task.FromResult(new AiFaceDetectionResult(DetectCalls == 1 ? _first : SecondRun));
        }

        public Task<AiEmbeddingResult> EmbedFaceAsync(
            ReadOnlyMemory<byte> faceCropBytes, AiProfile profile, CancellationToken cancellationToken = default)
        {
            EmbedCalls++;
            return Task.FromResult(new AiEmbeddingResult(new float[32], 32, AiDistanceMetrics.Cosine));
        }

        public Task<AiBackendResolution<T>> ResolveForCapabilityAsync<T>(
            string capability, CancellationToken cancellationToken = default) where T : class, IAiBackend
            => Task.FromResult(AiBackendResolution<T>.Available((T)(IAiBackend)this, Res(capability)));

        public Task<AiBackendResolution<T>> ResolveForProfileKeyAsync<T>(
            string profileKey, CancellationToken cancellationToken = default) where T : class, IAiBackend
            => Task.FromResult(
                AiBackendResolution<T>.Available((T)(IAiBackend)this, Res(AiCapabilities.FaceEmbedding)));

        public Task<AiResolution> GetCapabilityAvailabilityAsync(
            string capability, CancellationToken cancellationToken = default)
            => Task.FromResult(Res(capability));

        private static AiResolution Res(string capability) => new()
        {
            IsAvailable = true,
            Capability = capability,
            Provider = "deterministic",
            ProfileKey = ProfileKey,
            Dimension = 32,
            DistanceMetric = AiDistanceMetrics.Cosine,
        };
    }
}
