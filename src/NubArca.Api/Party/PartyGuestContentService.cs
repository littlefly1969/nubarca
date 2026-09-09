using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NubArca.Api.Data;
using NubArca.Api.Domain;

namespace NubArca.Api.Party;

public sealed class PartyGuestContentService : IPartyGuestContentService
{
    private readonly AppDbContext _db;
    private readonly TimeProvider _clock;

    public PartyGuestContentService(AppDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    public async Task<IReadOnlyList<PartyGuestContentDto>?> ListAsync(
        Guid ownerUserId, Guid partyId, CancellationToken cancellationToken = default)
    {
        var owns = await _db.Parties
            .AsNoTracking()
            .AnyAsync(p => p.Id == partyId && p.OwnerUserId == ownerUserId, cancellationToken);
        if (!owns)
        {
            return null;
        }

        var stored = await _db.PartyGuestContents
            .AsNoTracking()
            .Where(c => c.PartyId == partyId)
            .ToDictionaryAsync(c => c.Kind, cancellationToken);

        // EVERY kind, in product order — the ones the host has written and the
        // ones they have not. A slot they have never touched arrives at version
        // 0 with the product's default visibility, which is what makes those
        // defaults the server's answer rather than the editor's guess.
        return PartyGuestContentKinds.All
            .Select(kind => stored.TryGetValue(kind, out var row) ? Project(row) : Blank(kind))
            .ToList();
    }

    public async Task<PartyGuestContentResult> UpsertAsync(
        Guid ownerUserId,
        Guid partyId,
        string kind,
        PartyGuestContentWrite write,
        CancellationToken cancellationToken = default)
    {
        if (!PartyGuestContentKinds.IsKnown(kind))
        {
            return new PartyGuestContentResult(PartyGuestContentOutcome.UnknownKind);
        }

        var owns = await _db.Parties
            .AsNoTracking()
            .AnyAsync(p => p.Id == partyId && p.OwnerUserId == ownerUserId, cancellationToken);
        if (!owns)
        {
            return new PartyGuestContentResult(PartyGuestContentOutcome.NotFound);
        }

        // Parsed into the shape this kind declares, checked against its stated
        // limits, and re-serialized from the parsed object — so what is stored
        // is what was validated, and an unknown field is dropped rather than
        // kept for some future reader to trip over.
        var validation = PartyGuestContentPayload.Validate(kind, write.Content);
        if (validation.CanonicalJson is not string canonical)
        {
            return new PartyGuestContentResult(
                validation.Error == PartyGuestContentError.UnknownKind
                    ? PartyGuestContentOutcome.UnknownKind
                    : PartyGuestContentOutcome.InvalidPayload);
        }

        var now = _clock.GetUtcNow().UtcDateTime;
        var row = await _db.PartyGuestContents
            .FirstOrDefaultAsync(c => c.PartyId == partyId && c.Kind == kind, cancellationToken);

        if (row is null)
        {
            // Version 0 is how a caller says "there is nothing here yet". Any
            // other number means they think they are editing something that
            // does not exist, which is a stale read like any other.
            if (write.Version != 0)
            {
                return new PartyGuestContentResult(
                    PartyGuestContentOutcome.VersionConflict, Blank(kind));
            }

            row = new PartyGuestContent
            {
                PartyId = partyId,
                Kind = kind,
                // The increment below is what makes it 1 — the first stored
                // version — so "nothing here yet" reads as 0 in both directions.
                Version = 0,
                CreatedAt = now,
            };
            _db.PartyGuestContents.Add(row);
        }
        else if (row.Version != write.Version)
        {
            return new PartyGuestContentResult(
                PartyGuestContentOutcome.VersionConflict, Project(row));
        }

        row.Enabled = write.Enabled;
        row.VisibleBefore = write.VisibleBefore;
        row.VisibleLive = write.VisibleLive;
        row.VisibleAfter = write.VisibleAfter;
        row.ContentJson = canonical;
        row.Version++;
        row.UpdatedAt = now;
        await _db.SaveChangesAsync(cancellationToken);

        return new PartyGuestContentResult(PartyGuestContentOutcome.Ok, Project(row));
    }

    public async Task<IReadOnlyList<PartyGuestContentDto>> ForGuestAsync(
        Guid partyId, PartyGuestPhase phase, CancellationToken cancellationToken = default)
    {
        var rows = await _db.PartyGuestContents
            .AsNoTracking()
            .Where(c => c.PartyId == partyId && c.Enabled)
            .ToListAsync(cancellationToken);

        // Only what belongs to THIS surface, in product order. A slot the host
        // disabled or scoped elsewhere is absent from the guest's context
        // entirely rather than sent with a flag for the client to respect.
        return PartyGuestContentKinds.All
            .Select(kind => rows.FirstOrDefault(r => r.Kind == kind))
            .Where(row => row is not null && row.IsVisibleIn(phase))
            .Select(row => Project(row!))
            .ToList();
    }

    private static PartyGuestContentDto Project(PartyGuestContent row) => new(
        row.Kind, row.Enabled, row.VisibleBefore, row.VisibleLive, row.VisibleAfter,
        JsonDocument.Parse(row.ContentJson).RootElement.Clone(), row.Version);

    private static PartyGuestContentDto Blank(string kind)
    {
        var (before, live, after) = PartyGuestContentKinds.DefaultsFor(kind);
        return new PartyGuestContentDto(
            kind, Enabled: false, before, live, after,
            JsonDocument.Parse(PartyGuestContentPayload.Empty(kind)).RootElement.Clone(),
            Version: 0);
    }
}
