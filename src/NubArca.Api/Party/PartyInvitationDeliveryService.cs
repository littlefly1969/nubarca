using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NubArca.Api.Auth.Recovery;
using NubArca.Api.Data;
using NubArca.Api.Domain;

namespace NubArca.Api.Party;

/// <summary>
/// Sending an invitation, and the ledger that makes a double click send once.
///
/// <para>THE PROTOCOL, in order. Ownership. Then the request id: if this group
/// has already recorded a delivery for it, that delivery IS the answer and SMTP
/// is not called again, whatever state it is in. Then whether mail can be sent
/// at all, and whether this send is allowed now. Then a <c>pending</c> row,
/// COMMITTED before the message leaves — the unique index on
/// <c>(group, request id)</c> is what makes two racing copies of one click
/// produce one row. Then SMTP, then the row's outcome. A process that dies
/// between the last two leaves the row <c>pending</c>, and a retry of the same
/// click still sends nothing: the host sees "not confirmed" and decides, by
/// pressing resend — which is a new click, a new id and a new attempt. There is
/// no repair scheduler, on purpose.</para>
///
/// <para>A delivery never touches an RSVP. Failure, success and uncertainty are
/// all facts about an email, and none of them is an answer.</para>
/// </summary>
public sealed class PartyInvitationDeliveryService : IPartyInvitationDeliveryService
{
    private readonly AppDbContext _db;
    private readonly TimeProvider _clock;
    private readonly IPartyService _parties;
    private readonly IPartyInvitationService _invitations;
    private readonly PartyInvitationTokens _tokens;
    private readonly IEmailSender _email;
    private readonly IOptionsMonitor<MailOptions> _mail;
    private readonly ILogger<PartyInvitationDeliveryService> _logger;

    public PartyInvitationDeliveryService(
        AppDbContext db,
        TimeProvider clock,
        IPartyService parties,
        IPartyInvitationService invitations,
        PartyInvitationTokens tokens,
        IEmailSender email,
        IOptionsMonitor<MailOptions> mail,
        ILogger<PartyInvitationDeliveryService> logger)
    {
        _db = db;
        _clock = clock;
        _parties = parties;
        _invitations = invitations;
        _tokens = tokens;
        _email = email;
        _mail = mail;
        _logger = logger;
    }

    public Task<PartyInvitationSendResult> SendAsync(
        Guid ownerUserId, Guid partyId, Guid groupId, Guid clientRequestId, int? partyVersion,
        CancellationToken cancellationToken = default) =>
        DeliverAsync(ownerUserId, partyId, groupId, clientRequestId, partyVersion, reminder: false, cancellationToken);

    public Task<PartyInvitationSendResult> RemindAsync(
        Guid ownerUserId, Guid partyId, Guid groupId, Guid clientRequestId,
        CancellationToken cancellationToken = default) =>
        DeliverAsync(ownerUserId, partyId, groupId, clientRequestId, null, reminder: true, cancellationToken);

    private async Task<PartyInvitationSendResult> DeliverAsync(
        Guid ownerUserId, Guid partyId, Guid groupId, Guid clientRequestId, int? partyVersion,
        bool reminder, CancellationToken cancellationToken)
    {
        if (clientRequestId == Guid.Empty)
        {
            return new(PartyInvitationOutcome.InvalidRequest, Error: "invalid_request_id");
        }

        var party = await _db.Parties.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == partyId && p.OwnerUserId == ownerUserId, cancellationToken);
        if (party is null) return new(PartyInvitationOutcome.NotFound);
        var group = await _db.PartyInvitationGroups.AsNoTracking()
            .FirstOrDefaultAsync(g => g.Id == groupId && g.PartyId == partyId, cancellationToken);
        if (group is null) return new(PartyInvitationOutcome.NotFound);

        // THE REPLAY, before anything else is judged. A retry must get the
        // answer the first attempt got, even if the party has moved on since.
        var earlier = await FindAsync(groupId, clientRequestId, cancellationToken);
        if (earlier is not null)
        {
            return await OkAsync(ownerUserId, partyId, earlier, replayed: true, cancellationToken);
        }

        var origin = PartyLinkPreview.Origin(_mail.CurrentValue.PublicOrigin);
        if (!_email.IsEnabled || origin is null)
        {
            return await RefusedAsync(
                ownerUserId, partyId, PartyInvitationOutcome.MailUnavailable, "mail_unavailable", cancellationToken);
        }

        var invited = await _db.PartyInvitationDeliveries.AsNoTracking()
            .AnyAsync(d => d.PartyInvitationGroupId == groupId
                && d.CapabilityId == group.CapabilityId
                && d.Status == PartyInvitationDeliveryStatuses.Sent
                && d.Kind != PartyInvitationDeliveryKinds.Reminder, cancellationToken);

        string kind;
        if (reminder)
        {
            if (party.Status != PartyStatuses.Published)
            {
                return await RefusedAsync(
                    ownerUserId, partyId, PartyInvitationOutcome.InvitationsClosed, "invitations_closed",
                    cancellationToken);
            }
            var pending = await _db.PartyGuests.AsNoTracking()
                .Where(g => g.PartyInvitationGroupId == groupId && !g.IsAdditionalGuest)
                .Join(_db.PartyRsvps.AsNoTracking(), g => g.Id, r => r.PartyGuestId, (g, r) => r.Status)
                .CountAsync(s => s == PartyRsvpStatuses.Pending, cancellationToken);
            // A reminder is for somebody who was invited on the link they hold
            // now and has not answered. Anybody else would be reminded of
            // nothing, or of a link that no longer opens.
            if (!invited || pending == 0)
            {
                return await RefusedAsync(
                    ownerUserId, partyId, PartyInvitationOutcome.ReminderNotAllowed, "reminder_not_allowed",
                    cancellationToken);
            }
            kind = PartyInvitationDeliveryKinds.Reminder;
        }
        else
        {
            if (party.Status is PartyStatuses.Live or PartyStatuses.Ended)
            {
                return await RefusedAsync(
                    ownerUserId, partyId, PartyInvitationOutcome.InvitationsClosed, "invitations_closed",
                    cancellationToken);
            }

            // THE FIRST INVITATION IS THE ANNOUNCEMENT. A Draft party is
            // published by it — through the lifecycle's own transition and its
            // own optimistic check, never by assigning a status here — so a send
            // made from a stale read of the party is refused with the party as
            // it is now, rather than publishing over somebody else's edit.
            // Publication is lifecycle state, not proof of delivery: if the mail
            // then fails, the party stays published.
            if (party.Status == PartyStatuses.Draft)
            {
                var published = partyVersion is int version
                    ? await _parties.TransitionAsync(
                        ownerUserId, partyId, PartyLifecycleAction.Publish, version, cancellationToken)
                    : null;
                if (published?.Outcome != PartyMutationOutcome.Ok)
                {
                    return new(
                        PartyInvitationOutcome.PartyVersionConflict,
                        Party: published?.Party ?? await _parties.GetAsync(ownerUserId, partyId, cancellationToken),
                        Error: "party_version_conflict");
                }
                _db.ChangeTracker.Clear();
            }
            kind = invited ? PartyInvitationDeliveryKinds.Resend : PartyInvitationDeliveryKinds.Initial;
        }

        var delivery = new PartyInvitationDelivery
        {
            Id = Guid.NewGuid(),
            PartyInvitationGroupId = groupId,
            ClientRequestId = clientRequestId,
            // The generation this email will carry. A rotation after this point
            // makes it a delivery of a dead link, which is exactly what it is.
            CapabilityId = group.CapabilityId,
            Kind = kind,
            Status = PartyInvitationDeliveryStatuses.Pending,
            CreatedAt = _clock.GetUtcNow().UtcDateTime,
        };
        _db.PartyInvitationDeliveries.Add(delivery);
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // The unique index refused it: the same click arrived twice and the
            // other copy recorded first. Its row is the answer; no email here.
            _db.ChangeTracker.Clear();
            var winner = await FindAsync(groupId, clientRequestId, CancellationToken.None);
            if (winner is not null)
            {
                return await OkAsync(ownerUserId, partyId, winner, replayed: true, cancellationToken);
            }
            // Or the group was removed under us, which is the same not-found as
            // one that never existed.
            if (!await _db.PartyInvitationGroups.AnyAsync(g => g.Id == groupId, CancellationToken.None))
            {
                return new(PartyInvitationOutcome.NotFound);
            }
            throw;
        }
        _db.ChangeTracker.Clear();

        // From here the attempt is real and is FINISHED whatever the caller
        // does: a browser giving up must not turn a sent email into a failed
        // one, or leave an uncertain row that a completed request could have
        // settled.
        var hostLanguage = await _db.Users.AsNoTracking()
            .Where(u => u.Id == ownerUserId)
            .Select(u => u.UiLanguage)
            .FirstOrDefaultAsync(CancellationToken.None) ?? string.Empty;
        var message = PartyInvitationEmail.Compose(
            hostLanguage,
            group.RecipientEmail,
            group.Label,
            party.Title,
            party.EventStartsAt,
            origin + PartyInvitationTokens.InvitationPath(_tokens.Derive(group.CapabilityId)),
            reminder);

        bool accepted;
        try
        {
            accepted = await _email.SendAsync(message, CancellationToken.None);
        }
        catch (Exception)
        {
            accepted = false;
        }

        var status = accepted ? PartyInvitationDeliveryStatuses.Sent : PartyInvitationDeliveryStatuses.Failed;
        var completedAt = _clock.GetUtcNow().UtcDateTime;
        await _db.PartyInvitationDeliveries
            .Where(d => d.Id == delivery.Id && d.Status == PartyInvitationDeliveryStatuses.Pending)
            .ExecuteUpdateAsync(s => s
                .SetProperty(d => d.Status, status)
                .SetProperty(d => d.CompletedAt, completedAt), CancellationToken.None);
        if (!accepted)
        {
            // The kind and nothing else: not the address, not the group, not
            // the link. SmtpEmailSender has already logged the host it failed on.
            _logger.LogWarning("party.invitation.delivery_failed Kind={Kind}", kind);
        }

        delivery.Status = status;
        delivery.CompletedAt = completedAt;
        return await OkAsync(ownerUserId, partyId, delivery, replayed: false, cancellationToken);
    }

    private Task<PartyInvitationDelivery?> FindAsync(
        Guid groupId, Guid clientRequestId, CancellationToken cancellationToken) =>
        _db.PartyInvitationDeliveries.AsNoTracking()
            .FirstOrDefaultAsync(
                d => d.PartyInvitationGroupId == groupId && d.ClientRequestId == clientRequestId,
                cancellationToken);

    private async Task<PartyInvitationSendResult> OkAsync(
        Guid ownerUserId, Guid partyId, PartyInvitationDelivery delivery, bool replayed,
        CancellationToken cancellationToken) =>
        new(PartyInvitationOutcome.Ok,
            new PartyInvitationDeliveryDto(
                delivery.Kind, delivery.Status, delivery.CreatedAt, delivery.CompletedAt, replayed),
            await _invitations.GetGuestListAsync(ownerUserId, partyId, cancellationToken),
            await _parties.GetAsync(ownerUserId, partyId, cancellationToken));

    private async Task<PartyInvitationSendResult> RefusedAsync(
        Guid ownerUserId, Guid partyId, PartyInvitationOutcome outcome, string error,
        CancellationToken cancellationToken) =>
        new(outcome,
            GuestList: await _invitations.GetGuestListAsync(ownerUserId, partyId, cancellationToken),
            Party: await _parties.GetAsync(ownerUserId, partyId, cancellationToken),
            Error: error);
}
