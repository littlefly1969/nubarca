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
///     duplicates in a strip, crops that are actually crops.
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
    private readonly IPartyPrintSourceReader _sources;
    private readonly NubArca.Api.Party.IPartyParticipantService _participants;

    public PartyPrintSubmissionService(
        AppDbContext db, IPartyPrintBudget budget, IPartyMediaService media,
        IDerivedBlobStorage artifacts, PartyPrintComposer composer,
        IPartyPrintSourceReader sources,
        NubArca.Api.Party.IPartyParticipantService participants)
    {
        _participants = participants;
        _db = db;
        _budget = budget;
        _media = media;
        _artifacts = artifacts;
        _composer = composer;
        _sources = sources;
    }

    public async Task<PartyPrintSubmitResult> SubmitAsync(
        PartyPrintAccess access,
        PartyPrintSubmitRequest request,
        string idempotencyKey,
        Guid? participantId,
        CancellationToken cancellationToken)
    {
        // 1. Shape.
        if (!PartyPrintProducts.IsKnown(request.Product))
            return PartyPrintSubmitResult.Refuse(PartyPrintRefusal.Invalid);
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 200)
            return PartyPrintSubmitResult.Refuse(PartyPrintRefusal.Invalid);

        var required = PartyPrintProducts.RequiredPhotos(request.Product);
        if (request.Slots.Count != required)
            return PartyPrintSubmitResult.Refuse(PartyPrintRefusal.Invalid);
        // A strip of the same photograph four times is not a strip.
        if (request.Slots.Select(s => s.ItemId).Distinct().Count() != required)
            return PartyPrintSubmitResult.Refuse(PartyPrintRefusal.Invalid);
        if (request.Slots.Any(s => !PrintJobSource.IsValidCrop(
                s.CropX, s.CropY, s.CropWidth, s.CropHeight)))
        {
            return PartyPrintSubmitResult.Refuse(PartyPrintRefusal.Invalid);
        }

        var product = access.Product(request.Product);
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

        // 3. Idempotency: the same key answers with the same job, always.
        var keyHash = HashKey(idempotencyKey);
        var existing = await _db.PartyPrintRequests.AsNoTracking()
            .Where(r => r.PartyAlbumId == access.PartyAlbumId && r.IdempotencyKeyHash == keyHash)
            .Select(r => new { r.PrintJobId, r.Product })
            .FirstOrDefaultAsync(cancellationToken);
        if (existing is not null)
        {
            // A key belongs to one submission; reusing it for a different
            // product is a client bug, not a second print.
            if (existing.Product != request.Product)
                return PartyPrintSubmitResult.Refuse(PartyPrintRefusal.Invalid);
            var seq = await _db.PrintJobs.AsNoTracking()
                .Where(j => j.Id == existing.PrintJobId)
                .Select(j => j.PublicSequence)
                .FirstOrDefaultAsync(cancellationToken);
            return PartyPrintSubmitResult.Accept(new PartyPrintAccepted(
                existing.PrintJobId, seq ?? 0, request.Product, product.Remaining,
                await QueueAheadAsync(
                    access.PrintStationId, existing.PrintJobId, cancellationToken)));
        }

        // 4a. The GUEST's own allowance first, atomically.
        //
        // Before the party's, deliberately: a guest who has had their share must
        // not consume one of the party's remaining sheets on the way to being
        // told no. Both ceilings apply, and this is the one that makes the paper
        // last the evening.
        var isStrip = request.Product == PartyPrintProducts.Strip4;
        var perGuest = access.Product(request.Product)?.PerGuest ?? 0;
        if (participantId is Guid guest && perGuest > 0)
        {
            if (!await _participants.TryClaimPrintAsync(
                    guest, isStrip, perGuest, cancellationToken))
            {
                return PartyPrintSubmitResult.Refuse(PartyPrintRefusal.GuestBudgetExhausted);
            }
        }

        // 4b. One unit of the party's, atomically. Losing here means someone
        // else took the last.
        var reservation = await _budget.TryReserveAsync(
            access.PartyAlbumId, request.Product, cancellationToken);
        if (reservation is null)
        {
            // The guest's slot was claimed a moment ago and this sheet will not
            // happen, so it goes back: their allowance is not spent by the
            // party running out.
            if (participantId is Guid held && perGuest > 0)
            {
                await _participants.ReleasePrintAsync(
                    held, isStrip, perGuest, CancellationToken.None);
            }
            return PartyPrintSubmitResult.Refuse(PartyPrintRefusal.BudgetExhausted);
        }

        var jobId = Guid.NewGuid();
        try
        {
            // 5. Compose. Reading the originals is a server-side act: their
            // bytes never travel to the browser.
            var photos = new List<PartyPrintPhoto>(request.Slots.Count);
            foreach (var slot in request.Slots)
            {
                var bytes = await _sources.ReadAsync(
                    access.OwnerUserId, slot.ItemId, cancellationToken);
                if (bytes is null)
                {
                    await _budget.ReleaseAsync(
                        access.PartyAlbumId, request.Product, CancellationToken.None);
                    return PartyPrintSubmitResult.Refuse(PartyPrintRefusal.InvalidSource);
                }
                photos.Add(new PartyPrintPhoto(
                    bytes, slot.CropX, slot.CropY, slot.CropWidth, slot.CropHeight));
            }

            // The number is reserved before the sheet is drawn, so it can be
            // printed ON it: the guest reads the same number off their phone and
            // off the paper.
            var artifact = await _composer.RenderAsync(new PartyPrintComposition(
                request.Product, ParseTheme(request.Theme), photos,
                access.PartyName, access.FooterText,
                reservation.PublicSequence), cancellationToken);

            await using var stream = new MemoryStream(artifact, writable: false);
            var stored = await _artifacts.WriteAsync(stream, cancellationToken);

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
                Kind = request.Product == PartyPrintProducts.Strip4
                    ? PrintJobKinds.PartyStrip4
                    : PrintJobKinds.PartyPhoto,
                Format = PrintFormats.Photo10x15,
                State = PrintJobStates.Ready,
                PublicSequence = reservation.PublicSequence,
                RenderSpecificationJson = JsonSerializer.Serialize(new
                {
                    product = request.Product,
                    theme = ParseTheme(request.Theme).ToString().ToLowerInvariant(),
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
                _db.PrintJobSources.Add(new PrintJobSource
                {
                    Id = Guid.NewGuid(),
                    PrintJobId = jobId,
                    SlotIndex = i,
                    FileItemId = slot.ItemId,
                    CropX = slot.CropX,
                    CropY = slot.CropY,
                    CropWidth = slot.CropWidth,
                    CropHeight = slot.CropHeight,
                });
            }
            _db.PartyPrintRequests.Add(new PartyPrintRequest
            {
                Id = Guid.NewGuid(),
                PartyAlbumId = access.PartyAlbumId,
                IdempotencyKeyHash = keyHash,
                Product = request.Product,
                PrintJobId = jobId,
                CreatedAt = now,
            });
            await _db.SaveChangesAsync(cancellationToken);

            return PartyPrintSubmitResult.Accept(new PartyPrintAccepted(
                jobId, reservation.PublicSequence, request.Product,
                Math.Max(0, reservation.RemainingAfter),
                await QueueAheadAsync(access.PrintStationId, jobId, cancellationToken)));
        }
        catch (DbUpdateException)
        {
            // Two requests raced on the same key: the index refused the second.
            // Give the unit back and answer with the job that did win, so a
            // retry never becomes a second sheet.
            await _budget.ReleaseAsync(
                access.PartyAlbumId, request.Product, CancellationToken.None);
            _db.ChangeTracker.Clear();
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
                winner, seq ?? 0, request.Product, product.Remaining,
                await QueueAheadAsync(access.PrintStationId, winner, CancellationToken.None)));
        }
        catch when (!cancellationToken.IsCancellationRequested)
        {
            // Nothing was accepted, so nothing was spent.
            await _budget.ReleaseAsync(
                access.PartyAlbumId, request.Product, CancellationToken.None);
            _db.ChangeTracker.Clear();
            return PartyPrintSubmitResult.Refuse(PartyPrintRefusal.RenderFailed);
        }
    }

    private static PartyPrintTheme ParseTheme(string? value) => value?.ToLowerInvariant() switch
    {
        "midnight" => PartyPrintTheme.Midnight,
        "event" => PartyPrintTheme.Event,
        _ => PartyPrintTheme.Pure,
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
public interface IPartyPrintSourceReader
{
    Task<byte[]?> ReadAsync(Guid ownerUserId, Guid fileItemId, CancellationToken cancellationToken);
}
