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

    /// <summary>
    /// The guest's address for one slot's photograph. The slot's version is in
    /// it because the bytes are cached privately for a day: a host who replaces
    /// the menu photo must not leave the room looking at the old one.
    /// </summary>
    public static string MediaUrl(string token, string kind, int version) =>
        $"/api/party/{Uri.EscapeDataString(token)}/content/{kind}/media?v={version}";

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
        var eligible = await PartyMediaReference.EligibleAmongAsync(
            _db, ownerUserId, MediaIds(stored.Values), cancellationToken);

        // EVERY kind, in product order — the ones the host has written and the
        // ones they have not. A slot they have never touched arrives at version
        // 0 with the product's default visibility, which is what makes those
        // defaults the server's answer rather than the editor's guess.
        return PartyGuestContentKinds.All
            .Select(kind => stored.TryGetValue(kind, out var row) ? Project(row, eligible) : Blank(kind))
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

        // The presentation is structural, so it is checked before anything is
        // written and independently of the payload. "poster" without a
        // photograph is refused rather than stored and rendered as nothing: a
        // poster slot IS its picture, and the guest surface has no composition
        // to fall back to. The frontend clears the presentation when the host
        // removes the image; this is what makes that a rule rather than a habit.
        if (!PartyGuestContentMediaPresentations.IsKnown(write.MediaPresentation)
            || (write.MediaPresentation == PartyGuestContentMediaPresentations.Poster
                && write.MediaFileItemId is null))
        {
            return new PartyGuestContentResult(PartyGuestContentOutcome.InvalidPresentation);
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
        }
        else if (row.Version != write.Version)
        {
            return new PartyGuestContentResult(
                PartyGuestContentOutcome.VersionConflict,
                await ProjectAsync(ownerUserId, row, cancellationToken));
        }

        // A NEW photograph must be one this owner may put on a party — and album
        // membership is deliberately not part of the question: the menu's photo
        // may be in the album, or may be a graphic that never will be, and
        // choosing it files it nowhere. A reference the slot ALREADY holds is not
        // re-judged here, so a host whose photo went to Trash can still fix a
        // typo in the menu; whether it is served is decided again on every guest
        // request anyway.
        if (write.MediaFileItemId is Guid mediaId
            && mediaId != row?.MediaFileItemId
            && !await PartyMediaReference.IsEligibleAsync(_db, ownerUserId, mediaId, cancellationToken))
        {
            return new PartyGuestContentResult(PartyGuestContentOutcome.InvalidMedia);
        }

        if (row is null)
        {
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

        row.Enabled = write.Enabled;
        row.VisibleBefore = write.VisibleBefore;
        row.VisibleLive = write.VisibleLive;
        row.VisibleAfter = write.VisibleAfter;
        row.ContentJson = canonical;
        row.MediaFileItemId = write.MediaFileItemId;
        // Changing HOW the photograph is presented is an edit of the slot like
        // any other, so it moves the slot's own version — never the party's.
        row.MediaPresentation = write.MediaPresentation;
        row.Version++;
        row.UpdatedAt = now;
        await _db.SaveChangesAsync(cancellationToken);

        return new PartyGuestContentResult(
            PartyGuestContentOutcome.Ok, await ProjectAsync(ownerUserId, row, cancellationToken));
    }

    public async Task<IReadOnlyList<PartyGuestContentViewDto>> ForGuestAsync(
        Guid partyId,
        Guid ownerUserId,
        PartyGuestPhase phase,
        string token,
        CancellationToken cancellationToken = default)
    {
        var rows = await _db.PartyGuestContents
            .AsNoTracking()
            .Where(c => c.PartyId == partyId && c.Enabled)
            .ToListAsync(cancellationToken);

        // Only what belongs to THIS surface, in product order. A slot the host
        // disabled or scoped elsewhere is absent from the guest's context
        // entirely rather than sent with a flag for the client to respect.
        var visible = PartyGuestContentKinds.All
            .Select(kind => rows.FirstOrDefault(r => r.Kind == kind))
            .OfType<PartyGuestContent>()
            .Where(row => row.IsVisibleIn(phase))
            .ToList();

        // A photograph is offered only while its file still qualifies. One that
        // went to Trash or into the Private Vault is simply not offered, so the
        // page renders the words without a broken frame.
        var eligible = await PartyMediaReference.EligibleAmongAsync(
            _db, ownerUserId, MediaIds(visible), cancellationToken);

        return visible
            .Select(row => new PartyGuestContentViewDto(
                row.Kind, row.Enabled, row.VisibleBefore, row.VisibleLive, row.VisibleAfter,
                Parse(row.ContentJson), row.Version,
                row.MediaFileItemId is Guid id && eligible.Contains(id)
                    ? MediaUrl(token, row.Kind, row.Version)
                    : null,
                row.MediaPresentation))
            .ToList();
    }

    public async Task<Guid?> GuestMediaFileAsync(
        Guid partyId,
        Guid ownerUserId,
        PartyGuestPhase phase,
        string kind,
        CancellationToken cancellationToken = default)
    {
        if (!PartyGuestContentKinds.IsKnown(kind))
        {
            return null;
        }

        var row = await _db.PartyGuestContents
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.PartyId == partyId && c.Kind == kind, cancellationToken);

        // The same rule the guest context is built by, asked again for the
        // bytes: a slot that is not on this surface has no photograph here, and
        // the reference must still qualify at the moment it is served.
        if (row?.MediaFileItemId is not Guid mediaId || !row.IsVisibleIn(phase))
        {
            return null;
        }

        return await PartyMediaReference.IsEligibleAsync(_db, ownerUserId, mediaId, cancellationToken)
            ? mediaId
            : null;
    }

    private async Task<PartyGuestContentDto> ProjectAsync(
        Guid ownerUserId, PartyGuestContent row, CancellationToken cancellationToken)
    {
        var eligible = await PartyMediaReference.EligibleAmongAsync(
            _db, ownerUserId, MediaIds([row]), cancellationToken);
        return Project(row, eligible);
    }

    private static PartyGuestContentDto Project(PartyGuestContent row, IReadOnlySet<Guid> eligible) => new(
        row.Kind, row.Enabled, row.VisibleBefore, row.VisibleLive, row.VisibleAfter,
        Parse(row.ContentJson), row.Version, row.MediaFileItemId,
        row.MediaFileItemId is Guid id && eligible.Contains(id)
            ? $"/api/files/{id}/thumbnail?size=medium"
            : null,
        row.MediaPresentation);

    private static PartyGuestContentDto Blank(string kind)
    {
        var (before, live, after) = PartyGuestContentKinds.DefaultsFor(kind);
        return new PartyGuestContentDto(
            kind, Enabled: false, before, live, after,
            Parse(PartyGuestContentPayload.Empty(kind)),
            Version: 0,
            MediaPresentation: PartyGuestContentMediaPresentations.Inline);
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static List<Guid> MediaIds(IEnumerable<PartyGuestContent> rows) =>
        rows.Where(r => r.MediaFileItemId is not null)
            .Select(r => r.MediaFileItemId!.Value)
            .Distinct()
            .ToList();
}
