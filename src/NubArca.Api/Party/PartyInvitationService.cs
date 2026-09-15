using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Text.Json;
using NubArca.Api.Auth.Recovery;
using NubArca.Api.Data;
using NubArca.Api.Domain;

namespace NubArca.Api.Party;

/// <summary>
/// The host's guest list.
///
/// <para>Every query is owner-scoped at the database boundary: the party is
/// matched on <c>OwnerUserId</c> first, and a group or question is then matched
/// on BOTH its own id and that party's, so neither id is ever trusted on its own
/// and a foreign object is the same not-found as a missing one.</para>
///
/// <para>Every write to a group spends the group's version through ONE
/// conditional statement — <c>UPDATE … WHERE Id = @id AND Version = @expected</c>
/// — issued first inside the transaction. That statement is the lock and the
/// check at once: a concurrent guest reply or owner edit either happened before
/// it (and the version no longer matches) or waits behind it.</para>
/// </summary>
public sealed class PartyInvitationService : IPartyInvitationService
{
    private readonly AppDbContext _db;
    private readonly TimeProvider _clock;
    private readonly PartyInvitationTokens _tokens;
    private readonly IEmailSender _email;
    private readonly IOptionsMonitor<MailOptions> _mail;

    public PartyInvitationService(
        AppDbContext db,
        TimeProvider clock,
        PartyInvitationTokens tokens,
        IEmailSender email,
        IOptionsMonitor<MailOptions> mail)
    {
        _db = db;
        _clock = clock;
        _tokens = tokens;
        _email = email;
        _mail = mail;
    }

    /// <summary>
    /// Whether an invitation can be emailed on this installation: a mailer that
    /// is switched on AND a public origin to build the personal link on. The
    /// request's Host header is never a substitute for the second.
    /// </summary>
    public static bool IsMailAvailable(IEmailSender email, MailOptions options) =>
        email.IsEnabled && PartyLinkPreview.Origin(options.PublicOrigin) is not null;

    public async Task<PartyGuestListDto?> GetGuestListAsync(
        Guid ownerUserId, Guid partyId, CancellationToken cancellationToken = default)
    {
        var status = await OwnedPartyStatusAsync(ownerUserId, partyId, cancellationToken);
        return status is null ? null : await ProjectAsync(partyId, status, cancellationToken);
    }

    // --- Groups -----------------------------------------------------------------

    public async Task<PartyInvitationResult> CreateGroupAsync(
        Guid ownerUserId, Guid partyId, PartyInvitationGroupWrite write,
        CancellationToken cancellationToken = default)
    {
        var status = await OwnedPartyStatusAsync(ownerUserId, partyId, cancellationToken);
        if (status is null) return new(PartyInvitationOutcome.NotFound);

        var group = NormalizeGroup(write, out var error);
        if (group is null) return new(PartyInvitationOutcome.InvalidRequest, Error: error);
        // A new group has no guests for an id to name.
        if (group.Guests.Any(g => g.Id is not null))
        {
            return new(PartyInvitationOutcome.InvalidRequest, Error: "unknown_guest");
        }

        var count = await _db.PartyInvitationGroups.CountAsync(g => g.PartyId == partyId, cancellationToken);
        if (count >= PartyInvitationLimits.MaxGroupsPerParty)
        {
            return new(PartyInvitationOutcome.InvalidRequest, Error: "too_many_groups");
        }

        var now = _clock.GetUtcNow().UtcDateTime;
        var (capabilityId, tokenHash) = _tokens.Mint();
        var row = new PartyInvitationGroup
        {
            Id = Guid.NewGuid(),
            PartyId = partyId,
            Label = group.Label,
            RecipientEmail = group.Email,
            Phone = group.Phone,
            MaxAdditionalGuests = group.MaxAdditionalGuests,
            CapabilityId = capabilityId,
            TokenHash = tokenHash,
            CapabilityIssuedAt = now,
            Version = 1,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.PartyInvitationGroups.Add(row);
        for (var i = 0; i < group.Guests.Count; i++)
        {
            AddNamedGuest(row.Id, group.Guests[i], i, now);
        }
        await _db.SaveChangesAsync(cancellationToken);
        _db.ChangeTracker.Clear();

        return new(PartyInvitationOutcome.Ok, await ProjectAsync(partyId, status, cancellationToken));
    }

    public async Task<PartyInvitationResult> UpdateGroupAsync(
        Guid ownerUserId, Guid partyId, Guid groupId, PartyInvitationGroupWrite write,
        int expectedVersion, CancellationToken cancellationToken = default)
    {
        var status = await OwnedPartyStatusAsync(ownerUserId, partyId, cancellationToken);
        if (status is null) return new(PartyInvitationOutcome.NotFound);
        var current = await _db.PartyInvitationGroups.AsNoTracking()
            .FirstOrDefaultAsync(g => g.Id == groupId && g.PartyId == partyId, cancellationToken);
        if (current is null) return new(PartyInvitationOutcome.NotFound);

        var group = NormalizeGroup(write, out var error);
        if (group is null) return new(PartyInvitationOutcome.InvalidRequest, Error: error);
        if (current.Version != expectedVersion)
        {
            return await ConflictAsync(partyId, status, cancellationToken);
        }

        var members = await _db.PartyGuests
            .Where(g => g.PartyInvitationGroupId == groupId)
            .ToListAsync(cancellationToken);
        // The +1s the group already added stay the group's. Lowering the
        // allowance below them would leave the group holding guests it may not
        // have, so the host is told instead of the guests being dropped.
        if (group.MaxAdditionalGuests < members.Count(m => m.IsAdditionalGuest))
        {
            return new(PartyInvitationOutcome.InvalidRequest, Error: "additional_guests_in_use");
        }
        var named = members.Where(m => !m.IsAdditionalGuest).ToDictionary(m => m.Id);
        if (group.Guests.Any(g => g.Id is Guid id && !named.ContainsKey(id)))
        {
            return new(PartyInvitationOutcome.InvalidRequest, Error: "unknown_guest");
        }

        // A NEW MAILBOX GETS A NEW LINK. The old address may belong to somebody
        // who is no longer meant to answer for this group, so the link it
        // received stops opening anything at the same instant the address
        // changes. The case of an address is not a different mailbox.
        var rotate = !string.Equals(current.RecipientEmail, group.Email, StringComparison.OrdinalIgnoreCase);
        var now = _clock.GetUtcNow().UtcDateTime;

        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        var (capabilityId, tokenHash) = rotate ? _tokens.Mint() : (current.CapabilityId, current.TokenHash);
        var bumped = await _db.PartyInvitationGroups
            .Where(g => g.Id == groupId && g.Version == expectedVersion)
            .ExecuteUpdateAsync(s => s
                .SetProperty(g => g.Label, group.Label)
                .SetProperty(g => g.RecipientEmail, group.Email)
                .SetProperty(g => g.Phone, group.Phone)
                .SetProperty(g => g.MaxAdditionalGuests, group.MaxAdditionalGuests)
                .SetProperty(g => g.CapabilityId, capabilityId)
                .SetProperty(g => g.TokenHash, tokenHash)
                .SetProperty(g => g.CapabilityIssuedAt, rotate ? now : current.CapabilityIssuedAt)
                .SetProperty(g => g.Version, g => g.Version + 1)
                .SetProperty(g => g.UpdatedAt, now), cancellationToken);
        if (bumped == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            _db.ChangeTracker.Clear();
            return await ConflictAsync(partyId, status, cancellationToken);
        }

        var kept = group.Guests.Where(g => g.Id is not null).Select(g => g.Id!.Value).ToHashSet();
        var removed = named.Keys.Where(id => !kept.Contains(id)).ToList();
        if (removed.Count > 0)
        {
            // A person taken off the list takes their arrival with them: it was
            // a fact about that guest, and it names them by key.
            await _db.PartyGuestAttendances
                .Where(a => removed.Contains(a.PartyGuestId))
                .ExecuteDeleteAsync(cancellationToken);
            await _db.PartyRsvps.Where(r => removed.Contains(r.PartyGuestId)).ExecuteDeleteAsync(cancellationToken);
            await _db.PartyGuests.Where(g => removed.Contains(g.Id)).ExecuteDeleteAsync(cancellationToken);
        }
        for (var i = 0; i < group.Guests.Count; i++)
        {
            var guest = group.Guests[i];
            if (guest.Id is Guid id)
            {
                var row = named[id];
                row.Name = guest.Name;
                row.Email = guest.Email;
                row.Phone = guest.Phone;
                row.SortOrder = i;
                row.UpdatedAt = now;
            }
            else
            {
                AddNamedGuest(groupId, guest, i, now);
            }
        }
        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        _db.ChangeTracker.Clear();

        return new(
            PartyInvitationOutcome.Ok, await ProjectAsync(partyId, status, cancellationToken), LinkRotated: rotate);
    }

    public async Task<PartyInvitationResult> DeleteGroupAsync(
        Guid ownerUserId, Guid partyId, Guid groupId, int expectedVersion,
        CancellationToken cancellationToken = default)
    {
        var status = await OwnedPartyStatusAsync(ownerUserId, partyId, cancellationToken);
        if (status is null) return new(PartyInvitationOutcome.NotFound);
        var exists = await _db.PartyInvitationGroups.AsNoTracking()
            .AnyAsync(g => g.Id == groupId && g.PartyId == partyId, cancellationToken);
        if (!exists) return new(PartyInvitationOutcome.NotFound);

        // Removing the group IS revoking its link: the hash goes with the row,
        // so the personal token resolves to nothing. It touches nothing of the
        // party's public capability and no PartyParticipant — those were never
        // this group's.
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        var bumped = await _db.PartyInvitationGroups
            .Where(g => g.Id == groupId && g.Version == expectedVersion)
            .ExecuteUpdateAsync(s => s.SetProperty(g => g.Version, g => g.Version + 1), cancellationToken);
        if (bumped == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return await ConflictAsync(partyId, status, cancellationToken);
        }
        // The same list the party's teardown uses, so what a group owns is
        // stated in one place.
        await PartyStateEraser.EraseInvitationGroupsAsync(
            _db, _db.PartyInvitationGroups.Where(g => g.Id == groupId), cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new(PartyInvitationOutcome.Ok, await ProjectAsync(partyId, status, cancellationToken));
    }

    public async Task<PartyInvitationResult> RotateLinkAsync(
        Guid ownerUserId, Guid partyId, Guid groupId, int expectedVersion,
        CancellationToken cancellationToken = default)
    {
        var status = await OwnedPartyStatusAsync(ownerUserId, partyId, cancellationToken);
        if (status is null) return new(PartyInvitationOutcome.NotFound);
        var exists = await _db.PartyInvitationGroups.AsNoTracking()
            .AnyAsync(g => g.Id == groupId && g.PartyId == partyId, cancellationToken);
        if (!exists) return new(PartyInvitationOutcome.NotFound);

        // A new generation. Every link sent before now resolves to nothing, and
        // the deliveries that carried it are kept, tagged with the generation
        // they sent — which is why they stop counting as "sent" for this link.
        var now = _clock.GetUtcNow().UtcDateTime;
        var (capabilityId, tokenHash) = _tokens.Mint();
        var bumped = await _db.PartyInvitationGroups
            .Where(g => g.Id == groupId && g.PartyId == partyId && g.Version == expectedVersion)
            .ExecuteUpdateAsync(s => s
                .SetProperty(g => g.CapabilityId, capabilityId)
                .SetProperty(g => g.TokenHash, tokenHash)
                .SetProperty(g => g.CapabilityIssuedAt, now)
                .SetProperty(g => g.Version, g => g.Version + 1)
                .SetProperty(g => g.UpdatedAt, now), cancellationToken);
        return bumped == 0
            ? await ConflictAsync(partyId, status, cancellationToken)
            : new(PartyInvitationOutcome.Ok, await ProjectAsync(partyId, status, cancellationToken));
    }

    // --- Questions --------------------------------------------------------------

    public async Task<PartyInvitationResult> CreateQuestionAsync(
        Guid ownerUserId, Guid partyId, PartyRsvpQuestionWrite write,
        CancellationToken cancellationToken = default)
    {
        var status = await OwnedPartyStatusAsync(ownerUserId, partyId, cancellationToken);
        if (status is null) return new(PartyInvitationOutcome.NotFound);
        var question = NormalizeQuestion(write, out var error);
        if (question is null) return new(PartyInvitationOutcome.InvalidRequest, Error: error);

        var existing = await _db.PartyRsvpQuestions.AsNoTracking()
            .Where(q => q.PartyId == partyId)
            .Select(q => new { q.IsActive, q.SortOrder })
            .ToListAsync(cancellationToken);
        if (existing.Count >= PartyInvitationLimits.MaxQuestionsPerParty
            || (write.IsActive && existing.Count(q => q.IsActive) >= PartyInvitationLimits.MaxActiveQuestions))
        {
            return new(PartyInvitationOutcome.InvalidRequest, Error: "too_many_questions");
        }

        var now = _clock.GetUtcNow().UtcDateTime;
        _db.PartyRsvpQuestions.Add(new PartyRsvpQuestion
        {
            Id = Guid.NewGuid(),
            PartyId = partyId,
            Prompt = question.Prompt,
            Kind = question.Kind,
            Required = question.Required,
            OptionsJson = question.OptionsJson,
            IsActive = write.IsActive,
            SortOrder = existing.Count == 0 ? 0 : existing.Max(q => q.SortOrder) + 1,
            Version = 1,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await _db.SaveChangesAsync(cancellationToken);
        _db.ChangeTracker.Clear();

        return new(PartyInvitationOutcome.Ok, await ProjectAsync(partyId, status, cancellationToken));
    }

    public async Task<PartyInvitationResult> UpdateQuestionAsync(
        Guid ownerUserId, Guid partyId, Guid questionId, PartyRsvpQuestionWrite write,
        int expectedVersion, CancellationToken cancellationToken = default)
    {
        var status = await OwnedPartyStatusAsync(ownerUserId, partyId, cancellationToken);
        if (status is null) return new(PartyInvitationOutcome.NotFound);
        var current = await _db.PartyRsvpQuestions.AsNoTracking()
            .FirstOrDefaultAsync(q => q.Id == questionId && q.PartyId == partyId, cancellationToken);
        if (current is null) return new(PartyInvitationOutcome.NotFound);

        var question = NormalizeQuestion(write, out var error);
        if (question is null) return new(PartyInvitationOutcome.InvalidRequest, Error: error);
        if (current.Version != expectedVersion) return await ConflictAsync(partyId, status, cancellationToken);

        // WHAT IT ASKS is frozen once anybody has answered. Deactivating and
        // moving it stay possible; changing its meaning under a stored answer
        // does not.
        var semanticChange = question.Prompt != current.Prompt
            || question.Kind != current.Kind
            || question.Required != current.Required
            || question.OptionsJson != current.OptionsJson;
        if (semanticChange
            && await _db.PartyRsvpAnswers.AnyAsync(a => a.PartyRsvpQuestionId == questionId, cancellationToken))
        {
            return new(
                PartyInvitationOutcome.QuestionLocked,
                await ProjectAsync(partyId, status, cancellationToken),
                "question_locked");
        }

        if (write.IsActive && !current.IsActive
            && await _db.PartyRsvpQuestions.CountAsync(q => q.PartyId == partyId && q.IsActive, cancellationToken)
                >= PartyInvitationLimits.MaxActiveQuestions)
        {
            return new(PartyInvitationOutcome.InvalidRequest, Error: "too_many_questions");
        }

        var now = _clock.GetUtcNow().UtcDateTime;
        var updated = await _db.PartyRsvpQuestions
            .Where(q => q.Id == questionId && q.Version == expectedVersion)
            .ExecuteUpdateAsync(s => s
                .SetProperty(q => q.Prompt, question.Prompt)
                .SetProperty(q => q.Kind, question.Kind)
                .SetProperty(q => q.Required, question.Required)
                .SetProperty(q => q.OptionsJson, question.OptionsJson)
                .SetProperty(q => q.IsActive, write.IsActive)
                .SetProperty(q => q.Version, q => q.Version + 1)
                .SetProperty(q => q.UpdatedAt, now), cancellationToken);
        return updated == 0
            ? await ConflictAsync(partyId, status, cancellationToken)
            : new(PartyInvitationOutcome.Ok, await ProjectAsync(partyId, status, cancellationToken));
    }

    public async Task<PartyInvitationResult> ReorderQuestionsAsync(
        Guid ownerUserId, Guid partyId, IReadOnlyList<Guid> questionIds,
        CancellationToken cancellationToken = default)
    {
        var status = await OwnedPartyStatusAsync(ownerUserId, partyId, cancellationToken);
        if (status is null) return new(PartyInvitationOutcome.NotFound);

        var questions = await _db.PartyRsvpQuestions
            .Where(q => q.PartyId == partyId)
            .ToListAsync(cancellationToken);
        // The whole order, every time: exactly this party's questions, once each.
        if (questionIds.Count != questions.Count
            || questionIds.Distinct().Count() != questionIds.Count
            || !questionIds.All(id => questions.Any(q => q.Id == id)))
        {
            return new(PartyInvitationOutcome.InvalidRequest, Error: "invalid_order");
        }

        var now = _clock.GetUtcNow().UtcDateTime;
        for (var i = 0; i < questionIds.Count; i++)
        {
            var question = questions.First(q => q.Id == questionIds[i]);
            if (question.SortOrder == i) continue;
            question.SortOrder = i;
            question.Version++;
            question.UpdatedAt = now;
        }
        await _db.SaveChangesAsync(cancellationToken);
        _db.ChangeTracker.Clear();

        return new(PartyInvitationOutcome.Ok, await ProjectAsync(partyId, status, cancellationToken));
    }

    // --- Projection -------------------------------------------------------------

    private async Task<PartyGuestListDto> ProjectAsync(
        Guid partyId, string partyStatus, CancellationToken cancellationToken)
    {
        var groupsOfParty = _db.PartyInvitationGroups.AsNoTracking().Where(g => g.PartyId == partyId);
        var groups = await groupsOfParty
            .OrderBy(g => g.CreatedAt).ThenBy(g => g.Id)
            .ToListAsync(cancellationToken);

        var guests = await (
            from guest in _db.PartyGuests.AsNoTracking()
            join g in groupsOfParty on guest.PartyInvitationGroupId equals g.Id
            join r in _db.PartyRsvps.AsNoTracking() on guest.Id equals r.PartyGuestId into rsvps
            from r in rsvps.DefaultIfEmpty()
            select new
            {
                Guest = guest,
                Status = r == null ? PartyRsvpStatuses.Pending : r.Status,
                DietaryNotes = r == null ? null : r.DietaryNotes,
                RespondedAt = r == null ? null : r.RespondedAt,
            }).ToListAsync(cancellationToken);
        var guestsByGroup = guests.ToLookup(x => x.Guest.PartyInvitationGroupId);

        var deliveries = await _db.PartyInvitationDeliveries.AsNoTracking()
            .Join(groupsOfParty, d => d.PartyInvitationGroupId, g => g.Id, (d, g) => d)
            .ToListAsync(cancellationToken);
        var deliveriesByGroup = deliveries.ToLookup(d => d.PartyInvitationGroupId);

        var answers = await _db.PartyRsvpAnswers.AsNoTracking()
            .Join(groupsOfParty, a => a.PartyInvitationGroupId, g => g.Id, (a, g) => a)
            .ToListAsync(cancellationToken);
        var answersByGroup = answers.ToLookup(a => a.PartyInvitationGroupId);

        var questions = await _db.PartyRsvpQuestions.AsNoTracking()
            .Where(q => q.PartyId == partyId)
            .OrderBy(q => q.SortOrder).ThenBy(q => q.CreatedAt)
            .Select(q => new
            {
                Question = q,
                Answers = _db.PartyRsvpAnswers.Count(a => a.PartyRsvpQuestionId == q.Id),
            })
            .ToListAsync(cancellationToken);

        var mailAvailable = IsMailAvailable(_email, _mail.CurrentValue);
        var sendable = partyStatus is PartyStatuses.Draft or PartyStatuses.Published;

        var groupDtos = groups.Select(group =>
        {
            var members = guestsByGroup[group.Id]
                .OrderBy(x => x.Guest.IsAdditionalGuest)
                .ThenBy(x => x.Guest.SortOrder)
                .ThenBy(x => x.Guest.CreatedAt)
                .ToList();
            var named = members.Where(x => !x.Guest.IsAdditionalGuest).ToList();
            var pending = named.Count(x => x.Status == PartyRsvpStatuses.Pending);
            var delivery = DeliveryState(deliveriesByGroup[group.Id], group.CapabilityId, out var invited);

            return new PartyInvitationGroupDto(
                group.Id,
                group.Label,
                group.RecipientEmail,
                group.Phone,
                group.MaxAdditionalGuests,
                group.Version,
                members.Select(x => new PartyGuestDto(
                    x.Guest.Id, x.Guest.Name, x.Guest.Email, x.Guest.Phone, x.Guest.IsAdditionalGuest,
                    x.Status, x.DietaryNotes, x.RespondedAt)).ToList(),
                members.Count(x => x.Guest.IsAdditionalGuest),
                pending,
                members.Count(x => x.Status == PartyRsvpStatuses.Attending),
                named.Count(x => x.Status == PartyRsvpStatuses.Declined),
                answersByGroup[group.Id]
                    .Select(a => new PartyRsvpAnswerDto(a.PartyRsvpQuestionId, ParseJson(a.ValueJson)))
                    .ToList(),
                delivery,
                CanSend: mailAvailable && sendable,
                // Only a group that has been invited on the link it holds now,
                // and that still has somebody who has not answered.
                CanRemind: mailAvailable && partyStatus == PartyStatuses.Published && invited && pending > 0);
        }).ToList();

        var namedGuests = guests.Where(x => !x.Guest.IsAdditionalGuest).ToList();
        var attending = guests.Count(x => x.Status == PartyRsvpStatuses.Attending);
        var summary = new PartyRsvpSummaryDto(
            Groups: groups.Count,
            Invited: namedGuests.Count,
            MissingResponses: namedGuests.Count(x => x.Status == PartyRsvpStatuses.Pending),
            Attending: attending,
            Declined: namedGuests.Count(x => x.Status == PartyRsvpStatuses.Declined),
            ExpectedPeople: attending,
            UnansweredGroups: groupDtos.Count(g => g.PendingCount > 0));

        return new PartyGuestListDto(
            partyId,
            partyStatus,
            mailAvailable,
            summary,
            groupDtos,
            questions.Select(x => new PartyRsvpQuestionDto(
                x.Question.Id,
                x.Question.Prompt,
                x.Question.Kind,
                x.Question.Required,
                PartyRsvpQuestionRules.ParseOptions(x.Question.OptionsJson),
                x.Question.IsActive,
                x.Question.SortOrder,
                x.Question.Version,
                x.Answers,
                Locked: x.Answers > 0)).ToList());
    }

    /// <summary>
    /// Where the CURRENT link generation's invitation stands. Only deliveries
    /// that carried this generation count: an email with a rotated link in it
    /// invited nobody.
    /// </summary>
    internal static PartyInvitationDeliveryStateDto DeliveryState(
        IEnumerable<PartyInvitationDelivery> deliveries, Guid capabilityId, out bool invited)
    {
        var current = deliveries.Where(d => d.CapabilityId == capabilityId)
            .OrderByDescending(d => d.CreatedAt)
            .ToList();
        invited = current.Any(d =>
            d.Status == PartyInvitationDeliveryStatuses.Sent
            && d.Kind != PartyInvitationDeliveryKinds.Reminder);
        var last = current.FirstOrDefault();
        var lastSent = current
            .Where(d => d.Status == PartyInvitationDeliveryStatuses.Sent)
            .Select(d => d.CompletedAt)
            .Max();
        var state = invited
            ? PartyInvitationDeliveryStates.Sent
            : last?.Status switch
            {
                PartyInvitationDeliveryStatuses.Pending => PartyInvitationDeliveryStates.Pending,
                PartyInvitationDeliveryStatuses.Failed => PartyInvitationDeliveryStates.Failed,
                _ => PartyInvitationDeliveryStates.NotSent,
            };
        return new PartyInvitationDeliveryStateDto(state, last?.CreatedAt, last?.Kind, last?.Status, lastSent);
    }

    // --- Helpers ----------------------------------------------------------------

    private async Task<string?> OwnedPartyStatusAsync(
        Guid ownerUserId, Guid partyId, CancellationToken cancellationToken) =>
        await _db.Parties.AsNoTracking()
            .Where(p => p.Id == partyId && p.OwnerUserId == ownerUserId)
            .Select(p => p.Status)
            .FirstOrDefaultAsync(cancellationToken);

    private async Task<PartyInvitationResult> ConflictAsync(
        Guid partyId, string partyStatus, CancellationToken cancellationToken) =>
        new(PartyInvitationOutcome.VersionConflict,
            await ProjectAsync(partyId, partyStatus, cancellationToken),
            "version_conflict");

    private void AddNamedGuest(Guid groupId, NormalizedGuest guest, int sortOrder, DateTime now)
    {
        var id = Guid.NewGuid();
        _db.PartyGuests.Add(new PartyGuest
        {
            Id = id,
            PartyInvitationGroupId = groupId,
            Name = guest.Name,
            Email = guest.Email,
            Phone = guest.Phone,
            IsAdditionalGuest = false,
            SortOrder = sortOrder,
            CreatedAt = now,
            UpdatedAt = now,
        });
        // Invited means asked: every named guest starts with an answer to give.
        _db.PartyRsvps.Add(new PartyRsvp
        {
            PartyGuestId = id,
            Status = PartyRsvpStatuses.Pending,
            UpdatedAt = now,
        });
    }

    private static JsonElement ParseJson(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private sealed record NormalizedGuest(Guid? Id, string Name, string? Email, string? Phone);

    private sealed record NormalizedGroup(
        string Label, string Email, string? Phone, int MaxAdditionalGuests,
        IReadOnlyList<NormalizedGuest> Guests);

    private sealed record NormalizedQuestion(string Prompt, string Kind, bool Required, string? OptionsJson);

    private static NormalizedGroup? NormalizeGroup(PartyInvitationGroupWrite write, out string? error)
    {
        error = null;
        var label = PartyInvitationLimits.Normalize(write.Label);
        if (label is null || !PartyInvitationLimits.Fits(label, PartyInvitationLimits.MaxLabelLength))
        {
            error = "invalid_label";
            return null;
        }
        var email = PartyInvitationLimits.Normalize(write.RecipientEmail);
        if (!PartyInvitationLimits.IsValidEmail(email))
        {
            error = "invalid_email";
            return null;
        }
        var phone = PartyInvitationLimits.Normalize(write.Phone);
        if (!PartyInvitationLimits.Fits(phone, PartyInvitationLimits.MaxPhoneLength))
        {
            error = "invalid_phone";
            return null;
        }
        if (write.MaxAdditionalGuests is < 0 or > PartyInvitationLimits.MaxAdditionalGuests)
        {
            error = "invalid_max_additional_guests";
            return null;
        }
        // A group is always somebody: at least one named guest, and a bounded
        // household rather than a mailing list.
        if (write.Guests is null || write.Guests.Count is 0 or > PartyInvitationLimits.MaxNamedGuests)
        {
            error = "invalid_guests";
            return null;
        }
        var ids = write.Guests.Where(g => g.Id is not null).Select(g => g.Id!.Value).ToList();
        if (ids.Distinct().Count() != ids.Count)
        {
            error = "invalid_guests";
            return null;
        }

        var guests = new List<NormalizedGuest>(write.Guests.Count);
        foreach (var guest in write.Guests)
        {
            var name = PartyInvitationLimits.Normalize(guest.Name);
            var guestEmail = PartyInvitationLimits.Normalize(guest.Email);
            var guestPhone = PartyInvitationLimits.Normalize(guest.Phone);
            if (name is null
                || !PartyInvitationLimits.Fits(name, PartyInvitationLimits.MaxGuestNameLength)
                || (guestEmail is not null && !PartyInvitationLimits.IsValidEmail(guestEmail))
                || !PartyInvitationLimits.Fits(guestPhone, PartyInvitationLimits.MaxPhoneLength))
            {
                error = "invalid_guest";
                return null;
            }
            guests.Add(new NormalizedGuest(guest.Id, name, guestEmail, guestPhone));
        }
        return new NormalizedGroup(label, email!, phone, write.MaxAdditionalGuests, guests);
    }

    private static NormalizedQuestion? NormalizeQuestion(PartyRsvpQuestionWrite write, out string? error)
    {
        error = null;
        var prompt = PartyInvitationLimits.Normalize(write.Prompt);
        if (prompt is null || !PartyInvitationLimits.Fits(prompt, PartyInvitationLimits.MaxQuestionPromptLength))
        {
            error = "invalid_prompt";
            return null;
        }
        if (!PartyRsvpQuestionKinds.IsKnown(write.Kind))
        {
            error = "invalid_kind";
            return null;
        }
        var options = PartyRsvpQuestionRules.NormalizeOptions(write.Kind!, write.Options);
        if (options is null)
        {
            error = "invalid_options";
            return null;
        }
        return new NormalizedQuestion(
            prompt, write.Kind!, write.Required, PartyRsvpQuestionRules.SerializeOptions(write.Kind!, options));
    }
}
