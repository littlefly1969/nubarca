using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Domain.Print;
using NubArca.Api.Party;
using NubArca.Api.Print;
using NubArca.Api.Storage;
using NubArca.Api.Tests.Endpoints;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace NubArca.Api.Tests.Print;

/// <summary>
/// Submission is where budget, idempotency and source validation meet, and
/// where a mistake becomes a sheet of paper nobody asked for.
/// </summary>
public sealed class PartyPrintSubmissionTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory = new();
    public PartyPrintSubmissionTests() => _factory.EnsureDatabaseCreated();
    public void Dispose() => _factory.Dispose();

    private int _seeded;
    private readonly List<Guid> _photos = [];
    private readonly List<Guid> _videos = [];

    /// <summary>A party whose guest gallery holds four photographs and one video.</summary>
    private async Task<(PartyPrintAccess Access, Guid AlbumId)> SeedAsync(
        int photoMax = 5, int stripMax = 5, bool photoEnabled = true, bool stripEnabled = true)
    {
        var ownerId = await _factory.SeedUserAsync($"o{Interlocked.Increment(ref _seeded)}@example.com");
        var albumId = Guid.NewGuid();
        var linkId = Guid.NewGuid();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Albums.Add(new Album
        {
            Id = albumId, OwnerUserId = ownerId, Name = "Giulia & Matteo",
            CreatedAt = DateTime.UtcNow,
        });
        // A real party and a real link, because a guest's participant session
        // hangs off the link, the link belongs to the party, and neither foreign
        // key is decoration.
        var partyId = NubArca.Api.Tests.Party.PartySeed.Party(db, ownerId, albumId);
        db.PartyAlbumLinks.Add(new NubArca.Api.Domain.PartyAlbumLink
        {
            Id = linkId, PartyId = partyId, OwnerUserId = ownerId, AlbumId = albumId,
            TokenHash = Guid.NewGuid().ToString("N"), Enabled = true,
            CreatedAt = DateTime.UtcNow,
        });
        db.PartyPrintProfiles.Add(new PartyPrintProfile
        {
            Id = Guid.NewGuid(), PartyAlbumId = albumId, OwnerUserId = ownerId,
            Enabled = true,
            PhotoEnabled = photoEnabled, PhotoMaxPrints = photoMax,
            StripEnabled = stripEnabled, StripMaxPrints = stripMax,
            GridEnabled = true, GridMaxPrints = 5,
            PublicSequenceNext = 1,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        // A job's station, printer and every source are real foreign keys, so the
        // seed honours them — a test that faked them would be asserting against a
        // database the application could never produce.
        var stationId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();
        db.PrintStations.Add(new PrintStation
        {
            Id = stationId, OwnerUserId = ownerId, Name = "Postazione",
            Enabled = true, CreatedAt = DateTime.UtcNow,
        });
        db.PrinterDevices.Add(new PrinterDevice
        {
            Id = deviceId, PrintStationId = stationId, DeviceKey = "dev-1",
            DisplayName = "DNP DS620", AdapterKind = "fake",
            // A DNP with its cut queue: 10x15, and the twin strip cut in two.
            CapabilitiesJson = "{\"formats\":[\"10x15\",\"2x6x2\"]}",
            LastObservedState = PrintDeviceStates.Ready, LastSeenAt = DateTime.UtcNow,
        });

        _photos.Clear();
        _videos.Clear();
        for (var i = 0; i < 10; i++)
        {
            var blobId = Guid.NewGuid();
            var fileId = Guid.NewGuid();
            db.BlobObjects.Add(new BlobObject
            {
                Id = blobId, Sha256 = $"sha-{blobId:N}", SizeBytes = 1,
                StorageKey = $"sk/{blobId:N}", ReferenceCount = 1, CreatedAt = DateTime.UtcNow,
            });
            db.FileItems.Add(new FileItem
            {
                Id = fileId, OwnerUserId = ownerId, BlobObjectId = blobId,
                Name = $"p{i}.jpg", MimeType = "image/jpeg", SizeBytes = 1,
                CreatedAt = DateTime.UtcNow, EffectiveDateTaken = DateTime.UtcNow,
            });
            if (i < 9) _photos.Add(fileId); else _videos.Add(fileId);
        }
        await db.SaveChangesAsync();

        return (new PartyPrintAccess(
            linkId, albumId, ownerId, stationId, deviceId,
            "Giulia & Matteo", "Una notte da ricordare",
            new PartyPrintProductState(photoEnabled, photoMax),
            new PartyPrintProductState(stripEnabled, stripMax),
            StripCutByPrinter: true,
            Grid: new PartyPrintProductState(true, 5)), albumId);
    }

    private IPartyPrintSubmissionService Service(IServiceScope scope) =>
        new PartyPrintSubmissionService(
            scope.ServiceProvider.GetRequiredService<AppDbContext>(),
            new PartyPrintBudget(scope.ServiceProvider.GetRequiredService<AppDbContext>()),
            new FakeMedia(_photos, _videos),
            new FakeArtifacts(),
            new PartyPrintComposer(),
            new FakeSources(),
            scope.ServiceProvider
                .GetRequiredService<NubArca.Api.Party.IPartyParticipantService>(),
            new PrinterAccess(scope.ServiceProvider.GetRequiredService<AppDbContext>()));

    /// <summary>A strip's eight photographs: four for each of its two strips.</summary>
    private Guid[] Eight() => _photos.Take(8).ToArray();

    private static PartyPrintSubmitRequest Request(string product, params Guid[] ids) =>
        new(product, "pure",
            ids.Select(id => new PartyPrintSlotRequest(id, 0, 0, 1, 1)).ToList());

    private async Task<PartyPrintSubmitResult> SubmitAsync(
        PartyPrintAccess access, PartyPrintSubmitRequest request, string key,
        Guid? participantId = null)
    {
        using var scope = _factory.Services.CreateScope();
        return await Service(scope).SubmitAsync(access, request, key, participantId, default);
    }

    private static PartyPrintSubmitRequest OnPaper(string paper, string product, params Guid[] ids) =>
        Request(product, ids) with { PaperSize = paper };

    /// <summary>A job as the queue stores it: kind, format, its sources in order, and its specification.</summary>
    private async Task<(string Kind, string Format, Guid[] Sources, System.Text.Json.JsonElement Spec)> JobAsync(Guid jobId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var job = await db.PrintJobs.AsNoTracking().SingleAsync(j => j.Id == jobId);
        var sources = await db.PrintJobSources.AsNoTracking()
            .Where(s => s.PrintJobId == jobId).OrderBy(s => s.SlotIndex)
            .Select(s => s.FileItemId).ToArrayAsync();
        return (job.Kind, job.Format, sources,
            System.Text.Json.JsonDocument.Parse(job.RenderSpecificationJson).RootElement.Clone());
    }

    private async Task<PartyPrintProfile> ProfileAsync(Guid albumId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.PartyPrintProfiles.AsNoTracking()
            .SingleAsync(p => p.PartyAlbumId == albumId);
    }

    [Fact]
    public async Task A_Photo_Becomes_A_Ready_Job_That_Cost_One_Photo()
    {
        var (access, albumId) = await SeedAsync();
        var result = await SubmitAsync(access, Request(PartyPrintProducts.Photo, _photos[0]), "k1");

        Assert.True(result.Ok);
        Assert.Equal(1, result.Accepted!.PublicSequence);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var job = await db.PrintJobs.AsNoTracking().SingleAsync(j => j.Id == result.Accepted.JobId);
        // Ready is what the agent claims: the artifact exists before anyone is
        // told the print is coming.
        Assert.Equal(PrintJobStates.Ready, job.State);
        Assert.Equal(PrintJobKinds.PartyPhoto, job.Kind);
        // The strip is a composition, never a second paper size.
        Assert.Equal(PrintFormats.Photo10x15, job.Format);
        Assert.NotNull(job.ArtifactStorageKey);

        var profile = await ProfileAsync(albumId);
        Assert.Equal(1, profile.PhotoAcceptedCount);
        Assert.Equal(0, profile.StripAcceptedCount);
    }

    [Fact]
    public async Task A_Strip_Records_Its_Eight_Sources_In_Order_And_Costs_One_Strip()
    {
        var (access, albumId) = await SeedAsync();
        var result = await SubmitAsync(access,
            Request(PartyPrintProducts.TwinStrip4, Eight()), "k1");
        Assert.True(result.Ok);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var sources = await db.PrintJobSources.AsNoTracking()
            .Where(s => s.PrintJobId == result.Accepted!.JobId)
            .OrderBy(s => s.SlotIndex)
            .ToListAsync();

        // Eight real rows with real foreign keys, in the order the guest chose —
        // four for each strip, not ids buried in a JSON blob.
        Assert.Equal(8, sources.Count);
        Assert.Equal(Eight(), sources.Select(s => s.FileItemId));
        Assert.Equal([0, 1, 2, 3, 4, 5, 6, 7], sources.Select(s => s.SlotIndex));

        // Eight photographs, one sheet, one strip.
        var profile = await ProfileAsync(albumId);
        Assert.Equal(1, profile.StripAcceptedCount);
        Assert.Equal(0, profile.PhotoAcceptedCount);
    }

    // --- Papers, the matrix, four photographs ---------------------------------

    [Theory]
    [InlineData(PrintPapers.Photo10x15, "portrait")]
    [InlineData(PrintPapers.Photo13x18, "portrait")]
    [InlineData(PrintPapers.Photo20x15, "landscape")]
    public async Task Four_Photographs_Make_One_Sheet_Of_The_Loaded_Paper(string paper, string orientation)
    {
        var (access, albumId) = await SeedAsync();
        var four = _photos.Take(4).ToArray();
        var result = await SubmitAsync(access with { Paper = paper },
            OnPaper(paper, PartyPrintProducts.Grid4, four), $"g-{paper}");
        Assert.True(result.Ok);
        Assert.Equal(PartyPrintProducts.Grid4, result.Accepted!.Product);

        var job = await JobAsync(result.Accepted.JobId);
        Assert.Equal(PrintJobKinds.PartyGrid4, job.Kind);
        // The sheet goes to the agent as the paper it is printed on.
        Assert.Equal(paper, job.Format);
        // Top left, top right, bottom left, bottom right: the order chosen.
        Assert.Equal(four, job.Sources);
        // The specification says, on its own, what came out of the printer.
        Assert.Equal(paper, job.Spec.GetProperty("paperSize").GetString());
        Assert.Equal("grid4", job.Spec.GetProperty("product").GetString());
        Assert.Equal("grid4", job.Spec.GetProperty("layout").GetString());
        Assert.Equal(orientation, job.Spec.GetProperty("orientation").GetString());
        Assert.False(job.Spec.GetProperty("cutByPrinter").GetBoolean());

        // One sheet is one unit of the four-photo budget, and of nothing else.
        var profile = await ProfileAsync(albumId);
        Assert.Equal(1, profile.GridAcceptedCount);
        Assert.Equal(0, profile.PhotoAcceptedCount);
        Assert.Equal(0, profile.StripAcceptedCount);
    }

    [Fact]
    public async Task Four_Photographs_Are_Exactly_Four_Different_Ones()
    {
        var (access, albumId) = await SeedAsync();
        var p = _photos;
        foreach (var (ids, key) in new[]
        {
            (new[] { p[0], p[1], p[2] }, "three"),
            (new[] { p[0], p[1], p[2], p[3], p[4] }, "five"),
            (new[] { p[0], p[1], p[2], p[0] }, "twice"),
        })
        {
            var refused = await SubmitAsync(access, OnPaper(PrintPapers.Photo10x15, PartyPrintProducts.Grid4, ids), key);
            Assert.Equal(PartyPrintRefusal.Invalid, refused.Refusal);
        }
        Assert.Equal(0, (await ProfileAsync(albumId)).GridAcceptedCount);
    }

    [Theory]
    [InlineData(PrintPapers.Photo13x18)]
    [InlineData(PrintPapers.Photo20x15)]
    public async Task The_Twin_Strip_Exists_Only_On_10x15(string paper)
    {
        // Two strips are the printer's cut of a 10x15; no other paper has them,
        // however the request is dressed up.
        var (access, albumId) = await SeedAsync();
        var onPaper = access with { Paper = paper };
        Assert.False(onPaper.Offers(PartyPrintProducts.TwinStrip4));
        var refused = await SubmitAsync(onPaper,
            OnPaper(paper, PartyPrintProducts.TwinStrip4, Eight()), $"t-{paper}");
        Assert.Equal(PartyPrintRefusal.Unavailable, refused.Refusal);
        Assert.Equal(0, (await ProfileAsync(albumId)).StripAcceptedCount);
    }

    [Fact]
    public async Task A_Sheet_Prints_On_The_Paper_It_Was_Composed_For_Or_Not_At_All()
    {
        // The operator swapped to 20x15 while a guest composed a 10x15 sheet.
        var (access, albumId) = await SeedAsync();
        var swapped = access with { Paper = PrintPapers.Photo20x15 };
        var old = await SubmitAsync(swapped,
            OnPaper(PrintPapers.Photo10x15, PartyPrintProducts.Photo, _photos[0]), "old");
        Assert.Equal(PartyPrintRefusal.PaperChanged, old.Refusal);
        // A page from before papers composed 10x15, the only paper there was.
        var unsaid = await SubmitAsync(swapped, Request(PartyPrintProducts.Photo, _photos[0]), "unsaid");
        Assert.Equal(PartyPrintRefusal.PaperChanged, unsaid.Refusal);
        // A paper nobody knows is not a paper.
        var unknown = await SubmitAsync(swapped,
            OnPaper("a4", PartyPrintProducts.Photo, _photos[0]), "a4");
        Assert.Equal(PartyPrintRefusal.Invalid, unknown.Refusal);
        // Nothing was spent by any of them.
        Assert.Equal(0, (await ProfileAsync(albumId)).PhotoAcceptedCount);

        var right = await SubmitAsync(swapped,
            OnPaper(PrintPapers.Photo20x15, PartyPrintProducts.Photo, _photos[0]), "right");
        Assert.True(right.Ok);
        Assert.Equal(PrintPapers.Photo20x15, (await JobAsync(right.Accepted!.JobId)).Format);
        // And on the paper there always was, saying nothing still works.
        var plain = await SubmitAsync(access, Request(PartyPrintProducts.Photo, _photos[1]), "plain");
        Assert.True(plain.Ok);
    }

    [Fact]
    public async Task A_Page_From_Before_The_Rename_Still_Prints_Its_Twin_Strip()
    {
        var (access, albumId) = await SeedAsync();
        var legacy = await SubmitAsync(access, Request(PartyPrintProducts.LegacyStrip4, Eight()), "legacy");
        Assert.True(legacy.Ok);
        Assert.Equal(PartyPrintProducts.TwinStrip4, legacy.Accepted!.Product);
        // The retry under the new name is the same print, not a second one.
        var retry = await SubmitAsync(access, Request(PartyPrintProducts.TwinStrip4, Eight()), "legacy");
        Assert.Equal(legacy.Accepted.JobId, retry.Accepted!.JobId);
        Assert.Equal(1, (await ProfileAsync(albumId)).StripAcceptedCount);

        var job = await JobAsync(legacy.Accepted.JobId);
        Assert.Equal(PrintJobKinds.PartyStrip4, job.Kind);
        Assert.Equal(PrintFormats.Strip2x6Pair, job.Format);
        Assert.Equal(PrintPapers.Photo10x15, job.Spec.GetProperty("paperSize").GetString());
        Assert.Equal("twinStrip4", job.Spec.GetProperty("product").GetString());
        Assert.Equal("twinStrip4", job.Spec.GetProperty("layout").GetString());
        Assert.Equal("portrait", job.Spec.GetProperty("orientation").GetString());
        Assert.True(job.Spec.GetProperty("cutByPrinter").GetBoolean());
    }

    [Theory]
    [InlineData(PrintPapers.Photo10x15, "portrait")]
    [InlineData(PrintPapers.Photo10x15, "landscape")]
    [InlineData(PrintPapers.Photo13x18, "portrait")]
    [InlineData(PrintPapers.Photo13x18, "landscape")]
    [InlineData(PrintPapers.Photo20x15, "portrait")]
    [InlineData(PrintPapers.Photo20x15, "landscape")]
    public async Task A_Photo_Is_A_Sheet_Of_The_Loaded_Paper_Either_Way_Up(string paper, string orientation)
    {
        var (access, _) = await SeedAsync();
        var result = await SubmitAsync(access with { Paper = paper },
            OnPaper(paper, PartyPrintProducts.Photo, _photos[0]) with { Orientation = orientation },
            $"p-{paper}-{orientation}");
        Assert.True(result.Ok);
        var job = await JobAsync(result.Accepted!.JobId);
        Assert.Equal(paper, job.Format);
        Assert.Equal(paper, job.Spec.GetProperty("paperSize").GetString());
        Assert.Equal("photo", job.Spec.GetProperty("layout").GetString());
        Assert.Equal(orientation, job.Spec.GetProperty("orientation").GetString());
    }

    // --- On a printer lent to the host ---------------------------------------

    /// <summary>A loan of the party's printer to its host, from another account.</summary>
    private async Task<Guid> LendAsync(PartyPrintAccess access, int? maxSheets)
    {
        var lender = await _factory.SeedUserAsync($"lender{Interlocked.Increment(ref _seeded)}@example.com");
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        // A loan is of the LENDER's printer: the station is theirs, the party the host's.
        (await db.PrintStations.SingleAsync(s => s.Id == access.PrintStationId)).OwnerUserId = lender;
        var share = new PrinterShare
        {
            Id = Guid.NewGuid(), PrinterDeviceId = access.PrinterDeviceId, OwnerUserId = lender,
            GranteeUserId = access.OwnerUserId, MaxSheets = maxSheets, CreatedAt = DateTime.UtcNow,
        };
        db.PrinterShares.Add(share);
        await db.SaveChangesAsync();
        return share.Id;
    }

    private async Task<PrinterShare> ShareAsync(Guid id)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().PrinterShares
            .AsNoTracking().SingleAsync(s => s.Id == id);
    }

    [Fact]
    public async Task On_A_Lent_Printer_Every_Sheet_Takes_One_Of_The_Loan_And_None_Beyond_It()
    {
        var (access, albumId) = await SeedAsync();
        var shareId = await LendAsync(access, maxSheets: 2);
        var lent = access with { PrinterShareId = shareId };

        // A twin strip is one sheet, like a photo.
        Assert.True((await SubmitAsync(lent, Request(PartyPrintProducts.Photo, _photos[0]), "a")).Ok);
        Assert.True((await SubmitAsync(lent, Request(PartyPrintProducts.TwinStrip4, Eight()), "b")).Ok);
        Assert.Equal(2, (await ShareAsync(shareId)).UsedSheets);

        // The loan is spent while the party still has sheets of its own: the
        // owner's ceiling is the one that holds, and nothing else is spent.
        var third = await SubmitAsync(lent, Request(PartyPrintProducts.Photo, _photos[1]), "c");
        Assert.Equal(PartyPrintRefusal.ShareExhausted, third.Refusal);
        Assert.Equal(1, (await ProfileAsync(albumId)).PhotoAcceptedCount);
        Assert.Equal(2, (await ShareAsync(shareId)).UsedSheets);

        // A retry of an accepted print takes nothing more.
        Assert.True((await SubmitAsync(lent, Request(PartyPrintProducts.Photo, _photos[0]), "a")).Ok);
        Assert.Equal(2, (await ShareAsync(shareId)).UsedSheets);
    }

    [Fact]
    public async Task A_Loan_Ended_While_A_Guest_Composed_Stops_That_Print_At_Acceptance()
    {
        var (access, albumId) = await SeedAsync();
        var shareId = await LendAsync(access, maxSheets: null);
        var lent = access with { PrinterShareId = shareId };
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            (await db.PrinterShares.SingleAsync(s => s.Id == shareId)).RevokedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }
        // The capability was resolved before the owner ended the loan; the
        // print still does not go through, and costs the party nothing.
        var refused = await SubmitAsync(lent, Request(PartyPrintProducts.Photo, _photos[0]), "late");
        Assert.Equal(PartyPrintRefusal.Unavailable, refused.Refusal);
        Assert.Equal(0, (await ProfileAsync(albumId)).PhotoAcceptedCount);
    }

    [Fact]
    public async Task A_Party_Out_Of_Budget_Gives_The_Loans_Sheet_Back()
    {
        var (access, _) = await SeedAsync(photoMax: 1);
        var shareId = await LendAsync(access, maxSheets: 5);
        var lent = access with { PrinterShareId = shareId };
        Assert.True((await SubmitAsync(lent, Request(PartyPrintProducts.Photo, _photos[0]), "one")).Ok);
        var none = await SubmitAsync(lent, Request(PartyPrintProducts.Photo, _photos[1]), "two");
        Assert.Equal(PartyPrintRefusal.BudgetExhausted, none.Refusal);
        // Only the sheet that became a job was taken from the loan.
        Assert.Equal(1, (await ShareAsync(shareId)).UsedSheets);
    }

    [Fact]
    public async Task A_Twin_Strip_Is_Sent_As_2x6x2_And_Needs_The_Printers_Cut()
    {
        var (access, _) = await SeedAsync();

        var strip = await SubmitAsync(access,
            Request(PartyPrintProducts.TwinStrip4, Eight()), "s1");
        var photo = await SubmitAsync(access, Request(PartyPrintProducts.Photo, _photos[0]), "p1");
        // The two strips are the printer's cut; without it there is no twin
        // strip at all — never a sheet for scissors.
        var uncut = await SubmitAsync(access with { StripCutByPrinter = false },
            Request(PartyPrintProducts.TwinStrip4, Eight()), "s2");
        Assert.True(strip.Ok && photo.Ok);
        Assert.Equal(PartyPrintRefusal.Unavailable, uncut.Refusal);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        async Task<string> FormatOf(PartyPrintSubmitResult result) =>
            (await db.PrintJobs.AsNoTracking().SingleAsync(j => j.Id == result.Accepted!.JobId)).Format;

        // Still the same 10x15 sheet; the format is what tells the agent to cut it.
        Assert.Equal(PrintFormats.Strip2x6Pair, await FormatOf(strip));
        Assert.Equal(PrintFormats.Photo10x15, await FormatOf(photo));
    }

    [Fact]
    public async Task The_Title_On_The_Photo_Is_A_Photo_Look_And_A_Strip_Keeps_Its_Frame()
    {
        var (access, _) = await SeedAsync();
        var photo = await SubmitAsync(access,
            Request(PartyPrintProducts.Photo, _photos[0])
                with { Theme = "overlay", OverlayText = "red", OverlayLogo = "dark" }, "o1");
        var plain = await SubmitAsync(access,
            Request(PartyPrintProducts.Photo, _photos[0]) with { Theme = "overlay" }, "o2");
        var strip = await SubmitAsync(access,
            Request(PartyPrintProducts.TwinStrip4, Eight())
                with { Theme = "overlay", OverlayText = "red", OverlayLogo = "dark" }, "o3");
        var framed = await SubmitAsync(access,
            Request(PartyPrintProducts.Photo, _photos[0])
                with { Theme = "midnight", OverlayText = "red", OverlayLogo = "dark" }, "o4");
        Assert.True(photo.Ok && plain.Ok && strip.Ok && framed.Ok);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        async Task<(string? Theme, string? Text, string? Logo)> SpecOf(PartyPrintSubmitResult result)
        {
            var spec = System.Text.Json.JsonDocument.Parse((await db.PrintJobs.AsNoTracking()
                .SingleAsync(j => j.Id == result.Accepted!.JobId)).RenderSpecificationJson).RootElement;
            return (spec.GetProperty("theme").GetString(), spec.GetProperty("overlayText").GetString(),
                spec.GetProperty("overlayLogo").GetString());
        }
        // The look, and its two colours chosen apart.
        Assert.Equal(("overlay", "red", "dark"), await SpecOf(photo));
        // Unsaid, they are white words and the light symbol.
        Assert.Equal(("overlay", "white", "light"), await SpecOf(plain));
        // A strip keeps its frame, and a framed look has no overlay colours.
        Assert.Equal(("pure", null, null), await SpecOf(strip));
        Assert.Equal(("midnight", null, null), await SpecOf(framed));
    }

    [Fact]
    public async Task The_Same_Idempotency_Key_Prints_Once()
    {
        var (access, albumId) = await SeedAsync();
        var request = Request(PartyPrintProducts.Photo, _photos[0]);

        var first = await SubmitAsync(access, request, "same-key");
        var second = await SubmitAsync(access, request, "same-key");
        var third = await SubmitAsync(access, request, "same-key");

        Assert.True(first.Ok && second.Ok && third.Ok);
        // One job, one artifact, one unit of budget — whatever the network did.
        Assert.Equal(first.Accepted!.JobId, second.Accepted!.JobId);
        Assert.Equal(first.Accepted.JobId, third.Accepted!.JobId);
        Assert.Equal(1, (await ProfileAsync(albumId)).PhotoAcceptedCount);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await db.PrintJobs.CountAsync());
    }

    [Fact]
    public async Task A_Different_Key_Is_A_Different_Print()
    {
        var (access, albumId) = await SeedAsync();
        var a = await SubmitAsync(access, Request(PartyPrintProducts.Photo, _photos[0]), "k1");
        var b = await SubmitAsync(access, Request(PartyPrintProducts.Photo, _photos[0]), "k2");

        Assert.NotEqual(a.Accepted!.JobId, b.Accepted!.JobId);
        // A second keepsake of the same photograph is a second print.
        Assert.Equal(2, (await ProfileAsync(albumId)).PhotoAcceptedCount);
        Assert.NotEqual(a.Accepted.PublicSequence, b.Accepted.PublicSequence);
    }

    [Fact]
    public async Task A_Strip_Needs_Eight_DIFFERENT_Photographs()
    {
        var (access, albumId) = await SeedAsync();

        // Too few — the old four included, which would print two copies — too
        // many, and a repeated picture are all refused.
        Assert.Equal(PartyPrintRefusal.Invalid,
            (await SubmitAsync(access, Request(PartyPrintProducts.TwinStrip4, _photos[0]), "a")).Refusal);
        Assert.Equal(PartyPrintRefusal.Invalid,
            (await SubmitAsync(access, Request(PartyPrintProducts.TwinStrip4,
                _photos[0], _photos[1], _photos[2], _photos[3]), "four")).Refusal);
        Assert.Equal(PartyPrintRefusal.Invalid,
            (await SubmitAsync(access, Request(PartyPrintProducts.TwinStrip4, _photos.Take(9).ToArray()), "b")).Refusal);
        Assert.Equal(PartyPrintRefusal.Invalid,
            (await SubmitAsync(access, Request(PartyPrintProducts.TwinStrip4,
                [.. _photos.Take(7), _photos[0]]), "c")).Refusal);

        // None of them cost anything.
        Assert.Equal(0, (await ProfileAsync(albumId)).StripAcceptedCount);
    }

    [Fact]
    public async Task Videos_And_Photographs_From_Elsewhere_Are_Refused()
    {
        var (access, albumId) = await SeedAsync();

        // A video is not printable, and its poster is not a photograph.
        Assert.Equal(PartyPrintRefusal.InvalidSource,
            (await SubmitAsync(access, Request(PartyPrintProducts.Photo, _videos[0]), "v")).Refusal);
        // Nor is a photograph that is not in THIS party's guest gallery — the
        // browser's list is a suggestion, not an authority.
        Assert.Equal(PartyPrintRefusal.InvalidSource,
            (await SubmitAsync(access, Request(PartyPrintProducts.Photo, Guid.NewGuid()), "x")).Refusal);

        Assert.Equal(0, (await ProfileAsync(albumId)).PhotoAcceptedCount);
    }

    [Fact]
    public async Task A_Crop_That_Is_Not_A_Crop_Is_Refused_Before_Anything_Is_Spent()
    {
        var (access, albumId) = await SeedAsync();
        var bad = new PartyPrintSubmitRequest(PartyPrintProducts.Photo, "pure",
            [new PartyPrintSlotRequest(_photos[0], 0.9, 0, 0.5, 0.5)]);

        Assert.Equal(PartyPrintRefusal.Invalid, (await SubmitAsync(access, bad, "k")).Refusal);
        Assert.Equal(0, (await ProfileAsync(albumId)).PhotoAcceptedCount);
    }

    [Fact]
    public async Task Running_Out_Of_One_Product_Leaves_The_Other_Printing()
    {
        // The rule the whole feature is built around.
        var (access, albumId) = await SeedAsync(photoMax: 1, stripMax: 1);

        Assert.True((await SubmitAsync(access,
            Request(PartyPrintProducts.Photo, _photos[0]), "p1")).Ok);
        var second = await SubmitAsync(access, Request(PartyPrintProducts.Photo, _photos[1]), "p2");
        Assert.Equal(PartyPrintRefusal.BudgetExhausted, second.Refusal);

        // Photos are gone; strips are untouched.
        var strip = await SubmitAsync(access,
            Request(PartyPrintProducts.TwinStrip4, Eight()), "s1");
        Assert.True(strip.Ok);

        var profile = await ProfileAsync(albumId);
        Assert.Equal(1, profile.PhotoAcceptedCount);
        Assert.Equal(1, profile.StripAcceptedCount);
    }

    [Fact]
    public async Task A_Disabled_Product_Refuses_Without_Spending()
    {
        var (access, albumId) = await SeedAsync(photoEnabled: false);
        var result = await SubmitAsync(access, Request(PartyPrintProducts.Photo, _photos[0]), "k");
        Assert.Equal(PartyPrintRefusal.Unavailable, result.Refusal);
        Assert.Equal(0, (await ProfileAsync(albumId)).PhotoAcceptedCount);
    }

    [Fact]
    public async Task One_Guest_Is_Bounded_By_Their_Own_Share_Before_The_Party_Is()
    {
        // A party-wide budget alone is spent by whoever reaches the studio
        // first. The per-guest ceiling is what makes the paper last the evening,
        // so it is checked BEFORE the party's — a guest who has had their share
        // must not consume one of the party's remaining sheets on the way to
        // being told no.
        var (access, albumId) = await SeedAsync(photoMax: 20);
        var guest = await SeedGuestAsync(access.PartyAlbumLinkId);
        var bounded = access with { Photo = access.Photo with { PerGuest = 2 } };

        for (var i = 0; i < 2; i++)
        {
            var ok = await SubmitAsync(
                bounded, Request(PartyPrintProducts.Photo, _photos[i]), $"k{i}", guest);
            Assert.True(ok.Ok);
        }

        var refused = await SubmitAsync(
            bounded, Request(PartyPrintProducts.Photo, _photos[2]), "k2", guest);
        Assert.Equal(PartyPrintRefusal.GuestBudgetExhausted, refused.Refusal);

        // The party still has paper — 18 of its 20 — so this is the guest's
        // limit talking, not the party's.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var profile = await db.PartyPrintProfiles.AsNoTracking()
            .SingleAsync(p => p.PartyAlbumId == albumId);
        Assert.Equal(2, profile.PhotoAcceptedCount);
    }

    [Fact]
    public async Task Another_Guest_Has_Their_Own_Share()
    {
        var (access, _) = await SeedAsync(photoMax: 20);
        var bounded = access with { Photo = access.Photo with { PerGuest = 1 } };
        var first = await SeedGuestAsync(access.PartyAlbumLinkId);
        var second = await SeedGuestAsync(access.PartyAlbumLinkId);

        Assert.True((await SubmitAsync(
            bounded, Request(PartyPrintProducts.Photo, _photos[0]), "a", first)).Ok);
        Assert.Equal(PartyPrintRefusal.GuestBudgetExhausted, (await SubmitAsync(
            bounded, Request(PartyPrintProducts.Photo, _photos[1]), "b", first)).Refusal);
        // One guest reaching their limit says nothing about anybody else.
        Assert.True((await SubmitAsync(
            bounded, Request(PartyPrintProducts.Photo, _photos[2]), "c", second)).Ok);
    }

    [Fact]
    public async Task A_Guest_Slot_Goes_Back_When_The_Party_Has_Run_Out()
    {
        // The guest's allowance must not be spent by the PARTY running out: the
        // sheet never happened, so neither did their claim on it.
        var (access, _) = await SeedAsync(photoMax: 1);
        var bounded = access with { Photo = access.Photo with { PerGuest = 3 } };
        var guest = await SeedGuestAsync(access.PartyAlbumLinkId);

        Assert.True((await SubmitAsync(
            bounded, Request(PartyPrintProducts.Photo, _photos[0]), "a", guest)).Ok);
        Assert.Equal(PartyPrintRefusal.BudgetExhausted, (await SubmitAsync(
            bounded, Request(PartyPrintProducts.Photo, _photos[1]), "b", guest)).Refusal);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var participant = await db.PartyParticipants.AsNoTracking().SingleAsync(p => p.Id == guest);
        // One accepted print, one refusal that cost them nothing.
        Assert.Equal(1, participant.AcceptedPhotoPrintCount);
    }

    [Fact]
    public async Task Zero_Per_Guest_Means_No_Per_Guest_Limit()
    {
        // The same convention the upload quotas use: 0 is not a small number,
        // it is the absence of a ceiling.
        var (access, _) = await SeedAsync(photoMax: 20);
        var guest = await SeedGuestAsync(access.PartyAlbumLinkId);
        for (var i = 0; i < 3; i++)
        {
            Assert.True((await SubmitAsync(
                access, Request(PartyPrintProducts.Photo, _photos[i]), $"n{i}", guest)).Ok);
        }
    }

    /// <summary>A guest with a participant session on this link.</summary>
    private async Task<Guid> SeedGuestAsync(Guid linkId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var id = Guid.NewGuid();
        db.PartyParticipants.Add(new NubArca.Api.Domain.PartyParticipant
        {
            Id = id,
            PartyAlbumLinkId = linkId,
            TokenHash = Guid.NewGuid().ToString("N"),
            CreatedAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return id;
    }

    [Fact]
    public async Task A_Render_Failure_Costs_Nothing()
    {
        var (access, albumId) = await SeedAsync();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = new PartyPrintSubmissionService(
            db, new PartyPrintBudget(db), new FakeMedia(_photos, _videos),
            new FakeArtifacts(), new PartyPrintComposer(),
            // Bytes that are not an image: composing them throws.
            new BrokenSources(),
            scope.ServiceProvider
                .GetRequiredService<NubArca.Api.Party.IPartyParticipantService>(),
            new PrinterAccess(db));

        var result = await service.SubmitAsync(
            access, Request(PartyPrintProducts.Photo, _photos[0]), "k", null, default);

        Assert.Equal(PartyPrintRefusal.RenderFailed, result.Refusal);
        // Nothing was accepted, so the unit went back.
        Assert.Equal(0, (await ProfileAsync(albumId)).PhotoAcceptedCount);
        Assert.Equal(0, await db.PrintJobs.CountAsync());
    }

    [Fact]
    public async Task The_Idempotency_Key_Is_Stored_Hashed()
    {
        var (access, _) = await SeedAsync();
        await SubmitAsync(access, Request(PartyPrintProducts.Photo, _photos[0]), "secret-key");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stored = await db.PartyPrintRequests.AsNoTracking().SingleAsync();
        // The raw key is never kept, like every other capability secret here.
        Assert.DoesNotContain("secret-key", stored.IdempotencyKeyHash);
        Assert.Equal(64, stored.IdempotencyKeyHash.Length);
    }

    // --- Fakes: the party's gallery, its originals, and the artifact store ---

    private sealed class FakeMedia(List<Guid> photos, List<Guid> videos) : IPartyMediaService
    {
        public Task<PartyAlbumHeader?> GetAlbumAsync(Guid o, Guid a, CancellationToken c) =>
            Task.FromResult<PartyAlbumHeader?>(new PartyAlbumHeader("Festa", 5, null));
        public Task<IReadOnlyList<PartyMediaItem>?> ListItemsAsync(Guid o, Guid a, CancellationToken c) =>
            Task.FromResult<IReadOnlyList<PartyMediaItem>?>(
            [
                .. photos.Select(p => new PartyMediaItem(p, PartyMediaKind.Image)),
                .. videos.Select(v => new PartyMediaItem(v, PartyMediaKind.Video)),
            ]);
        public Task<PartyMediaKind?> GetVisibleMediaKindAsync(Guid o, Guid a, Guid f, CancellationToken c) =>
            Task.FromResult<PartyMediaKind?>(
                photos.Contains(f) ? PartyMediaKind.Image
                : videos.Contains(f) ? PartyMediaKind.Video : null);
    }

    private sealed class FakeSources : IPartyPrintSourceReader
    {
        public Task<byte[]?> ReadAsync(Guid owner, Guid fileItemId, CancellationToken c)
        {
            using var image = new Image<Rgba32>(1200, 900);
            image.Mutate(x => x.Fill(new Rgba32(0xC9, 0x76, 0x2F)));
            using var ms = new MemoryStream();
            image.SaveAsJpeg(ms);
            return Task.FromResult<byte[]?>(ms.ToArray());
        }
    }

    private sealed class BrokenSources : IPartyPrintSourceReader
    {
        public Task<byte[]?> ReadAsync(Guid owner, Guid fileItemId, CancellationToken c) =>
            Task.FromResult<byte[]?>([0x00, 0x01, 0x02, 0x03]);
    }

    /// <summary>Accepts the artifact and remembers nothing: the store is not what
    /// these tests are about, and the real one has its own.</summary>
    private sealed class FakeArtifacts : IDerivedBlobStorage
    {
        public async Task<BlobWriteResult> WriteAsync(
            Stream content, CancellationToken c = default)
        {
            var staged = await StageAsync(content, c);
            return await PublishAsync(staged, c);
        }
        public async Task<StagedBlobWrite> StageAsync(
            Stream content, CancellationToken c = default)
        {
            using var ms = new MemoryStream();
            await content.CopyToAsync(ms, c);
            var sha = new string('a', 64);
            return new StagedBlobWrite(
                sha, $"objects/{sha[..2]}/{sha[2..4]}/{sha}", ms.Length,
                stagedPath: string.Empty,
                discard: static _ => ValueTask.CompletedTask);
        }
        public Task<BlobWriteResult> PublishAsync(
            StagedBlobWrite staged, CancellationToken c = default)
        {
            staged.MarkConsumed();
            return Task.FromResult(new BlobWriteResult(
                staged.Sha256, staged.StorageKey, staged.SizeBytes, false));
        }
        public Task<Stream> OpenReadAsync(string key, CancellationToken c = default) =>
            Task.FromResult<Stream>(new MemoryStream());
        public Task<bool> ExistsAsync(string key, CancellationToken c = default) =>
            Task.FromResult(true);
        public Task DeleteAsync(string key, CancellationToken c = default) => Task.CompletedTask;
        // Null = "age unknown" → callers must treat it as brand new and refuse
        // to delete. Safe default for a fake that keeps no bytes.
        public Task<DateTimeOffset?> GetLastWriteTimeUtcAsync(
            string key, CancellationToken c = default)
            => Task.FromResult<DateTimeOffset?>(null);
        public async IAsyncEnumerable<string> EnumerateStorageKeysAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken c = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
