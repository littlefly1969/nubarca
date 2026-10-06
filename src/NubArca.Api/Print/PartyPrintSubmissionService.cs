using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NubArca.Api.Data;
using NubArca.Api.Domain.Print;
using NubArca.Api.Party;
using NubArca.Api.Storage;

namespace NubArca.Api.Print;

public interface IPartyPrintSubmissionService
{
    Task<PartyPrintSubmitResult> SubmitAsync(
        PartyPrintAccess access,
        PartyPrintSubmitRequest request,
        string idempotencyKey,
        // Null when the guest has no participant session — the per-guest
        // ceiling then cannot apply, and only the party's budget bounds them.
        Guid? participantId,
        CancellationToken cancellationToken);
}

/// <summary>
/// Turns a guest's composition into a real print job, exactly once.
///
/// The order of operations here is the whole design, because each step can fail
/// and printing has a physical effect:
///
///  1. VALIDATE the shape — right product, right number of photographs, no
///     duplicates in a strip, framings that are framings (a placement no
///     further out than the whole photograph, or a legacy crop inside it).
///  2. RE-VALIDATE EVERY SOURCE against the database. The browser's list is a
///     suggestion: a photograph must still be a photograph, still belong to THIS
///     party, and still be visible to guests. One that was hidden or moderated
///     away between composing and printing must not reach paper.
///  3. IDEMPOTENCY. A key that has been seen returns the job it produced. A
///     double tap, a retried POST, a flaky network replaying a request — none of
///     them may put a second sheet through the printer.
///  4. RESERVE one unit of the product's budget, atomically.
///  5. RENDER. If composing fails, the unit goes back: nothing was accepted, so
///     nothing was spent.
///  6. ACCEPT — job, its sources, and the idempotency record in one save. From
///     here the unit stays spent whatever the printer does later, because by
///     then the paper may already have moved.
/// </summary>
public sealed class PartyPrintSubmissionService : IPartyPrintSubmissionService
{
    private readonly AppDbContext _db;
    private readonly IPartyPrintBudget _budget;
    private readonly IPartyMediaService _media;
    private readonly IDerivedBlobStorage _artifacts;
    private readonly PartyPrintComposer _composer;
    private readonly IPrintPhotoSourceReader _sources;
    private readonly NubArca.Api.Party.IPartyParticipantService _participants;

    public PartyPrintSubmissionService(
        AppDbContext db, IPartyPrintBudget budget, IPartyMediaService media,
        IDerivedBlobStorage artifacts, PartyPrintComposer composer,
        IPrintPhotoSourceReader sources,
        NubArca.Api.Party.IPartyParticipantService participants,
        IPrinterAccess printers)
    {
        _participants = participants;
        _printers = printers;
        _db = db;
        _budget = budget;
        _media = media;
        _artifacts = artifacts;
        _composer = composer;
        _sources = sources;
    }

    private readonly IPrinterAccess _printers;

    public async Task<PartyPrintSubmitResult> SubmitAsync(
        PartyPrintAccess access,
        PartyPrintSubmitRequest request,
        string idempotencyKey,
        Guid? participantId,
        CancellationToken cancellationToken)
    {
        // 1. Shape. The product under its current name: a page opened before
        // the twin strip was renamed still asks for "strip4".
        var productId = PartyPrintProducts.Normalize(request.Product);
        if (productId.Length == 0)
            return PartyPrintSubmitResult.Refuse(PartyPrintRefusal.Invalid);
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 200)
            return PartyPrintSubmitResult.Refuse(PartyPrintRefusal.Invalid);

        // As many photographs as the format takes — a twin strip four (the same
        // strip twice) or eight (two strips) — and each a DIFFERENT one: the
        // same picture twice on one sheet is not what was asked for.
        if (!PrintLayouts.PhotoCounts(productId).Contains(request.Slots.Count))
            return PartyPrintSubmitResult.Refuse(PartyPrintRefusal.Invalid);
        if (request.Slots.Select(s => s.ItemId).Distinct().Count() != request.Slots.Count)
            return PartyPrintSubmitResult.Refuse(PartyPrintRefusal.Invalid);
        // Each photograph is framed ONE way: by a placement (a current studio)
        // or by the four crop fractions (a page from before placements). Both
        // at once does not say what it means; neither frames nothing.
        foreach (var slot in request.Slots)
        {
            var valid = slot.Placement is { } placement
                ? !slot.HasCrop && PhotoPlacementGeometry.IsStructurallyValid(placement.ToPlacement())
                : slot.CropX is double x && slot.CropY is double y
                    && slot.CropWidth is double cw && slot.CropHeight is double ch
                    && PrintJobSource.IsValidCrop(x, y, cw, ch);
            if (!valid) return PartyPrintSubmitResult.Refuse(PartyPrintRefusal.Invalid);
        }

        // The sheet was composed for one paper; it prints on that paper or not
        // at all. Checked before the product, because a product the new paper
        // cannot make is the same situation seen from the other side.
        var paper = request.PaperSize ?? PrintPapers.Photo10x15;
        if (!PrintPapers.IsKnown(paper))
            return PartyPrintSubmitResult.Refuse(PartyPrintRefusal.Invalid);
        if (paper != access.Paper)
            return PartyPrintSubmitResult.Refuse(PartyPrintRefusal.PaperChanged);

        // Null when this printer and paper cannot make the product at all —
        // the matrix is checked here, never trusted from the page.
        var product = access.Product(productId);
        if (product is null || !product.Enabled)
            return PartyPrintSubmitResult.Refuse(PartyPrintRefusal.Unavailable);

        // 2. Sources, checked against the database rather than trusted.
        var visible = await _media.ListItemsAsync(
            access.OwnerUserId, access.PartyAlbumId, cancellationToken);
        if (visible is null) return PartyPrintSubmitResult.Refuse(PartyPrintRefusal.Unavailable);
        var printable = visible
            .Where(i => i.Kind == PartyMediaKind.Image)
            .Select(i => i.FileItemId)
            .ToHashSet();
        // Videos are not printable, and a video's poster is not a photograph.
        if (request.Slots.Any(s => !printable.Contains(s.ItemId)))
            return PartyPrintSubmitResult.Refuse(PartyPrintRefusal.InvalidSource);

        // How far OUT a photograph may go depends on its shape and its frame's:
        // no further than the whole photograph inside the frame. Checked from the
        // stored metadata, before anything is reserved. A photograph whose shape
        // is not known yet may be framed as it always could, at zoom 1 and in.
        var theme = ThemeFor(productId, request.Theme);
        var orientation = productId == PartyPrintProducts.Photo
            ? ParseOrientation(request.Orientation)
            : PartyPrintOrientation.FollowPhoto;
        var shapes = await PrintPhotoShapes.DisplayAspectsAsync(
            _db, access.OwnerUserId, request.Slots.Select(s => s.ItemId).ToList(), cancellationToken);
        double? FrameAspectOf(Guid itemId)
        {
            if (productId == PartyPrintProducts.TwinStrip4) return PrintLayouts.SlotAspect(PrintLayouts.TwinStrip4, PrintLayouts.Framed, Domain.Print.PrintPapers.Photo10x15, portrait: true);
            if (productId == PartyPrintProducts.Grid4) return PrintLayouts.SlotAspect(PrintLayouts.Grid4, PrintLayouts.Framed, paper, portrait: true);
            if (!shapes.TryGetValue(itemId, out var aspect) && orientation == PartyPrintOrientation.FollowPhoto)
                return null;
            // The composer's own rule: the sheet follows the photograph unless turned.
            var portrait = orientation switch
            {
                PartyPrintOrientation.Portrait => true,
                PartyPrintOrientation.Landscape => false,
                _ => aspect <= 1,
            };
            return theme == PartyPrintTheme.Overlay
                ? PrintLayouts.SlotAspect(PrintLayouts.Photo, PrintLayouts.FullBleed, paper, portrait)
                : PrintLayouts.SlotAspect(PrintLayouts.Photo, PrintLayouts.Framed, paper, portrait);
        }
        foreach (var slot in request.Slots)
        {
            if (slot.Placement is not { } requested) continue;
            var placement = requested.ToPlacement();
            if (placement.Zoom >= 1) continue;
            if (!shapes.TryGetValue(slot.ItemId, out var aspect) || FrameAspectOf(slot.ItemId) is not double frame
                || !PhotoPlacementGeometry.IsValid(aspect, frame, placement))
            {
                return PartyPrintSubmitResult.Refuse(PartyPrintRefusal.Invalid);
            }
        }

        // 3. Idempotency: the same key answers with the same job, always.
        var keyHash = HashKey(idempotencyKey);
        var existing = await _db.PartyPrintRequests.AsNoTracking()
            .Where(r => r.PartyAlbumId == access.PartyAlbumId && r.IdempotencyKeyHash == keyHash)
            .Select(r => new { r.PrintJobId, r.Product })
            .FirstOrDefaultAsync(cancellationToken);
        if (existing is not null)
        {
            // A key belongs to one submission; reusing it for a different
            // product is a client bug, not a second print. (A key stored as
            // "strip4" is the twin strip.)
            if (PartyPrintProducts.Normalize(existing.Product) != productId)
                return PartyPrintSubmitResult.Refuse(PartyPrintRefusal.Invalid);
            var seq = await _db.PrintJobs.AsNoTracking()
                .Where(j => j.Id == existing.PrintJobId)
                .Select(j => j.PublicSequence)
                .FirstOrDefaultAsync(cancellationToken);
            return PartyPrintSubmitResult.Accept(new PartyPrintAccepted(
                existing.PrintJobId, seq ?? 0, productId, product.Remaining,
                await QueueAheadAsync(
                    access.PrintStationId, existing.PrintJobId, cancellationToken)));
        }

        // 4a. The GUEST's own allowance first, atomically.
        //
        // Before the party's, deliberately: a guest who has had their share must
        // not consume one of the party's remaining sheets on the way to being
        // told no. Both ceilings apply, and this is the one that makes the paper
        // last the evening.
        var perGuest = product.PerGuest;
        if (participantId is Guid guest && perGuest > 0)
        {
            if (!await _participants.TryClaimPrintAsync(
                    guest, productId, perGuest, cancellationToken))
            {
                return PartyPrintSubmitResult.Refuse(PartyPrintRefusal.GuestBudgetExhausted);
            }
        }

        // 4b. On a LENT printer, one sheet of the loan's ceiling — and the
        // proof, taken in the same statement, that the loan is still live: a
        // share revoked while this guest composed stops the print here, and the
        // queue behind it simply finishes.
        var sheet = await _printers.TryTakeSheetAsync(access.PrinterShareId, cancellationToken);
        if (sheet != PrinterSheetResult.Taken)
        {
            if (participantId is Guid returned && perGuest > 0)
            {
                await _participants.ReleasePrintAsync(
                    returned, productId, perGuest, CancellationToken.None);
            }
            return PartyPrintSubmitResult.Refuse(sheet == PrinterSheetResult.Revoked
                ? PartyPrintRefusal.Unavailable
                : PartyPrintRefusal.ShareExhausted);
        }

        // 4c. One unit of the party's, atomically. Losing here means someone
        // else took the last.
        var reservation = await _budget.TryReserveAsync(
            access.PartyAlbumId, productId, cancellationToken);
        if (reservation is null)
        {
            // The guest's slot and the loan's sheet were taken a moment ago and
            // this sheet will not happen, so they go back: neither is spent by
            // the party running out.
            if (participantId is Guid held && perGuest > 0)
            {
                await _participants.ReleasePrintAsync(
                    held, productId, perGuest, CancellationToken.None);
            }
            await _printers.ReturnSheetAsync(access.PrinterShareId, CancellationToken.None);
            return PartyPrintSubmitResult.Refuse(PartyPrintRefusal.BudgetExhausted);
        }

        var jobId = Guid.NewGuid();

        // From here three things are held for this sheet: the guest's slot, the
        // party's unit and, on a lent printer, the loan's sheet. A sheet that is
        // never accepted — its source gone, its render failed, a racing twin
        // that won, the guest gone mid-compose — gives ALL THREE back, and only
        // then: accepted is the job's row existing, read from the database, so
        // a failure after the commit never refunds a sheet that will print.
        async Task<bool> ReleaseUnlessAcceptedAsync()
        {
            _db.ChangeTracker.Clear();
            if (await _db.PrintJobs.AsNoTracking().AnyAsync(j => j.Id == jobId, CancellationToken.None))
                return false;
            if (participantId is Guid holder && perGuest > 0)
            {
                await _participants.ReleasePrintAsync(
                    holder, productId, perGuest, CancellationToken.None);
            }
            await _budget.ReleaseAsync(access.PartyAlbumId, productId, CancellationToken.None);
            await _printers.ReturnSheetAsync(access.PrinterShareId, CancellationToken.None);
            return true;
        }

        try
        {
            // 5. Compose. Reading the originals is a server-side act: their
            // bytes never travel to the browser.
            // Held until the sheet is drawn: HEIC frames keep their decode slots.
            await using var sources = await _sources.OpenAsync(
                access.OwnerUserId, request.Slots.Select(s => s.ItemId).ToList(), cancellationToken);
            if (sources is null)
            {
                await ReleaseUnlessAcceptedAsync();
                return PartyPrintSubmitResult.Refuse(PartyPrintRefusal.InvalidSource);
            }
            var photos = new List<PartyPrintPhoto>(request.Slots.Count);
            for (var i = 0; i < request.Slots.Count; i++)
            {
                var slot = request.Slots[i];
                var bytes = sources.Photos[i];
                photos.Add(slot.Placement is { } placement
                    ? PartyPrintPhoto.Placed(bytes, placement.ToPlacement())
                    : new PartyPrintPhoto(bytes, slot.CropX!.Value, slot.CropY!.Value,
                        slot.CropWidth!.Value, slot.CropHeight!.Value));
            }

            // The number is reserved before the sheet is drawn, so it can be
            // printed ON it: the guest reads the same number off their phone and
            // off the paper.
            // The words' and the symbol's colours belong to the title on the photo only.
            // (Only a single photograph turns: four photographs sit as the paper
            // is named, and the twin strip is the one geometry the cut expects.)
            var overlay = theme == PartyPrintTheme.Overlay ? ParseOverlay(request) : null;
            var artifact = await _composer.RenderAsync(new PartyPrintComposition(
                productId, theme, photos,
                access.PartyName, access.FooterText,
                reservation.PublicSequence,
                orientation,
                access.Calibration,
                overlay,
                paper), cancellationToken);
            // What was actually printed, read off the sheet itself rather than
            // re-derived: a photograph that followed its own shape says so here.
            var drawn = SixLabors.ImageSharp.Image.Identify(artifact);
            var sheetOrientation = drawn.Height >= drawn.Width ? "portrait" : "landscape";

            await using var stream = new MemoryStream(artifact, writable: false);
            // Stage outside the lock; publish and claim in one protected step.
            // A party print artifact is owned ONLY by PrintJob.ArtifactStorageKey,
            // so the bytes and that column have to become durable together or a
            // purge of identical content could unlink them in between.
            var staged = await _artifacts.StageAsync(stream, cancellationToken);
            await using var stagedScope = staged.ConfigureAwait(false);
            await StoragePublish.PublishOwnedAsync(
                _db, _artifacts, staged,
                async (stored, ct) =>
                {

                // 6. Accept: the job, its sources and the idempotency record together.
                // The unique index on (party, key) is what makes a racing duplicate
                // fail here rather than reach the printer.
                var now = DateTime.UtcNow;
                _db.PrintJobs.Add(new PrintJob
                {
                    Id = jobId,
                    OwnerUserId = access.OwnerUserId,
                    PrintStationId = access.PrintStationId,
                    PrinterDeviceId = access.PrinterDeviceId,
                    // The composition's first photograph, so the job still has the
                    // single FK the pipeline expects; all of them are in the child
                    // table below.
                    FileItemId = request.Slots[0].ItemId,
                    Kind = productId switch
                    {
                        PartyPrintProducts.Grid4 => PrintJobKinds.PartyGrid4,
                        PartyPrintProducts.TwinStrip4 => PrintJobKinds.PartyStrip4,
                        _ => PrintJobKinds.PartyPhoto,
                    },
                    Format = access.PrintFormat(productId),
                    State = PrintJobStates.Ready,
                    PublicSequence = reservation.PublicSequence,
                    // Enough to say, without the artifact, what this sheet is:
                    // which paper, which composition, which way up, who cuts it.
                    RenderSpecificationJson = JsonSerializer.Serialize(new
                    {
                        paperSize = paper,
                        product = productId,
                        layout = productId,
                        orientation = sheetOrientation,
                        cutByPrinter = access.CutByPrinter(productId),
                        theme = theme.ToString().ToLowerInvariant(),
                        overlayText = overlay?.Text.ToString().ToLowerInvariant(),
                        overlayLogo = overlay?.Logo.ToString().ToLowerInvariant(),
                    }),
                    ArtifactStorageKey = stored.StorageKey,
                    ArtifactContentType = "image/jpeg",
                    ArtifactByteLength = stored.SizeBytes,
                    CreatedAt = now,
                    RenderedAt = now,
                });
                for (var i = 0; i < request.Slots.Count; i++)
                {
                    var slot = request.Slots[i];
                    var source = new PrintJobSource
                    {
                        Id = Guid.NewGuid(),
                        PrintJobId = jobId,
                        SlotIndex = i,
                        FileItemId = slot.ItemId,
                    };
                    if (slot.Placement is { } requested)
                    {
                        // The placement is the authority; the crop beside it is
                        // the part of the photograph it shows, for every reader of
                        // the older columns.
                        var placement = requested.ToPlacement();
                        source.PlacementCenterX = placement.CenterX;
                        source.PlacementCenterY = placement.CenterY;
                        source.PlacementZoom = placement.Zoom;
                        (source.CropX, source.CropY, source.CropWidth, source.CropHeight) =
                            shapes.TryGetValue(slot.ItemId, out var aspect) && FrameAspectOf(slot.ItemId) is double frame
                                ? PhotoPlacementGeometry.LegacyCrop(PhotoPlacementGeometry.Place(aspect, frame, placement))
                                : (0, 0, 1, 1);
                    }
                    else
                    {
                        (source.CropX, source.CropY, source.CropWidth, source.CropHeight) =
                            (slot.CropX!.Value, slot.CropY!.Value, slot.CropWidth!.Value, slot.CropHeight!.Value);
                    }
                    _db.PrintJobSources.Add(source);
                }
                _db.PartyPrintRequests.Add(new PartyPrintRequest
                {
                    Id = Guid.NewGuid(),
                    PartyAlbumId = access.PartyAlbumId,
                    IdempotencyKeyHash = keyHash,
                    Product = productId,
                    PrintJobId = jobId,
                    CreatedAt = now,
                });
                await _db.SaveChangesAsync(ct);
                },
                cancellationToken);

            return PartyPrintSubmitResult.Accept(new PartyPrintAccepted(
                jobId, reservation.PublicSequence, productId,
                Math.Max(0, reservation.RemainingAfter),
                await QueueAheadAsync(access.PrintStationId, jobId, cancellationToken)));
        }
        catch (DbUpdateException)
        {
            // Two requests raced on the same key: the index refused the second.
            // Give its reservations back and answer with the job that did win,
            // so a retry never becomes a second sheet.
            await ReleaseUnlessAcceptedAsync();
            var winner = await _db.PartyPrintRequests.AsNoTracking()
                .Where(r => r.PartyAlbumId == access.PartyAlbumId
                    && r.IdempotencyKeyHash == keyHash)
                .Select(r => r.PrintJobId)
                .FirstOrDefaultAsync(CancellationToken.None);
            if (winner == Guid.Empty)
                return PartyPrintSubmitResult.Refuse(PartyPrintRefusal.RenderFailed);
            var seq = await _db.PrintJobs.AsNoTracking()
                .Where(j => j.Id == winner).Select(j => j.PublicSequence)
                .FirstOrDefaultAsync(CancellationToken.None);
            return PartyPrintSubmitResult.Accept(new PartyPrintAccepted(
                winner, seq ?? 0, productId, product.Remaining,
                await QueueAheadAsync(access.PrintStationId, winner, CancellationToken.None)));
        }
        catch when (cancellationToken.IsCancellationRequested)
        {
            // The guest went away mid-compose: not a failure to record, and not
            // a sheet to charge anyone for.
            await ReleaseUnlessAcceptedAsync();
            throw;
        }
        catch
        {
            // Nothing was accepted, so nothing was spent. Had the sheet been
            // accepted before this, it stands: the failure is the server's,
            // and a retry with the same key is answered with that job.
            if (!await ReleaseUnlessAcceptedAsync()) throw;
            return PartyPrintSubmitResult.Refuse(PartyPrintRefusal.RenderFailed);
        }
    }

    private static PartyPrintTheme ParseTheme(string? value) => value?.ToLowerInvariant() switch
    {
        "midnight" => PartyPrintTheme.Midnight,
        "event" => PartyPrintTheme.Event,
        "overlay" => PartyPrintTheme.Overlay,
        _ => PartyPrintTheme.Pure,
    };

    /// <summary>An unreadable colour is the look's default, not a refused print.</summary>
    private static PartyPrintOverlay ParseOverlay(PartyPrintSubmitRequest request) => new(
        request.OverlayText?.ToLowerInvariant() switch
        {
            "black" => PartyPrintOverlayText.Black,
            "red" => PartyPrintOverlayText.Red,
            _ => PartyPrintOverlayText.White,
        },
        request.OverlayLogo?.ToLowerInvariant() == "dark"
            ? PartyPrintOverlayLogo.Dark
            : PartyPrintOverlayLogo.Light);

    /// <summary>
    /// The title on the photograph is a single-photograph look; four photos and
    /// the twin strip keep their frame.
    /// </summary>
    private static PartyPrintTheme ThemeFor(string product, string? value)
    {
        var theme = ParseTheme(value);
        return product != PartyPrintProducts.Photo
            && theme == PartyPrintTheme.Overlay
            ? PartyPrintTheme.Pure
            : theme;
    }

    private static PartyPrintOrientation ParseOrientation(string? value) => value switch
    {
        "portrait" => PartyPrintOrientation.Portrait,
        "landscape" => PartyPrintOrientation.Landscape,
        // Absent, misspelled, or from a client that predates the choice: follow
        // the photograph, which is what everybody got before and is still right.
        _ => PartyPrintOrientation.FollowPhoto,
    };

    /// <summary>
    /// The key is matched by hash, never kept: the same discipline every other
    /// capability secret in this system is held to.
    /// </summary>
    internal static string HashKey(string key) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    /// <summary>
    /// Sheets already accepted for this printer and not yet finished, excluding
    /// one job.
    ///
    /// Counted for the STATION, not the party: the queue a guest waits in is the
    /// machine's, and two parties sharing a printer share the wait. Terminal
    /// states are not in it — a completed or failed sheet is nobody's wait.
    /// </summary>
    private async Task<int> QueueAheadAsync(
        Guid printStationId, Guid excluding, CancellationToken cancellationToken)
    {
        return await _db.PrintJobs.AsNoTracking()
            .Where(j => j.PrintStationId == printStationId
                && j.Id != excluding
                && !PrintJobStates.Terminal.Contains(j.State))
            .CountAsync(cancellationToken);
    }
}

/// <summary>
/// Reads an original's bytes for composition. Separate so the submission service
/// does not reach into storage itself, and so a test can supply fixtures.
/// </summary>
public interface IPrintPhotoSourceReader
{
    /// <summary>
    /// The originals of one print's photographs, in order — held until the
    /// sheet has been composed, then disposed — or null when any of them cannot
    /// be read.
    /// </summary>
    Task<PrintPhotoSources?> OpenAsync(
        Guid ownerUserId, IReadOnlyList<Guid> fileItemIds, CancellationToken cancellationToken);
}

/// <summary>
/// A print's originals, as the composer takes them: bytes. Disposing it ends
/// what holds them — for HEIC, the decoded frames' files and decode slots — so
/// the composer must be done with the bytes first.
/// </summary>
public sealed class PrintPhotoSources(IReadOnlyList<byte[]> photos, IAsyncDisposable? held = null) : IAsyncDisposable
{
    public IReadOnlyList<byte[]> Photos { get; } = photos;

    public ValueTask DisposeAsync() => held?.DisposeAsync() ?? ValueTask.CompletedTask;
}
