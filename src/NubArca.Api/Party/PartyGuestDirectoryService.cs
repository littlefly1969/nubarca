using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NubArca.Api.Auth.Recovery;
using NubArca.Api.Data;
using NubArca.Api.Domain;

namespace NubArca.Api.Party;

/// <summary>
/// The host's guest console, read one page at a time.
///
/// <para><b>Everything that decides WHICH rows is done by the database.</b> The
/// search is a substring test on each row's folded <c>SearchText</c> (see
/// <see cref="PartySearchText"/>); every filter is an <c>EXISTS</c> over the
/// group's own people, their answers, their arrivals or its deliveries; the page
/// is a keyset <c>LIMIT</c>. A page costs the same handful of statements for a
/// party of ten groups as for one of a thousand — the other arrivals, the
/// groups, then the page's people and deliveries by id — and the counts, asked
/// for on the first page only, are aggregates.</para>
///
/// <para><b>The order is stable under concurrent writes.</b> Other arrivals come
/// first, latest first by (<c>CheckedInAt</c>, <c>Id</c>); then groups by
/// (<c>lower(Label)</c>, <c>Id</c>). The cursor carries the last item's own sort
/// key, not a position, so a row added, renamed or removed meanwhile never makes
/// a page repeat or skip the rows around it — which an offset would. The key is
/// the database's own <c>lower()</c>, read back from it, so the comparison and
/// the order agree on every provider.</para>
///
/// <para><b>The cursor is opaque and sealed.</b> It holds a label, so it is
/// encrypted with the host's data-protection keys rather than merely encoded —
/// a URL in an access log must not spell a guest's name — and it is bound to a
/// hash of the party, the search and the filter it was issued for: replayed
/// against anything else, it is refused, never reinterpreted.</para>
///
/// <para>Owner-scoped at the database boundary like every guest-list read: the
/// party is matched on <c>OwnerUserId</c>, and a group on both its own id and
/// that party's.</para>
/// </summary>
public sealed class PartyGuestDirectoryService : IPartyGuestDirectoryService
{
    private const int HistoryLength = 20;

    private readonly AppDbContext _db;
    private readonly IEmailSender _email;
    private readonly IOptionsMonitor<MailOptions> _mail;
    private readonly IDataProtector _cursors;

    public PartyGuestDirectoryService(
        AppDbContext db,
        IEmailSender email,
        IOptionsMonitor<MailOptions> mail,
        IDataProtectionProvider dataProtection)
    {
        _db = db;
        _email = email;
        _mail = mail;
        _cursors = dataProtection.CreateProtector("NubArca.Party.GuestDirectory.Cursor.v1");
    }

    public async Task<PartyGuestDirectoryResult> PageAsync(
        Guid ownerUserId, Guid partyId, PartyGuestDirectoryQuery query, CancellationToken cancellationToken = default)
    {
        var status = await OwnedPartyStatusAsync(ownerUserId, partyId, cancellationToken);
        if (status is null) return new(PartyGuestDirectoryOutcome.NotFound);

        var state = string.IsNullOrEmpty(query.State) ? PartyGuestDirectoryStates.All : query.State;
        if (!PartyGuestDirectoryStates.IsKnown(state)) return Invalid("invalid_state");
        var take = query.Take ?? PartyGuestDirectoryLimits.DefaultTake;
        if (take is < 0 or > PartyGuestDirectoryLimits.MaxTake) return Invalid("invalid_take");
        var text = query.Q?.Trim();
        if (text is not null && PartyInvitationLimits.CodePoints(text) > PartySearchText.MaxQueryLength)
        {
            return Invalid("invalid_query");
        }
        var needle = PartySearchText.Needle(text);
        var fingerprint = Fingerprint(partyId, needle, state);
        Anchor? anchor = null;
        if (!string.IsNullOrEmpty(query.Cursor))
        {
            anchor = Decode(query.Cursor, fingerprint);
            if (anchor is null) return Invalid("invalid_cursor");
        }

        var items = new List<PartyGuestDirectoryItemDto>();
        Anchor? next = null;

        // take=0 asks for the counts alone.
        if (take == 0)
        {
            return new(PartyGuestDirectoryOutcome.Ok, new PartyGuestDirectoryPageDto(
                partyId,
                status,
                PartyInvitationService.IsMailAvailable(_email, _mail.CurrentValue),
                PartyInvitationService.IsShareAvailable(_mail.CurrentValue),
                await SummaryAsync(partyId, cancellationToken),
                items,
                null));
        }

        // Section one: the other arrivals, unless the cursor has moved past them.
        if (anchor?.Section != Anchor.Groups && PartyGuestDirectoryStates.IncludesOtherArrivals(state))
        {
            var others = OtherArrivals(partyId, needle);
            if (anchor is { Section: Anchor.Others, At: DateTime at, Id: Guid after })
            {
                others = others.Where(o => o.CheckedInAt < at || (o.CheckedInAt == at && o.Id.CompareTo(after) > 0));
            }
            var rows = await others
                .OrderByDescending(o => o.CheckedInAt).ThenBy(o => o.Id)
                .Take(take + 1)
                .Select(o => new PartyGuestDirectoryOtherItemDto(o.Id, o.Name, o.CheckedInAt, o.Version))
                .ToListAsync(cancellationToken);
            if (rows.Count > take)
            {
                rows.RemoveAt(take);
                next = new Anchor(Anchor.Others, null, rows[^1].CheckedInAt, rows[^1].Id);
            }
            items.AddRange(rows);
        }

        // Section two: the groups, when the page still has room.
        if (next is null)
        {
            var room = take - items.Count;
            var groups = Groups(partyId, needle, state);
            if (anchor is { Section: Anchor.Groups, Key: string key, Id: Guid after })
            {
                groups = groups.Where(g =>
                    g.Label.ToLower().CompareTo(key) > 0 || (g.Label.ToLower() == key && g.Id.CompareTo(after) > 0));
            }
            // One more than there is room for — even when there is none — so
            // "is there a next page" is always answered by the rows themselves.
            var rows = await groups
                .OrderBy(g => g.Label.ToLower()).ThenBy(g => g.Id)
                .Take(room + 1)
                .Select(g => new GroupRow(
                    g.Id, g.Label, g.Label.ToLower(), g.Version, g.MaxAdditionalGuests, g.CapabilityId, g.Phone))
                .ToListAsync(cancellationToken);
            if (rows.Count > room)
            {
                rows.RemoveAt(room);
                next = rows.Count > 0
                    ? new Anchor(Anchor.Groups, rows[^1].SortKey, null, rows[^1].Id)
                    : new Anchor(Anchor.Groups, null, null, null);
            }
            items.AddRange(await ItemsAsync(rows, status, needle, cancellationToken));
        }

        return new(PartyGuestDirectoryOutcome.Ok, new PartyGuestDirectoryPageDto(
            partyId,
            status,
            PartyInvitationService.IsMailAvailable(_email, _mail.CurrentValue),
            PartyInvitationService.IsShareAvailable(_mail.CurrentValue),
            anchor is null ? await SummaryAsync(partyId, cancellationToken) : null,
            items,
            next is null ? null : Encode(next, fingerprint)));
    }

    public async Task<PartyInvitationGroupDetailDto?> GroupAsync(
        Guid ownerUserId, Guid partyId, Guid groupId, CancellationToken cancellationToken = default)
    {
        var status = await OwnedPartyStatusAsync(ownerUserId, partyId, cancellationToken);
        if (status is null) return null;
        var selected = _db.PartyInvitationGroups.AsNoTracking().Where(g => g.Id == groupId && g.PartyId == partyId);
        var mail = PartyInvitationService.IsMailAvailable(_email, _mail.CurrentValue);
        var share = PartyInvitationService.IsShareAvailable(_mail.CurrentValue);
        var group = (await PartyInvitationService.ProjectGroupsAsync(
            _db, selected, status, mail, share, cancellationToken)).SingleOrDefault();
        if (group is null) return null;

        var capabilityId = await selected.Select(g => g.CapabilityId).SingleAsync(cancellationToken);
        var arrivals = await (
            from a in _db.PartyGuestAttendances.AsNoTracking()
            join p in _db.PartyGuests.AsNoTracking() on a.PartyGuestId equals p.Id
            where p.PartyInvitationGroupId == groupId
            select new PartyGuestArrivalDto(a.PartyGuestId, a.CheckedInAt, a.Source)).ToListAsync(cancellationToken);
        var history = await _db.PartyInvitationDeliveries.AsNoTracking()
            .Where(d => d.PartyInvitationGroupId == groupId)
            .OrderByDescending(d => d.CreatedAt).ThenByDescending(d => d.Id)
            .Take(HistoryLength)
            .Select(d => new PartyInvitationHistoryEntryDto(
                d.Channel, d.Kind, d.Status, d.CreatedAt, d.CompletedAt, d.CapabilityId == capabilityId))
            .ToListAsync(cancellationToken);
        var questions = await PartyInvitationService.ProjectQuestionsAsync(
            _db,
            _db.PartyRsvpQuestions.AsNoTracking().Where(q => q.PartyId == partyId
                && _db.PartyRsvpAnswers.Any(a => a.PartyInvitationGroupId == groupId && a.PartyRsvpQuestionId == q.Id)),
            cancellationToken);
        var item = await ItemAsync(partyId, status, groupId, cancellationToken);

        return new PartyInvitationGroupDetailDto(
            partyId,
            status,
            mail,
            share,
            group,
            PartyWhatsAppLink.InternationalNumber(group.Phone) is not null,
            arrivals,
            history,
            questions,
            item!,
            await SummaryAsync(partyId, cancellationToken));
    }

    public async Task<IReadOnlyList<PartyRsvpQuestionDto>?> QuestionsAsync(
        Guid ownerUserId, Guid partyId, CancellationToken cancellationToken = default)
    {
        var status = await OwnedPartyStatusAsync(ownerUserId, partyId, cancellationToken);
        return status is null
            ? null
            : await PartyInvitationService.ProjectQuestionsAsync(
                _db, _db.PartyRsvpQuestions.AsNoTracking().Where(q => q.PartyId == partyId), cancellationToken);
    }

    public async Task<PartyGuestDirectoryGroupItemDto?> ItemAsync(
        Guid partyId, string partyStatus, Guid groupId, CancellationToken cancellationToken = default)
    {
        var rows = await _db.PartyInvitationGroups.AsNoTracking()
            .Where(g => g.Id == groupId && g.PartyId == partyId)
            .Select(g => new GroupRow(
                g.Id, g.Label, g.Label.ToLower(), g.Version, g.MaxAdditionalGuests, g.CapabilityId, g.Phone))
            .ToListAsync(cancellationToken);
        return (await ItemsAsync(rows, partyStatus, null, cancellationToken)).SingleOrDefault();
    }

    // --- What a page selects ----------------------------------------------------------

    private IQueryable<PartyAttendanceGuest> OtherArrivals(Guid partyId, SearchNeedle? needle)
    {
        var others = _db.PartyAttendanceGuests.AsNoTracking().Where(o => o.PartyId == partyId);
        if (needle is null) return others;
        var (text, digits) = (needle.Text, needle.Digits ?? needle.Text);
        return others.Where(o => o.SearchText.Contains(text) || o.SearchText.Contains(digits));
    }

    /// <summary>
    /// The party's groups that match the search and the filter. Each filter is
    /// a rule about the group's PEOPLE — "at least one of them …" — except
    /// <c>not_invited</c>, which is about its link. A person with no RSVP row is
    /// pending, exactly as every projection reads them.
    /// </summary>
    private IQueryable<PartyInvitationGroup> Groups(Guid partyId, SearchNeedle? needle, string state)
    {
        var groups = _db.PartyInvitationGroups.AsNoTracking().Where(g => g.PartyId == partyId);
        var people = _db.PartyGuests.AsNoTracking();
        var rsvps = _db.PartyRsvps.AsNoTracking();
        var arrivals = _db.PartyGuestAttendances.AsNoTracking();

        if (needle is not null)
        {
            var (text, digits) = (needle.Text, needle.Digits ?? needle.Text);
            groups = groups.Where(g =>
                g.SearchText.Contains(text) || g.SearchText.Contains(digits)
                || people.Any(p => p.PartyInvitationGroupId == g.Id
                    && (p.SearchText.Contains(text) || p.SearchText.Contains(digits))));
        }

        return state switch
        {
            PartyGuestDirectoryStates.Pending => groups.Where(g => people.Any(p =>
                p.PartyInvitationGroupId == g.Id && !p.IsAdditionalGuest
                && !rsvps.Any(r => r.PartyGuestId == p.Id && r.Status != PartyRsvpStatuses.Pending))),
            PartyGuestDirectoryStates.Attending => groups.Where(g => people.Any(p =>
                p.PartyInvitationGroupId == g.Id
                && rsvps.Any(r => r.PartyGuestId == p.Id && r.Status == PartyRsvpStatuses.Attending))),
            PartyGuestDirectoryStates.Declined => groups.Where(g => people.Any(p =>
                p.PartyInvitationGroupId == g.Id && !p.IsAdditionalGuest
                && rsvps.Any(r => r.PartyGuestId == p.Id && r.Status == PartyRsvpStatuses.Declined))),
            PartyGuestDirectoryStates.NotInvited => groups.Where(g => !_db.PartyInvitationDeliveries.Any(d =>
                d.PartyInvitationGroupId == g.Id
                && d.CapabilityId == g.CapabilityId
                && d.Kind != PartyInvitationDeliveryKinds.Reminder
                && (d.Status == PartyInvitationDeliveryStatuses.Sent
                    || d.Status == PartyInvitationDeliveryStatuses.Shared))),
            PartyGuestDirectoryStates.ToArrive => groups.Where(g => people.Any(p =>
                p.PartyInvitationGroupId == g.Id
                && rsvps.Any(r => r.PartyGuestId == p.Id && r.Status == PartyRsvpStatuses.Attending)
                && !arrivals.Any(a => a.PartyGuestId == p.Id))),
            PartyGuestDirectoryStates.Arrived => groups.Where(g => people.Any(p =>
                p.PartyInvitationGroupId == g.Id && arrivals.Any(a => a.PartyGuestId == p.Id))),
            PartyGuestDirectoryStates.Unexpected => groups.Where(g => people.Any(p =>
                p.PartyInvitationGroupId == g.Id
                && arrivals.Any(a => a.PartyGuestId == p.Id)
                && !rsvps.Any(r => r.PartyGuestId == p.Id && r.Status == PartyRsvpStatuses.Attending))),
            _ => groups,
        };
    }

    private sealed record GroupRow(
        Guid Id, string Label, string SortKey, int Version, int MaxAdditionalGuests, Guid CapabilityId, string? Phone);

    /// <summary>
    /// The cards for a page's groups: two statements by id, whatever the page
    /// size — their people (with answers and arrivals) and their current
    /// link's deliveries.
    /// </summary>
    private async Task<List<PartyGuestDirectoryGroupItemDto>> ItemsAsync(
        IReadOnlyList<GroupRow> rows, string partyStatus, SearchNeedle? needle, CancellationToken cancellationToken)
    {
        if (rows.Count == 0) return [];
        var ids = rows.Select(r => r.Id).ToList();

        var people = await (
            from p in _db.PartyGuests.AsNoTracking()
            where ids.Contains(p.PartyInvitationGroupId)
            join r in _db.PartyRsvps.AsNoTracking() on p.Id equals r.PartyGuestId into rsvps
            from r in rsvps.DefaultIfEmpty()
            join a in _db.PartyGuestAttendances.AsNoTracking() on p.Id equals a.PartyGuestId into arrivals
            from a in arrivals.DefaultIfEmpty()
            select new
            {
                p.Id,
                p.PartyInvitationGroupId,
                p.Name,
                p.IsAdditionalGuest,
                p.SortOrder,
                p.CreatedAt,
                p.SearchText,
                Status = r == null ? PartyRsvpStatuses.Pending : r.Status,
                CheckedInAt = a == null ? (DateTime?)null : a.CheckedInAt,
                Source = a == null ? null : a.Source,
            }).ToListAsync(cancellationToken);
        var peopleByGroup = people.ToLookup(p => p.PartyInvitationGroupId);

        var deliveries = await (
            from d in _db.PartyInvitationDeliveries.AsNoTracking()
            join g in _db.PartyInvitationGroups.AsNoTracking() on d.PartyInvitationGroupId equals g.Id
            where ids.Contains(g.Id) && d.CapabilityId == g.CapabilityId
            select d).ToListAsync(cancellationToken);
        var deliveriesByGroup = deliveries.ToLookup(d => d.PartyInvitationGroupId);

        var mail = PartyInvitationService.IsMailAvailable(_email, _mail.CurrentValue);
        var share = PartyInvitationService.IsShareAvailable(_mail.CurrentValue);
        var open = PartyInvitationPolicy.TakesInvitations(partyStatus);
        bool Matched(string searchText) =>
            needle is not null
            && (searchText.Contains(needle.Text, StringComparison.Ordinal)
                || (needle.Digits is not null && searchText.Contains(needle.Digits, StringComparison.Ordinal)));

        return rows.Select(row =>
        {
            var members = peopleByGroup[row.Id]
                .OrderBy(p => p.IsAdditionalGuest).ThenBy(p => p.SortOrder).ThenBy(p => p.CreatedAt)
                .ToList();
            var named = members.Where(p => !p.IsAdditionalGuest).ToList();
            var pending = named.Count(p => p.Status == PartyRsvpStatuses.Pending);
            var invitation = PartyInvitationService.DeliveryState(deliveriesByGroup[row.Id], row.CapabilityId, out var invited);
            return new PartyGuestDirectoryGroupItemDto(
                row.Id,
                row.Label,
                row.Version,
                row.MaxAdditionalGuests,
                members.Count(p => p.IsAdditionalGuest),
                members.Select(p => new PartyGuestDirectoryPersonDto(
                    p.Id, p.Name, p.IsAdditionalGuest, p.Status, p.CheckedInAt, p.Source, Matched(p.SearchText))).ToList(),
                new PartyGuestDirectoryCountsDto(
                    Attending: members.Count(p => p.Status == PartyRsvpStatuses.Attending),
                    Pending: pending,
                    Declined: named.Count(p => p.Status == PartyRsvpStatuses.Declined),
                    Arrived: members.Count(p => p.CheckedInAt is not null)),
                invitation,
                PartyWhatsAppLink.InternationalNumber(row.Phone) is not null,
                CanSend: mail && open,
                CanRemind: PartyInvitationService.CanRemind(mail, partyStatus, invited, pending),
                CanShare: share && open);
        }).ToList();
    }

    /// <summary>
    /// The whole party's counts in four aggregate statements, fed to the ONE
    /// definition of each: <see cref="PartyInvitationService.Summarize"/> for
    /// what people declared, <see cref="PartyAttendanceService.Summarize"/> for
    /// who arrived.
    /// </summary>
    private async Task<PartyGuestDirectorySummaryDto> SummaryAsync(Guid partyId, CancellationToken cancellationToken)
    {
        var groupsOfParty = _db.PartyInvitationGroups.AsNoTracking().Where(g => g.PartyId == partyId);
        var rsvps = _db.PartyRsvps.AsNoTracking();

        var people = await (
            from p in _db.PartyGuests.AsNoTracking()
            join g in groupsOfParty on p.PartyInvitationGroupId equals g.Id
            join r in rsvps on p.Id equals r.PartyGuestId into rs
            from r in rs.DefaultIfEmpty()
            join a in _db.PartyGuestAttendances.AsNoTracking() on p.Id equals a.PartyGuestId into ar
            from a in ar.DefaultIfEmpty()
            group p by new
            {
                p.IsAdditionalGuest,
                Status = r == null ? PartyRsvpStatuses.Pending : r.Status,
                Arrived = a != null,
            } into bucket
            select new { bucket.Key.IsAdditionalGuest, bucket.Key.Status, bucket.Key.Arrived, Count = bucket.Count() })
            .ToListAsync(cancellationToken);

        var groups = await groupsOfParty.CountAsync(cancellationToken);
        var unanswered = await groupsOfParty.CountAsync(g => _db.PartyGuests.Any(p =>
            p.PartyInvitationGroupId == g.Id && !p.IsAdditionalGuest
            && !rsvps.Any(r => r.PartyGuestId == p.Id && r.Status != PartyRsvpStatuses.Pending)), cancellationToken);
        var others = await _db.PartyAttendanceGuests.CountAsync(o => o.PartyId == partyId, cancellationToken);
        // THE FILTER COUNTS ITSELF. The console makes every number a filter, so
        // the "not invited" tile needs a number — the named guests of the groups
        // that same query lists, never a second definition of "not invited"
        // that could drift away from the first. People, not groups, because
        // every other number beside it counts people.
        var notInvitedGroups = Groups(partyId, null, PartyGuestDirectoryStates.NotInvited);
        var notInvited = await _db.PartyGuests.AsNoTracking().CountAsync(p =>
            !p.IsAdditionalGuest && notInvitedGroups.Any(g => g.Id == p.PartyInvitationGroupId), cancellationToken);

        return new PartyGuestDirectorySummaryDto(
            groups,
            others,
            PartyInvitationService.Summarize(
                groups, people.Select(b => (b.IsAdditionalGuest, b.Status, b.Count)), unanswered),
            PartyAttendanceService.Summarize(
                people.SelectMany(b => Enumerable.Repeat((b.Status, b.Arrived), b.Count)), others),
            notInvited);
    }

    // --- The cursor ---------------------------------------------------------------------

    /// <summary>
    /// Where the previous page stopped: in the other arrivals at (<c>At</c>,
    /// <c>Id</c>), or in the groups at (<c>Key</c>, <c>Id</c>) — or at the start
    /// of the groups, when the other arrivals ended exactly with that page.
    /// </summary>
    private sealed record Anchor(string Section, string? Key, DateTime? At, Guid? Id)
    {
        public const string Others = "o";
        public const string Groups = "g";
    }

    private sealed record SealedAnchor(string F, string S, string? K, long? T, Guid? I);

    private string Encode(Anchor anchor, string fingerprint) =>
        _cursors.Protect(JsonSerializer.Serialize(
            new SealedAnchor(fingerprint, anchor.Section, anchor.Key, anchor.At?.Ticks, anchor.Id)));

    private Anchor? Decode(string cursor, string fingerprint)
    {
        SealedAnchor? sealedAnchor;
        try
        {
            sealedAnchor = JsonSerializer.Deserialize<SealedAnchor>(_cursors.Unprotect(cursor));
        }
        catch (Exception e) when (e is CryptographicException or JsonException or FormatException)
        {
            return null;
        }
        if (sealedAnchor is null || sealedAnchor.F != fingerprint) return null;
        return sealedAnchor switch
        {
            { S: Anchor.Others, T: long ticks, I: Guid id } =>
                new Anchor(Anchor.Others, null, new DateTime(ticks, DateTimeKind.Utc), id),
            { S: Anchor.Groups, K: string key, I: Guid id } => new Anchor(Anchor.Groups, key, null, id),
            { S: Anchor.Groups, K: null, I: null } => new Anchor(Anchor.Groups, null, null, null),
            _ => null,
        };
    }

    /// <summary>What a cursor is valid for — hashed, so the search itself never enters it.</summary>
    private static string Fingerprint(Guid partyId, SearchNeedle? needle, string state) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{partyId:N}\n{needle?.Text}\n{needle?.Digits}\n{state}")), 0, 12);

    // --- Helpers ------------------------------------------------------------------------

    private static PartyGuestDirectoryResult Invalid(string error) =>
        new(PartyGuestDirectoryOutcome.InvalidRequest, Error: error);

    private async Task<string?> OwnedPartyStatusAsync(
        Guid ownerUserId, Guid partyId, CancellationToken cancellationToken) =>
        await _db.Parties.AsNoTracking()
            .Where(p => p.Id == partyId && p.OwnerUserId == ownerUserId)
            .Select(p => p.Status)
            .FirstOrDefaultAsync(cancellationToken);
}
