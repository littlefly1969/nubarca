using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NubArca.Api.Auth.Recovery;
using NubArca.Api.Data;
using NubArca.Api.Domain;

namespace NubArca.Api.Party;

/// <summary>
/// The personal link leaving NubArca — emailed, or handed to the host to share
/// on WhatsApp or anywhere a copied link goes — and the ledger that makes a
/// double click do it once.
///
/// <para>THE EMAIL PROTOCOL, in order. Ownership. Then the request id: if this
/// group has already recorded a delivery for it, that delivery IS the answer and
/// SMTP is not called again, whatever state it is in. Then whether mail can be
/// sent at all, and whether this send is allowed now. Then a <c>pending</c> row,
/// COMMITTED before the message leaves — the unique index on
/// <c>(group, request id)</c> is what makes two racing copies of one click
/// produce one row. Then SMTP, then the row's outcome. A process that dies
/// between the last two leaves the row <c>pending</c>, and a retry of the same
/// click still sends nothing: the host sees "not confirmed" and decides, by
/// pressing resend — which is a new click, a new id and a new attempt. There is
/// no repair scheduler, on purpose.</para>
///
/// <para>THE SHARE PROTOCOL is the same ledger with nothing to wait for:
/// ownership, the request id, a public origin, whether invitations are still
/// open, the Draft's publication, then ONE <c>shared</c> row — complete when it
/// is written, because the only thing that happened is that the host now holds
/// the link. The answer carries the group's CURRENT link, derived from the
/// capability generation the row records, so a replay hands back the very same
/// link and never records a second share. What happens in WhatsApp afterwards is
/// the host's, and NubArca neither learns nor claims it.</para>
///
/// <para>A delivery never touches an RSVP. Failure, success, uncertainty and a
/// share are all facts about the link, and none of them is an answer.</para>
/// </summary>
public sealed class PartyInvitationDeliveryService : IPartyInvitationDeliveryService
{
    private readonly AppDbContext _db;
    private readonly TimeProvider _clock;
    private readonly IPartyService _parties;
    private readonly IPartyInvitationService _invitations;
    private readonly IPartyGuestDirectoryService _directory;
    private readonly PartyInvitationTokens _tokens;
    private readonly IEmailSender _email;
    private readonly IOptionsMonitor<MailOptions> _mail;
    private readonly ILogger<PartyInvitationDeliveryService> _logger;

    public PartyInvitationDeliveryService(
        AppDbContext db,
        TimeProvider clock,
        IPartyService parties,
        IPartyInvitationService invitations,
        IPartyGuestDirectoryService directory,
        PartyInvitationTokens tokens,
        IEmailSender email,
        IOptionsMonitor<MailOptions> mail,
        ILogger<PartyInvitationDeliveryService> logger)
    {
        _db = db;
        _clock = clock;
        _parties = parties;
        _invitations = invitations;
        _directory = directory;
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
            // One click is one act on one channel: an id already spent on a
            // share cannot be answered as if it had been an email.
            return earlier.Channel == PartyInvitationDeliveryChannels.Email
                ? await OkAsync(ownerUserId, partyId, earlier, replayed: true, cancellationToken)
                : new(PartyInvitationOutcome.InvalidRequest, Error: "request_id_reused");
        }

        var origin = PartyLinkPreview.Origin(_mail.CurrentValue.PublicOrigin);
        if (!_email.IsEnabled || origin is null)
        {
            return await RefusedAsync(
                ownerUserId, partyId, PartyInvitationOutcome.MailUnavailable, "mail_unavailable", cancellationToken);
        }

        var invited = await InvitedAsync(group, cancellationToken);

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
            // now — by email or by a link the host shared — and has not
            // answered. Anybody else would be reminded of nothing, or of a link
            // that no longer opens.
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
            if (!PartyInvitationPolicy.TakesInvitations(party.Status))
            {
                return await RefusedAsync(
                    ownerUserId, partyId, PartyInvitationOutcome.InvitationsClosed, "invitations_closed",
                    cancellationToken);
            }
            var conflict = await PublishDraftAsync(ownerUserId, party, partyVersion, cancellationToken);
            if (conflict is not null)
            {
                return new(PartyInvitationOutcome.PartyVersionConflict, Party: conflict, Error: "party_version_conflict");
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
            Channel = PartyInvitationDeliveryChannels.Email,
            Kind = kind,
            Status = PartyInvitationDeliveryStatuses.Pending,
            CreatedAt = _clock.GetUtcNow().UtcDateTime,
        };
        if (await RecordAsync(delivery, cancellationToken) is { } lost)
        {
            // The same click arrived twice and the other copy recorded first.
            // Its row is the answer; no email here.
            return lost.Winner is not { } row
                ? new(PartyInvitationOutcome.NotFound)
                : row.Channel == PartyInvitationDeliveryChannels.Email
                    ? await OkAsync(ownerUserId, partyId, row, replayed: true, cancellationToken)
                    : new(PartyInvitationOutcome.InvalidRequest, Error: "request_id_reused");
        }

        // From here the attempt is real and is FINISHED whatever the caller
        // does: a browser giving up must not turn a sent email into a failed
        // one, or leave an uncertain row that a completed request could have
        // settled.
        var message = PartyInvitationEmail.Compose(
            await HostLanguageAsync(ownerUserId),
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

    public async Task<PartyInvitationShareResult> ShareAsync(
        Guid ownerUserId, Guid partyId, Guid groupId, string? channel, Guid clientRequestId, int? partyVersion,
        CancellationToken cancellationToken = default)
    {
        if (!PartyInvitationDeliveryChannels.IsShare(channel))
        {
            return new(PartyInvitationOutcome.InvalidRequest, Error: "invalid_channel");
        }
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

        // The link is built on the operator's public origin and on nothing else
        // — never on the request's Host header. Without one there is no link.
        var origin = PartyLinkPreview.Origin(_mail.CurrentValue.PublicOrigin);

        var earlier = await FindAsync(groupId, clientRequestId, cancellationToken);
        if (earlier is not null)
        {
            return await ShareReplayAsync(ownerUserId, party, group, channel!, earlier, origin, cancellationToken);
        }

        if (origin is null)
        {
            return await ShareRefusedAsync(
                ownerUserId, partyId, PartyInvitationOutcome.LinkUnavailable, "link_unavailable", cancellationToken);
        }
        if (!PartyInvitationPolicy.TakesInvitations(party.Status))
        {
            return await ShareRefusedAsync(
                ownerUserId, partyId, PartyInvitationOutcome.InvitationsClosed, "invitations_closed", cancellationToken);
        }
        // The first share of a Draft is its announcement, exactly as the first
        // email is: published through the lifecycle, from the version the page read.
        var conflict = await PublishDraftAsync(ownerUserId, party, partyVersion, cancellationToken);
        if (conflict is not null)
        {
            return new(PartyInvitationOutcome.PartyVersionConflict, Party: conflict, Error: "party_version_conflict");
        }

        var now = _clock.GetUtcNow().UtcDateTime;
        var delivery = new PartyInvitationDelivery
        {
            Id = Guid.NewGuid(),
            PartyInvitationGroupId = groupId,
            ClientRequestId = clientRequestId,
            CapabilityId = group.CapabilityId,
            Channel = channel!,
            Kind = await InvitedAsync(group, cancellationToken)
                ? PartyInvitationDeliveryKinds.Resend
                : PartyInvitationDeliveryKinds.Initial,
            // Complete the moment it is written: the host holds the link now,
            // and nothing that happens next is NubArca's to record.
            Status = PartyInvitationDeliveryStatuses.Shared,
            CreatedAt = now,
            CompletedAt = now,
        };
        if (await RecordAsync(delivery, cancellationToken) is { } lost)
        {
            return lost.Winner is { } row
                ? await ShareReplayAsync(ownerUserId, party, group, channel!, row, origin, cancellationToken)
                : new(PartyInvitationOutcome.NotFound);
        }
        return await ShareOkAsync(ownerUserId, party, group, delivery, origin, replayed: false, cancellationToken);
    }

    // --- The ledger ------------------------------------------------------------------

    /// <summary>
    /// Whether the group was invited on the link it holds NOW, on any channel —
    /// <see cref="PartyInvitationDeliveryStatuses.IsInvitation"/>, as a query.
    /// </summary>
    private Task<bool> InvitedAsync(PartyInvitationGroup group, CancellationToken cancellationToken) =>
        _db.PartyInvitationDeliveries.AsNoTracking()
            .AnyAsync(d => d.PartyInvitationGroupId == group.Id
                && d.CapabilityId == group.CapabilityId
                && d.Kind != PartyInvitationDeliveryKinds.Reminder
                && (d.Status == PartyInvitationDeliveryStatuses.Sent
                    || d.Status == PartyInvitationDeliveryStatuses.Shared), cancellationToken);

    /// <summary>
    /// A Draft is published by its first invitation — through the lifecycle's
    /// own transition and its own optimistic check, never by assigning a status
    /// here — so an invitation made from a stale read of the party is refused
    /// with the party as it is now, rather than publishing over somebody else's
    /// edit. Publication is lifecycle state, not proof of delivery. Null: the
    /// party takes invitations now. Otherwise the party as it is, to adopt.
    /// </summary>
    private async Task<PartyDto?> PublishDraftAsync(
        Guid ownerUserId, Domain.Party party, int? partyVersion, CancellationToken cancellationToken)
    {
        if (party.Status != PartyStatuses.Draft) return null;
        var published = partyVersion is int version
            ? await _parties.TransitionAsync(
                ownerUserId, party.Id, PartyLifecycleAction.Publish, version, cancellationToken)
            : null;
        if (published?.Outcome == PartyMutationOutcome.Ok)
        {
            _db.ChangeTracker.Clear();
            return null;
        }
        return published?.Party ?? await _parties.GetAsync(ownerUserId, party.Id, cancellationToken);
    }

    /// <summary>The unique index refused a row: the copy that recorded first, or null when the group is gone.</summary>
    private sealed record Lost(PartyInvitationDelivery? Winner);

    /// <summary>
    /// Commits the row. Null: it is this call's. Otherwise the unique index
    /// refused it — the same click arrived twice — and the answer names the
    /// copy that recorded first, or nothing when the group was removed under
    /// us, which is the same not-found as one that never existed.
    /// </summary>
    private async Task<Lost?> RecordAsync(PartyInvitationDelivery delivery, CancellationToken cancellationToken)
    {
        _db.PartyInvitationDeliveries.Add(delivery);
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            _db.ChangeTracker.Clear();
            return null;
        }
        catch (DbUpdateException)
        {
            _db.ChangeTracker.Clear();
            var winner = await FindAsync(delivery.PartyInvitationGroupId, delivery.ClientRequestId, CancellationToken.None);
            if (winner is not null) return new Lost(winner);
            if (!await _db.PartyInvitationGroups.AnyAsync(g => g.Id == delivery.PartyInvitationGroupId, CancellationToken.None))
            {
                return new Lost(null);
            }
            throw;
        }
    }

    private Task<PartyInvitationDelivery?> FindAsync(
        Guid groupId, Guid clientRequestId, CancellationToken cancellationToken) =>
        _db.PartyInvitationDeliveries.AsNoTracking()
            .FirstOrDefaultAsync(
                d => d.PartyInvitationGroupId == groupId && d.ClientRequestId == clientRequestId,
                cancellationToken);

    private async Task<string> HostLanguageAsync(Guid ownerUserId) =>
        await _db.Users.AsNoTracking()
            .Where(u => u.Id == ownerUserId)
            .Select(u => u.UiLanguage)
            .FirstOrDefaultAsync(CancellationToken.None) ?? string.Empty;

    // --- Answers ---------------------------------------------------------------------

    private async Task<PartyInvitationSendResult> OkAsync(
        Guid ownerUserId, Guid partyId, PartyInvitationDelivery delivery, bool replayed,
        CancellationToken cancellationToken) =>
        new(PartyInvitationOutcome.Ok,
            new PartyInvitationDeliveryDto(
                delivery.Kind, delivery.Status, delivery.CreatedAt, delivery.CompletedAt, replayed, delivery.Channel),
            await _invitations.GetGuestListAsync(ownerUserId, partyId, cancellationToken),
            await _parties.GetAsync(ownerUserId, partyId, cancellationToken));

    private async Task<PartyInvitationSendResult> RefusedAsync(
        Guid ownerUserId, Guid partyId, PartyInvitationOutcome outcome, string error,
        CancellationToken cancellationToken) =>
        new(outcome,
            GuestList: await _invitations.GetGuestListAsync(ownerUserId, partyId, cancellationToken),
            Party: await _parties.GetAsync(ownerUserId, partyId, cancellationToken),
            Error: error);

    private async Task<PartyInvitationShareResult> ShareReplayAsync(
        Guid ownerUserId, Domain.Party party, PartyInvitationGroup group, string channel,
        PartyInvitationDelivery earlier, string? origin, CancellationToken cancellationToken)
    {
        // The same click, again: the same link, and no second share. An id
        // already spent on a different channel is a different act.
        if (earlier.Channel != channel)
        {
            return new(PartyInvitationOutcome.InvalidRequest, Error: "request_id_reused");
        }
        return origin is null
            ? await ShareRefusedAsync(
                ownerUserId, party.Id, PartyInvitationOutcome.LinkUnavailable, "link_unavailable", cancellationToken)
            : await ShareOkAsync(ownerUserId, party, group, earlier, origin, replayed: true, cancellationToken);
    }

    private async Task<PartyInvitationShareResult> ShareOkAsync(
        Guid ownerUserId, Domain.Party party, PartyInvitationGroup group, PartyInvitationDelivery delivery,
        string origin, bool replayed, CancellationToken cancellationToken)
    {
        // The link the row's generation derives — for a first share the current
        // one, for a replay exactly the link that click was given.
        var url = origin + PartyInvitationTokens.InvitationPath(_tokens.Derive(delivery.CapabilityId));
        var text = PartyInvitationShareText.Compose(await HostLanguageAsync(ownerUserId), party.Title, url);
        var current = await _parties.GetAsync(ownerUserId, party.Id, cancellationToken);
        return new(
            PartyInvitationOutcome.Ok,
            new PartyInvitationShareDto(
                delivery.Channel,
                delivery.Kind,
                delivery.Status,
                url,
                text,
                delivery.Channel == PartyInvitationDeliveryChannels.WhatsApp
                    ? PartyWhatsAppLink.Build(group.Phone, text)
                    : null,
                delivery.CreatedAt,
                replayed),
            current,
            current is null
                ? null
                : await _directory.ItemAsync(party.Id, current.Status, group.Id, cancellationToken));
    }

    private async Task<PartyInvitationShareResult> ShareRefusedAsync(
        Guid ownerUserId, Guid partyId, PartyInvitationOutcome outcome, string error,
        CancellationToken cancellationToken) =>
        new(outcome, Party: await _parties.GetAsync(ownerUserId, partyId, cancellationToken), Error: error);
}
