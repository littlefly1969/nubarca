using Microsoft.EntityFrameworkCore;
using NubArca.Api.Data;
using NubArca.Api.Domain;

namespace NubArca.Api.Party;

public sealed class PartyMessageService : IPartyMessageService
{
    private readonly AppDbContext _db;
    private readonly TimeProvider _clock;
    private readonly IPartyMessageAccessResolver _access;

    public PartyMessageService(
        AppDbContext db, TimeProvider clock, IPartyMessageAccessResolver access)
    {
        _db = db;
        _clock = clock;
        _access = access;
    }

    public async Task<PartyMessageSubmissionResult> SubmitAsync(
        PartyAccess access,
        string? displayName,
        string? text,
        Guid? participantId,
        CancellationToken cancellationToken = default)
    {
        // Normalise BEFORE measuring, so the limit applies to what will be
        // stored rather than to padding the guest cannot see.
        if (!PartyMessageText.TryNormalizeDisplayName(displayName, out var name))
        {
            return PartyMessageSubmissionResult.Fail(PartyMessageSubmissionError.InvalidDisplayName);
        }

        if (!PartyMessageText.TryNormalizeBody(text, out var body))
        {
            return PartyMessageSubmissionResult.Fail(PartyMessageSubmissionError.InvalidBody);
        }

        // A message with no party is unrepresentable — see PartyMessage. An
        // upload-token grant always carries the link it resolved from, so this
        // only fires against a hand-built PartyAccess.
        if (access.PartyAlbumLinkId is not Guid linkId)
        {
            return PartyMessageSubmissionResult.Fail(PartyMessageSubmissionError.InvalidBody);
        }

        var now = _clock.GetUtcNow().UtcDateTime;
        var status = access.RequireMessageApproval
            ? PartyMessageStatuses.Pending
            : PartyMessageStatuses.Visible;

        var message = new PartyMessage
        {
            Id = Guid.NewGuid(),
            PartyAlbumLinkId = linkId,
            AlbumId = access.AlbumId,
            OwnerUserId = access.OwnerUserId,
            PartyParticipantId = participantId,
            DisplayName = name,
            Body = body,
            Status = status,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.PartyMessages.Add(message);
        await _db.SaveChangesAsync(cancellationToken);

        return PartyMessageSubmissionResult.Ok(
            new PartyMessageSubmissionDto(message.Id, message.Status, message.CreatedAt));
    }

    public async Task<PartyMessageListDto?> ListForManagerAsync(
        Guid albumId, Guid actorUserId, CancellationToken cancellationToken = default)
    {
        var grant = await _access.ResolveAsync(albumId, actorUserId, cancellationToken);
        if (grant is null)
        {
            return null;
        }

        var link = await ActiveLinkAsync(grant.OwnerUserId, albumId, cancellationToken);
        if (link is null)
        {
            // Authorised, but there is no party running. An empty queue with
            // PartyActive=false is a different thing from "no such album", and
            // the owner UI says so.
            return new PartyMessageListDto(albumId, false, false, grant.IsOwner, []);
        }

        var items = await _db.PartyMessages
            .AsNoTracking()
            .Where(m => m.PartyAlbumLinkId == link.Id)
            .OrderByDescending(m => m.CreatedAt)
            .ThenByDescending(m => m.Id)
            .Select(m => new PartyMessageDto(
                m.Id,
                m.DisplayName,
                m.Body,
                m.Status,
                m.CreatedAt,
                m.ModeratedAt,
                m.Status == PartyMessageStatuses.Visible && m.HeroPromotedAt != null,
                m.HeroPromotedAt))
            .ToListAsync(cancellationToken);

        return new PartyMessageListDto(
            albumId, true, link.RequireMessageApproval, grant.IsOwner, items);
    }

    public async Task<PartyMessageMutation> SetStatusAsync(
        Guid albumId, Guid actorUserId, Guid messageId, string status,
        CancellationToken cancellationToken = default)
    {
        // Pending is a birth state only. Rejecting the target here keeps the
        // transition table honest at the one place that writes it.
        if (status is not (PartyMessageStatuses.Visible
            or PartyMessageStatuses.Hidden
            or PartyMessageStatuses.Rejected))
        {
            return PartyMessageMutation.InvalidTransition;
        }

        var found = await LoadForManagerAsync(albumId, actorUserId, messageId, cancellationToken);
        var message = found?.Message;
        if (message is null)
        {
            return PartyMessageMutation.NotFound;
        }

        message.Status = status;
        message.ModeratedAt = _clock.GetUtcNow().UtcDateTime;
        message.ModeratedByUserId = actorUserId;
        message.UpdatedAt = message.ModeratedAt.Value;
        await _db.SaveChangesAsync(cancellationToken);
        return PartyMessageMutation.Ok;
    }

    public async Task<PartyMessageMutation> SetHeroAsync(
        Guid albumId, Guid actorUserId, Guid messageId, bool hero,
        CancellationToken cancellationToken = default)
    {
        var found = await LoadForManagerAsync(albumId, actorUserId, messageId, cancellationToken);
        var message = found?.Message;
        if (message is null)
        {
            return PartyMessageMutation.NotFound;
        }

        // Only something the party can currently see may be put on the big card.
        // Demotion has no such gate: taking a card down must always work, even
        // for a message that has since been hidden.
        if (hero && message.Status != PartyMessageStatuses.Visible)
        {
            return PartyMessageMutation.InvalidTransition;
        }

        var now = _clock.GetUtcNow().UtcDateTime;
        if (hero)
        {
            // Re-promoting an already-Hero message keeps its original position
            // in the rotation rather than jumping it to the end of the queue.
            message.HeroPromotedAt ??= now;
            message.HeroPromotedByUserId ??= actorUserId;
        }
        else
        {
            message.HeroPromotedAt = null;
            message.HeroPromotedByUserId = null;
        }

        message.UpdatedAt = now;
        await _db.SaveChangesAsync(cancellationToken);
        return PartyMessageMutation.Ok;
    }

    public async Task<TvPartyMessagesDto?> GetTvProjectionAsync(
        Guid ownerUserId, Guid albumId, CancellationToken cancellationToken = default)
    {
        // ShowOnTv is re-read here for the same reason every other TV endpoint
        // re-reads it: turning an album off must empty the TV on the next poll.
        var albumOk = await _db.Albums
            .AsNoTracking()
            .AnyAsync(a => a.Id == albumId && a.OwnerUserId == ownerUserId && a.ShowOnTv,
                cancellationToken);
        if (!albumOk)
        {
            return null;
        }

        var link = await ActiveLinkAsync(ownerUserId, albumId, cancellationToken);
        if (link is null)
        {
            // TV-visible album, no party running: an empty feed, not a 404. The
            // TV shows no ribbon and keeps polling.
            return new TvPartyMessagesDto([]);
        }

        var messages = await _db.PartyMessages
            .AsNoTracking()
            .Where(m => m.PartyAlbumLinkId == link.Id
                && m.Status == PartyMessageStatuses.Visible)
            .OrderBy(m => m.CreatedAt)
            .ThenBy(m => m.Id)
            .Select(m => new TvPartyMessageDto(
                m.Id,
                m.DisplayName,
                m.Body,
                m.CreatedAt,
                m.HeroPromotedAt != null,
                m.HeroPromotedAt))
            .ToListAsync(cancellationToken);

        return new TvPartyMessagesDto(messages);
    }

    // The album's currently active party link, or null. "Active" is the same
    // predicate the rest of Party uses — enabled, not revoked, not expired —
    // evaluated now rather than remembered, which is what makes revocation take
    // messages off the TV without touching a single message row.
    private async Task<ActiveLink?> ActiveLinkAsync(
        Guid ownerUserId, Guid albumId, CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        return await _db.PartyAlbumLinks
            .AsNoTracking()
            .Where(p => p.AlbumId == albumId && p.OwnerUserId == ownerUserId
                && p.Enabled && p.RevokedAt == null
                && (p.ExpiresAt == null || p.ExpiresAt > now))
            .OrderByDescending(p => p.CreatedAt)
            .Select(p => new ActiveLink(p.Id, p.RequireMessageApproval))
            .FirstOrDefaultAsync(cancellationToken);
    }

    // Authorise, then resolve the message INSIDE the album's current party. The
    // scope is the reason a foreign message id cannot be probed through a route
    // the caller does legitimately manage: it is simply not in this party.
    private async Task<ManagedMessage?> LoadForManagerAsync(
        Guid albumId, Guid actorUserId, Guid messageId, CancellationToken cancellationToken)
    {
        var grant = await _access.ResolveAsync(albumId, actorUserId, cancellationToken);
        if (grant is null)
        {
            return null;
        }

        var link = await ActiveLinkAsync(grant.OwnerUserId, albumId, cancellationToken);
        if (link is null)
        {
            return null;
        }

        var message = await _db.PartyMessages
            .FirstOrDefaultAsync(
                m => m.Id == messageId && m.PartyAlbumLinkId == link.Id, cancellationToken);
        return message is null ? null : new ManagedMessage(message, grant);
    }

    private sealed record ActiveLink(Guid Id, bool RequireMessageApproval);

    private sealed record ManagedMessage(PartyMessage Message, PartyMessageManagerGrant Grant);
}
