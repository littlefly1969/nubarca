using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using NubArca.Api.Data;
using NubArca.Api.Domain;

namespace NubArca.Api.Party;

/// <summary>
/// The personal invitation, from the guest's side.
///
/// <para><b>What the token is.</b> A capability over ONE invitation group's own
/// invitation and RSVP. It opens the party's public face — title, date, cover,
/// the slots of the current phase — and that group's people, answers and
/// questions. It is not the party's QR: it grants no upload, no game, no print,
/// no greeting and no face search, and no endpoint of those accepts it. It is
/// not an identity either: opening it creates no <c>PartyParticipant</c> and
/// sets no cookie, so the phone that answered for "Mario" and the phone that
/// later scans the room's QR remain two separate facts.</para>
///
/// <para><b>What it is checked against, on every request.</b> The hash of the
/// CURRENT generation (so a rotated or removed link is nothing), the party's
/// phase and windows through the same <see cref="PartyGuestExperience"/> the QR
/// uses (so a Draft is nothing) — narrowed to the FULL experience, so a party
/// reduced to its memories is nothing too — and the host's <c>party.access</c>
/// through the same capability policy (so a host who may no longer run parties
/// has closed their invitations too). All of it collapses to one generic
/// not-found.</para>
/// </summary>
public sealed class PartyRsvpService : IPartyRsvpService
{
    private readonly AppDbContext _db;
    private readonly TimeProvider _clock;
    private readonly IPartyCapabilityPolicy _capabilities;
    private readonly IPartyGuestContentService _content;
    private readonly IPartyMediaService _media;
    private readonly IPartyLinkService _links;

    public PartyRsvpService(
        AppDbContext db,
        TimeProvider clock,
        IPartyCapabilityPolicy capabilities,
        IPartyGuestContentService content,
        IPartyMediaService media,
        IPartyLinkService links)
    {
        _db = db;
        _clock = clock;
        _capabilities = capabilities;
        _content = content;
        _media = media;
        _links = links;
    }

    public static string ContentMediaUrl(string encodedToken, string kind, int version) =>
        $"/api/party-invitations/{encodedToken}/content/{kind}/media?v={version}";

    public async Task<PartyInvitationAccess?> ResolveAsync(
        string token, CancellationToken cancellationToken = default)
    {
        // A derived token is 43 characters. Anything far longer is not one, and
        // is not worth hashing to find out.
        if (string.IsNullOrWhiteSpace(token) || token.Length > 128)
        {
            return null;
        }

        var hash = PartyInvitationTokens.Hash(token);
        var group = await _db.PartyInvitationGroups.AsNoTracking()
            .Where(g => g.TokenHash == hash)
            .Select(g => new { g.Id, g.PartyId })
            .FirstOrDefaultAsync(cancellationToken);
        if (group is null)
        {
            return null;
        }

        var party = await _db.Parties.AsNoTracking()
            .Where(p => p.Id == group.PartyId)
            .Select(p => new { p.OwnerUserId, p.Status, p.GuestAccessExpiresAt, p.LibraryAccessExpiresAt })
            .FirstOrDefaultAsync(cancellationToken);
        if (party is null)
        {
            return null;
        }

        // The SAME policy the party's QR is resolved by — Draft is nothing, a
        // closed window is nothing — with one narrowing of its own: the
        // invitation lives only while the guest experience is FULL. Once all that
        // remains is the memories, the QR still opens the album, but an RSVP link
        // is not a way into the library, and a group's names, notes and answers
        // are not memories. It is the same generic nothing as an unknown token:
        // no "expired" answer tells anybody the link was once real.
        var experience = PartyGuestExperience.Resolve(
            party.Status, party.GuestAccessExpiresAt, party.LibraryAccessExpiresAt,
            _clock.GetUtcNow().UtcDateTime);
        if (experience is not { Access: PartyGuestAccessMode.Full })
        {
            return null;
        }

        var capabilities = await _capabilities.ForOwnerAsync(party.OwnerUserId, cancellationToken);
        if (!capabilities.Access)
        {
            return null;
        }

        return new PartyInvitationAccess(group.Id, group.PartyId, party.OwnerUserId, party.Status, experience);
    }

    public async Task<PartyInvitationViewDto> ViewAsync(
        PartyInvitationAccess access, string token, CancellationToken cancellationToken = default)
    {
        var party = await _db.Parties.AsNoTracking()
            .Where(p => p.Id == access.PartyId)
            .Select(p => new { p.Title, p.Description, p.EventStartsAt, p.Version })
            .FirstAsync(cancellationToken);
        var enc = Uri.EscapeDataString(token);

        // What the party says in THIS phase, by the rule the QR's page uses, with
        // each photograph re-addressed on the invitation's own token.
        var content = (await _content.ForGuestAsync(
                access.PartyId, access.OwnerUserId, access.Experience.Phase, token, cancellationToken))
            .Select(slot => slot with
            {
                MediaUrl = slot.MediaUrl is null ? null : ContentMediaUrl(enc, slot.Kind, slot.Version),
            })
            .ToList();

        var cover = await CoverAsync(access, cancellationToken);
        var coverUrl = cover is { } pick
            ? pick.AlbumId is null
                // The party's version is the cache key: choosing a cover spends one.
                ? $"/api/party-invitations/{enc}/cover/{pick.Which}/media?v={party.Version}"
                : $"/api/party-invitations/{enc}/cover/{pick.Which}/media"
            : null;

        return new PartyInvitationViewDto(
            new PartyInvitationPartyDto(
                party.Title,
                party.Description,
                party.EventStartsAt,
                PartyGuestPhases.Wire(access.Experience.Phase),
                coverUrl,
                content,
                await PublicPartyUrlAsync(access, cancellationToken)),
            await InvitationAsync(access, cancellationToken));
    }

    /// <summary>
    /// "Entra nel Party": the party's own public address, while the party is
    /// live and only if that address really opens it now — judged by the ONE
    /// resolver the QR itself goes through, so a revoked, expired or disabled
    /// link, a host who may no longer run parties, or a party with no QR at all
    /// is simply no button. It hands over nothing the room's QR does not: the
    /// same capability, the same quotas, and a participant minted the way any
    /// browser's is. Nothing here binds this group to whoever follows it.
    /// </summary>
    private async Task<string?> PublicPartyUrlAsync(
        PartyInvitationAccess access, CancellationToken cancellationToken)
    {
        if (!access.CanCheckIn)
        {
            return null;
        }
        var linkId = await _db.PartyAlbumLinks.AsNoTracking()
            .Where(l => l.PartyId == access.PartyId && l.Enabled && l.RevokedAt == null)
            .OrderByDescending(l => l.CreatedAt)
            .Select(l => (Guid?)l.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (linkId is not Guid id)
        {
            return null;
        }
        var publicToken = _links.DeriveViewToken(id);
        return await _links.ResolvePublicAsync(publicToken, cancellationToken) is null
            ? null
            : $"/party/{publicToken}";
    }

    public async Task<(string Which, Guid FileId, Guid? AlbumId)?> CoverAsync(
        PartyInvitationAccess access, CancellationToken cancellationToken = default)
    {
        var covers = await _db.Parties.AsNoTracking()
            .Where(p => p.Id == access.PartyId)
            .Select(p => new { p.InvitationCoverFileItemId, p.LiveCoverFileItemId })
            .FirstOrDefaultAsync(cancellationToken);
        if (covers is null)
        {
            return null;
        }

        // The party's own choice first, by the policy its page uses — so the
        // invitation opens on the same photograph the party's page does.
        var candidates = new[] { covers.InvitationCoverFileItemId, covers.LiveCoverFileItemId }
            .OfType<Guid>().Distinct().ToList();
        if (candidates.Count > 0)
        {
            var eligible = await PartyMediaReference.EligibleAmongAsync(
                _db, access.OwnerUserId, candidates, cancellationToken);
            var chosen = PartyCoverPolicy.Choose(
                access.Experience.AllowsAlbumMedia,
                covers.InvitationCoverFileItemId, covers.LiveCoverFileItemId, eligible);
            if (chosen is { } pick)
            {
                return (pick.Which, pick.FileId, null);
            }
        }

        // Then the album's cover — only the one a host CHOSE, in every phase.
        // This link was never a way into the album, so it never falls back to
        // whichever photograph happens to sort first.
        var albumId = await _db.PartyMediaSources.AsNoTracking()
            .Where(s => s.PartyId == access.PartyId && s.Role == PartyMediaSourceRoles.Main)
            .OrderBy(s => s.SortOrder).ThenBy(s => s.AlbumId)
            .Select(s => (Guid?)s.AlbumId)
            .FirstOrDefaultAsync(cancellationToken);
        if (albumId is not Guid album)
        {
            return null;
        }
        var header = await _media.GetAlbumAsync(access.OwnerUserId, album, cancellationToken);
        return header?.ChosenCoverFileItemId is Guid file ? ("album", file, album) : null;
    }

    private async Task<PartyInvitationRsvpDto> InvitationAsync(
        PartyInvitationAccess access, CancellationToken cancellationToken)
    {
        var group = await _db.PartyInvitationGroups.AsNoTracking()
            .Where(g => g.Id == access.GroupId)
            .Select(g => new { g.Label, g.Version, g.MaxAdditionalGuests })
            .FirstAsync(cancellationToken);

        // Each person's own arrival rides along — and only THIS group's people
        // are in the query, so no other group's arrival can reach this link.
        var guests = await (
            from guest in _db.PartyGuests.AsNoTracking()
            where guest.PartyInvitationGroupId == access.GroupId
            join r in _db.PartyRsvps.AsNoTracking() on guest.Id equals r.PartyGuestId into rsvps
            from r in rsvps.DefaultIfEmpty()
            join a in _db.PartyGuestAttendances.AsNoTracking() on guest.Id equals a.PartyGuestId into arrivals
            from a in arrivals.DefaultIfEmpty()
            orderby guest.IsAdditionalGuest, guest.SortOrder, guest.CreatedAt
            select new PartyInvitationGuestDto(
                guest.Id,
                guest.Name,
                guest.IsAdditionalGuest,
                r == null ? PartyRsvpStatuses.Pending : r.Status,
                r == null ? null : r.DietaryNotes,
                a == null ? null : a.CheckedInAt,
                a == null ? null : a.Source)).ToListAsync(cancellationToken);

        // ACTIVE questions only. An answer to a question the host has since
        // retired stays in the host's history and is not the guest's form.
        var questions = await _db.PartyRsvpQuestions.AsNoTracking()
            .Where(q => q.PartyId == access.PartyId && q.IsActive)
            .OrderBy(q => q.SortOrder).ThenBy(q => q.CreatedAt)
            .ToListAsync(cancellationToken);
        var answers = await _db.PartyRsvpAnswers.AsNoTracking()
            .Where(a => a.PartyInvitationGroupId == access.GroupId)
            .ToDictionaryAsync(a => a.PartyRsvpQuestionId, a => a.ValueJson, cancellationToken);

        return new PartyInvitationRsvpDto(
            group.Label,
            group.Version,
            access.CanRespond,
            group.MaxAdditionalGuests,
            guests.Count(g => g.IsAdditionalGuest),
            guests,
            questions.Select(q => new PartyInvitationQuestionDto(
                q.Id,
                q.Prompt,
                q.Kind,
                q.Required,
                PartyRsvpQuestionRules.ParseOptions(q.OptionsJson),
                answers.TryGetValue(q.Id, out var json)
                    ? JsonDocument.Parse(json).RootElement.Clone()
                    : null)).ToList(),
            access.CanCheckIn);
    }

    public async Task<PartyRsvpResult> SubmitAsync(
        string token, PartyRsvpWrite write, CancellationToken cancellationToken = default)
    {
        var access = await ResolveAsync(token, cancellationToken);
        if (access is null)
        {
            return new(PartyRsvpOutcome.NotFound);
        }
        // Replies are for the announced party. Once it is happening, or over,
        // the invitation still shows what the group said and takes no new
        // answer — a conflict with a state the page can show, not a denial.
        if (!access.CanRespond)
        {
            return new(PartyRsvpOutcome.Closed, await ViewAsync(access, token, cancellationToken), "rsvp_closed");
        }

        var group = await _db.PartyInvitationGroups.AsNoTracking()
            .Where(g => g.Id == access.GroupId)
            .Select(g => new { g.Version, g.MaxAdditionalGuests })
            .FirstAsync(cancellationToken);
        if (write.Version != group.Version)
        {
            return new(
                PartyRsvpOutcome.VersionConflict, await ViewAsync(access, token, cancellationToken), "version_conflict");
        }

        var members = await _db.PartyGuests.AsNoTracking()
            .Where(g => g.PartyInvitationGroupId == access.GroupId)
            .Join(_db.PartyRsvps.AsNoTracking(), g => g.Id, r => r.PartyGuestId,
                (g, r) => new Member(g.Id, g.IsAdditionalGuest, r.Status))
            .ToListAsync(cancellationToken);
        var questions = await _db.PartyRsvpQuestions.AsNoTracking()
            .Where(q => q.PartyId == access.PartyId && q.IsActive)
            .Select(q => new ActiveQuestion(q.Id, q.Kind, q.Required, q.OptionsJson))
            .ToListAsync(cancellationToken);

        // THE WHOLE GRAPH IS VALIDATED BEFORE ANY OF IT IS WRITTEN.
        var plan = Plan(write, members, questions, group.MaxAdditionalGuests, out var error);
        if (plan is null)
        {
            return new(PartyRsvpOutcome.InvalidRequest, Error: error);
        }

        var now = _clock.GetUtcNow().UtcDateTime;
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);

        // ONE version spent, FIRST. On PostgreSQL this takes the group's row
        // lock, so a second reply quoting the same version waits, re-reads the
        // committed row and matches nothing; on SQLite the writer lock does the
        // same. Either way the loser writes nothing — no half of its people, no
        // half of its answers — and is shown the winner's.
        var bumped = await _db.PartyInvitationGroups
            .Where(g => g.Id == access.GroupId && g.Version == write.Version)
            .ExecuteUpdateAsync(s => s
                .SetProperty(g => g.Version, g => g.Version + 1)
                .SetProperty(g => g.UpdatedAt, now), cancellationToken);
        if (bumped == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(
                PartyRsvpOutcome.VersionConflict, await ViewAsync(access, token, cancellationToken), "version_conflict");
        }

        var namedIds = plan.Named.Select(n => n.GuestId).ToList();
        var namedRsvps = await _db.PartyRsvps
            .Where(r => namedIds.Contains(r.PartyGuestId))
            .ToDictionaryAsync(r => r.PartyGuestId, cancellationToken);
        foreach (var named in plan.Named)
        {
            var row = namedRsvps[named.GuestId];
            if (row.Status == named.Status && row.DietaryNotes == named.DietaryNotes)
            {
                continue;
            }
            // The FIRST answer is when they replied; a change of mind is not.
            if (row.Status == PartyRsvpStatuses.Pending && named.Status != PartyRsvpStatuses.Pending)
            {
                row.RespondedAt ??= now;
            }
            row.Status = named.Status;
            row.DietaryNotes = named.DietaryNotes;
            row.UpdatedAt = now;
        }

        // The +1s are the group's own list: kept by id, added without one, and
        // gone — person and answer — when the form no longer names them.
        var keptExtras = plan.Additional.Where(a => a.GuestId is not null).Select(a => a.GuestId!.Value).ToList();
        var droppedExtras = members
            .Where(m => m.IsAdditionalGuest && !keptExtras.Contains(m.Id))
            .Select(m => m.Id)
            .ToList();
        if (droppedExtras.Count > 0)
        {
            // Replies close before arrivals open, so a dropped +1 has none; the
            // statement keeps the restricting key from deciding that for us.
            await _db.PartyGuestAttendances
                .Where(a => droppedExtras.Contains(a.PartyGuestId))
                .ExecuteDeleteAsync(cancellationToken);
            await _db.PartyRsvps.Where(r => droppedExtras.Contains(r.PartyGuestId)).ExecuteDeleteAsync(cancellationToken);
            await _db.PartyGuests.Where(g => droppedExtras.Contains(g.Id)).ExecuteDeleteAsync(cancellationToken);
        }
        var extraGuests = await _db.PartyGuests
            .Where(g => keptExtras.Contains(g.Id))
            .ToDictionaryAsync(g => g.Id, cancellationToken);
        var extraRsvps = await _db.PartyRsvps
            .Where(r => keptExtras.Contains(r.PartyGuestId))
            .ToDictionaryAsync(r => r.PartyGuestId, cancellationToken);
        for (var i = 0; i < plan.Additional.Count; i++)
        {
            var extra = plan.Additional[i];
            if (extra.GuestId is Guid id)
            {
                var guest = extraGuests[id];
                guest.Name = extra.Name;
                guest.SearchText = PartySearchText.ForGuest(extra.Name, guest.Email, guest.Phone);
                guest.SortOrder = i;
                guest.UpdatedAt = now;
                if (extraRsvps.TryGetValue(id, out var rsvp))
                {
                    rsvp.Status = PartyRsvpStatuses.Attending;
                    rsvp.DietaryNotes = extra.DietaryNotes;
                    rsvp.UpdatedAt = now;
                }
                else
                {
                    AddRsvp(id, extra.DietaryNotes, now);
                }
            }
            else
            {
                var newId = Guid.NewGuid();
                _db.PartyGuests.Add(new PartyGuest
                {
                    Id = newId,
                    PartyInvitationGroupId = access.GroupId,
                    Name = extra.Name,
                    SearchText = PartySearchText.ForGuest(extra.Name, null, null),
                    IsAdditionalGuest = true,
                    SortOrder = i,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
                AddRsvp(newId, extra.DietaryNotes, now);
            }
        }

        // Answers to the ACTIVE questions follow the form exactly: given is
        // stored, omitted is cleared. An answer to a retired question is not on
        // the form, so it is left as the host's history.
        var activeIds = questions.Select(q => q.Id).ToList();
        var stored = await _db.PartyRsvpAnswers
            .Where(a => a.PartyInvitationGroupId == access.GroupId && activeIds.Contains(a.PartyRsvpQuestionId))
            .ToListAsync(cancellationToken);
        foreach (var row in stored.Where(s => !plan.Answers.ContainsKey(s.PartyRsvpQuestionId)))
        {
            _db.PartyRsvpAnswers.Remove(row);
        }
        foreach (var (questionId, json) in plan.Answers)
        {
            var row = stored.FirstOrDefault(s => s.PartyRsvpQuestionId == questionId);
            if (row is null)
            {
                _db.PartyRsvpAnswers.Add(new PartyRsvpAnswer
                {
                    PartyInvitationGroupId = access.GroupId,
                    PartyRsvpQuestionId = questionId,
                    ValueJson = json,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
            }
            else if (row.ValueJson != json)
            {
                row.ValueJson = json;
                row.UpdatedAt = now;
            }
        }

        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        _db.ChangeTracker.Clear();

        return new(PartyRsvpOutcome.Ok, await ViewAsync(access, token, cancellationToken));
    }

    private void AddRsvp(Guid guestId, string? dietaryNotes, DateTime now) =>
        _db.PartyRsvps.Add(new PartyRsvp
        {
            PartyGuestId = guestId,
            // A +1 exists because somebody is bringing them.
            Status = PartyRsvpStatuses.Attending,
            DietaryNotes = dietaryNotes,
            RespondedAt = now,
            UpdatedAt = now,
        });

    private sealed record Member(Guid Id, bool IsAdditionalGuest, string Status);

    private sealed record ActiveQuestion(Guid Id, string Kind, bool Required, string? OptionsJson);

    private sealed record NamedPlan(Guid GuestId, string Status, string? DietaryNotes);

    private sealed record AdditionalPlan(Guid? GuestId, string Name, string? DietaryNotes);

    private sealed record RsvpPlan(
        IReadOnlyList<NamedPlan> Named,
        IReadOnlyList<AdditionalPlan> Additional,
        IReadOnlyDictionary<Guid, string> Answers);

    /// <summary>
    /// The whole reply, checked against the group as it is. Every named guest
    /// exactly once; a known status, and never back to <c>pending</c> once
    /// answered; +1s within the allowance, named, and only alongside somebody
    /// who is coming; answers only to this party's active questions, each of
    /// the kind it asks.
    ///
    /// <para><b>Required means required of a group that is coming.</b> A family
    /// declining the invitation is not asked which menu it will not eat; a group
    /// with at least one person attending must answer every required
    /// question.</para>
    /// </summary>
    private static RsvpPlan? Plan(
        PartyRsvpWrite write,
        IReadOnlyList<Member> members,
        IReadOnlyList<ActiveQuestion> questions,
        int maxAdditionalGuests,
        out string? error)
    {
        var named = members.Where(m => !m.IsAdditionalGuest).ToDictionary(m => m.Id);
        var extras = members.Where(m => m.IsAdditionalGuest).Select(m => m.Id).ToHashSet();

        var guests = write.Guests ?? [];
        if (guests.Count != named.Count
            || guests.Select(g => g.GuestId).Distinct().Count() != guests.Count
            || guests.Any(g => !named.ContainsKey(g.GuestId)))
        {
            error = "invalid_guests";
            return null;
        }

        var namedPlan = new List<NamedPlan>(guests.Count);
        foreach (var guest in guests)
        {
            if (!PartyRsvpStatuses.IsKnown(guest.Status)
                || (guest.Status == PartyRsvpStatuses.Pending
                    && named[guest.GuestId].Status != PartyRsvpStatuses.Pending))
            {
                error = "invalid_status";
                return null;
            }
            var notes = PartyInvitationLimits.Normalize(guest.DietaryNotes);
            if (!PartyInvitationLimits.Fits(notes, PartyInvitationLimits.MaxDietaryNotesLength))
            {
                error = "invalid_dietary_notes";
                return null;
            }
            namedPlan.Add(new NamedPlan(guest.GuestId, guest.Status!, notes));
        }
        var anyoneComing = namedPlan.Any(n => n.Status == PartyRsvpStatuses.Attending);

        var additional = write.AdditionalGuests ?? [];
        if (additional.Count > maxAdditionalGuests)
        {
            error = "too_many_additional_guests";
            return null;
        }
        if (additional.Count > 0 && !anyoneComing)
        {
            error = "additional_guests_need_attendee";
            return null;
        }
        var extraIds = additional.Where(a => a.GuestId is not null).Select(a => a.GuestId!.Value).ToList();
        if (extraIds.Distinct().Count() != extraIds.Count || extraIds.Any(id => !extras.Contains(id)))
        {
            error = "invalid_additional_guest";
            return null;
        }
        var additionalPlan = new List<AdditionalPlan>(additional.Count);
        foreach (var extra in additional)
        {
            var name = PartyInvitationLimits.Normalize(extra.Name);
            var notes = PartyInvitationLimits.Normalize(extra.DietaryNotes);
            if (name is null || !PartyInvitationLimits.Fits(name, PartyInvitationLimits.MaxGuestNameLength))
            {
                error = "invalid_additional_guest";
                return null;
            }
            if (!PartyInvitationLimits.Fits(notes, PartyInvitationLimits.MaxDietaryNotesLength))
            {
                error = "invalid_dietary_notes";
                return null;
            }
            additionalPlan.Add(new AdditionalPlan(extra.GuestId, name, notes));
        }

        var byId = questions.ToDictionary(q => q.Id);
        var seen = new HashSet<Guid>();
        var answers = new Dictionary<Guid, string>();
        foreach (var answer in write.Answers ?? [])
        {
            if (!byId.TryGetValue(answer.QuestionId, out var question) || !seen.Add(answer.QuestionId))
            {
                error = "unknown_question";
                return null;
            }
            if (!PartyRsvpQuestionRules.TryCanonicalAnswer(
                    question.Kind, PartyRsvpQuestionRules.ParseOptions(question.OptionsJson),
                    answer.Value, out var canonical))
            {
                error = "invalid_answer";
                return null;
            }
            if (canonical is not null)
            {
                answers[answer.QuestionId] = canonical;
            }
        }
        if (anyoneComing && questions.Any(q => q.Required && !answers.ContainsKey(q.Id)))
        {
            error = "required_answer_missing";
            return null;
        }

        error = null;
        return new RsvpPlan(namedPlan, additionalPlan, answers);
    }
}
