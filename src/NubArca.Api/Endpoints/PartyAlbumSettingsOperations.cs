using NubArca.Api.Audit;
using NubArca.Api.Domain;
using NubArca.Api.Party;

namespace NubArca.Api.Endpoints;

/// <summary>
/// The album-level party settings, written once and called from two places.
///
/// <para>The HOST's routes under <c>/api/albums/{id}/party-*</c> and the Party
/// Crew façade under <c>/api/party-crew/*</c> perform the SAME operation with
/// the same validation, the same ranges and the same audit lines. Copying these
/// bodies into the façade would have worked exactly once: the first range that
/// changed on one side and not the other would be a party where the host and
/// their co-organizer are shown the same switch and get different
/// answers.</para>
///
/// <para><b>The authority and the actor are different things here, and both are
/// recorded.</b> <c>ownerUserId</c> is whose album it is and whose permissions
/// bounded the request; <c>actor</c> is who actually did it. For a host they are
/// the same person. For a collaborator the album is still the host's — a
/// collaborator owns nothing — but the audit names the collaborator, because
/// "the host turned the party on" would be false.</para>
/// </summary>
internal static class PartyAlbumSettingsOperations
{
    /// <summary>Public party mode on or off, with its three audited sub-decisions.</summary>
    internal static async Task<IResult> SetPartyModeAsync(
        IPartyLinkService party,
        IAuditLogger audit,
        Guid ownerUserId,
        AuditActor actor,
        Guid albumId,
        SetAlbumPartyModeRequest? body,
        string? ip,
        CancellationToken ct)
    {
        if (body is null) return Results.BadRequest(new { error = "Missing request body." });

        if (body.Enabled)
        {
            // Captured first, so an approval-mode change can be audited as a
            // change rather than as part of enabling.
            var before = await party.GetOwnerStatusAsync(ownerUserId, albumId, ct);
            var enabled = await party.EnableAsync(
                ownerUserId, albumId, ownerUserId, body.UploadEnabled, body.RequireUploadApproval,
                body.RequireMessageApproval, ct);
            if (enabled is null) return Results.NotFound();

            await audit.LogAsync(actor, AuditActions.PartyEnable, AuditEntityTypes.PartyAlbum,
                enabled.LinkId, ip, new { albumId, uploadEnabled = body.UploadEnabled }, ct);

            if (body.RequireUploadApproval is bool wantApproval
                && (before is null || before.RequireUploadApproval != wantApproval))
            {
                await audit.LogAsync(
                    actor,
                    wantApproval ? AuditActions.PartyApprovalModeEnable : AuditActions.PartyApprovalModeDisable,
                    AuditEntityTypes.PartyAlbum, enabled.LinkId, ip, new { albumId }, ct);
            }

            // Greetings are a separate decision from photographs and get their
            // own line, so "the host started reading greetings first" is
            // answerable without inferring it from a photo event.
            if (body.RequireMessageApproval is bool wantMessageApproval
                && (before is null || before.RequireMessageApproval != wantMessageApproval))
            {
                await audit.LogAsync(
                    actor,
                    wantMessageApproval
                        ? AuditActions.PartyMessageApprovalModeEnable
                        : AuditActions.PartyMessageApprovalModeDisable,
                    AuditEntityTypes.PartyAlbum, enabled.LinkId, ip, new { albumId }, ct);
            }
        }
        else
        {
            if (!await party.DisableAsync(ownerUserId, albumId, ct)) return Results.NotFound();
            await audit.LogAsync(actor, AuditActions.PartyRevoke, AuditEntityTypes.PartyAlbum,
                albumId, ip, new { albumId }, ct);
        }

        var status = await party.GetOwnerStatusAsync(ownerUserId, albumId, ct);
        return status is null ? Results.NotFound() : Results.Ok(status);
    }

    /// <summary>
    /// The slideshow's timings and the per-guest quotas.
    ///
    /// <para>Validated against the SAME ranges the client shows, and an
    /// out-of-range value is a 400 rather than a silent clamp: a host who typed
    /// 600 seconds should be told, not quietly given 60.</para>
    /// </summary>
    internal static async Task<IResult> SetSlideshowSettingsAsync(
        IPartyLinkService party,
        Guid ownerUserId,
        Guid albumId,
        SetPartySlideshowSettingsRequest? body,
        CancellationToken ct)
    {
        if (body is null) return Results.BadRequest(new { error = "Missing request body." });

        if (body.PhotoSlideSeconds is int photo && !PartySlideshowDefaults.IsValidPhotoSeconds(photo))
            return Results.BadRequest(new { error = "photoSlideSeconds out of range." });
        if (body.MaxVideoSlideSeconds is int video && !PartySlideshowDefaults.IsValidMaxVideoSeconds(video))
            return Results.BadRequest(new { error = "maxVideoSlideSeconds out of range." });
        if (body.MaxPhotoUploadsPerParticipant is int maxPhotos && !PartySlideshowDefaults.IsValidQuota(maxPhotos))
            return Results.BadRequest(new { error = "maxPhotoUploadsPerParticipant out of range." });
        if (body.MaxVideoUploadsPerParticipant is int maxVideos && !PartySlideshowDefaults.IsValidQuota(maxVideos))
            return Results.BadRequest(new { error = "maxVideoUploadsPerParticipant out of range." });
        if (body.MaxMessagesPerParticipant is int maxMessages && !PartySlideshowDefaults.IsValidQuota(maxMessages))
            return Results.BadRequest(new { error = "maxMessagesPerParticipant out of range." });

        var ok = await party.UpdateSlideshowSettingsAsync(
            ownerUserId, albumId,
            body.PhotoSlideSeconds, body.MaxVideoSlideSeconds,
            body.MaxPhotoUploadsPerParticipant, body.MaxVideoUploadsPerParticipant,
            body.MaxMessagesPerParticipant, ct);
        if (!ok) return Results.NotFound();

        var status = await party.GetOwnerStatusAsync(ownerUserId, albumId, ct);
        return status is null ? Results.NotFound() : Results.Ok(status);
    }

    /// <summary>
    /// WHICH OF THE THREE CONTRIBUTIONS this party takes, and whether each
    /// needs approving.
    ///
    /// <para>One route because they are one decision a host makes on one card,
    /// and every field is optional because they are three independent
    /// switches: a client that only knows about photographs must not be able to
    /// close the guest book by saving the form it does know about.</para>
    ///
    /// <para>Deliberately separate from <see cref="SetPartyModeAsync"/>, which
    /// is the party's own on/off and can publish a draft and mint a capability.
    /// Switching the guest book on is not a lifecycle event, and must never be
    /// able to become one.</para>
    ///
    /// <para>Each decision gets its OWN audit line, and only when it actually
    /// changed. "The host stopped taking greetings" and "the host started
    /// reading them first" are different facts, and a trail that made you infer
    /// one from the other would not be a trail.</para>
    /// </summary>
    internal static async Task<IResult> SetContributionSettingsAsync(
        IPartyLinkService party,
        IAuditLogger audit,
        Guid ownerUserId,
        AuditActor actor,
        Guid albumId,
        PartyContributionSettingsRequest? body,
        string? ip,
        CancellationToken ct)
    {
        if (body is null) return Results.BadRequest(new { error = "Missing request body." });

        // Read FIRST, so each line below can be written only for a decision
        // that really moved. Without this every save would audit six changes.
        var before = await party.GetOwnerStatusAsync(ownerUserId, albumId, ct);
        if (before is null) return Results.NotFound();

        var ok = await party.UpdateContributionSettingsAsync(
            ownerUserId, albumId,
            body.UploadEnabled, body.RequireUploadApproval,
            body.SlideshowMessagesEnabled, body.RequireMessageApproval,
            body.GuestbookEnabled, body.RequireGuestbookApproval, ct);
        if (!ok)
        {
            // No active link. The host has to open the party before deciding
            // what it takes, and saying so is better than silently writing
            // switches onto a revoked capability.
            return Results.NotFound();
        }

        await AuditSwitchAsync(audit, actor, albumId, ip, ct,
            before.UploadEnabled, body.UploadEnabled,
            AuditActions.PartyEnable, AuditActions.PartyRevoke);
        await AuditSwitchAsync(audit, actor, albumId, ip, ct,
            before.RequireUploadApproval, body.RequireUploadApproval,
            AuditActions.PartyApprovalModeEnable, AuditActions.PartyApprovalModeDisable);
        await AuditSwitchAsync(audit, actor, albumId, ip, ct,
            before.SlideshowMessagesEnabled, body.SlideshowMessagesEnabled,
            AuditActions.PartySlideshowMessagesEnable, AuditActions.PartySlideshowMessagesDisable);
        await AuditSwitchAsync(audit, actor, albumId, ip, ct,
            before.RequireMessageApproval, body.RequireMessageApproval,
            AuditActions.PartyMessageApprovalModeEnable, AuditActions.PartyMessageApprovalModeDisable);
        await AuditSwitchAsync(audit, actor, albumId, ip, ct,
            before.GuestbookEnabled, body.GuestbookEnabled,
            AuditActions.PartyGuestbookEnable, AuditActions.PartyGuestbookDisable);
        await AuditSwitchAsync(audit, actor, albumId, ip, ct,
            before.RequireGuestbookApproval, body.RequireGuestbookApproval,
            AuditActions.PartyGuestbookApprovalModeEnable, AuditActions.PartyGuestbookApprovalModeDisable);

        var status = await party.GetOwnerStatusAsync(ownerUserId, albumId, ct);
        return status is null ? Results.NotFound() : Results.Ok(status);
    }

    /// <summary>One line, and only when the switch actually moved.</summary>
    private static Task AuditSwitchAsync(
        IAuditLogger audit, AuditActor actor, Guid albumId, string? ip, CancellationToken ct,
        bool before, bool? requested, string onAction, string offAction)
        => requested is bool next && next != before
            ? audit.LogAsync(
                actor, next ? onAction : offAction, AuditEntityTypes.PartyAlbum,
                albumId, ip, new { albumId }, ct)
            : Task.CompletedTask;

    /// <summary>The hosted game's rules.</summary>
    internal static async Task<IResult> SetGameSettingsAsync(
        IPartyLinkService party,
        Guid ownerUserId,
        Guid albumId,
        PartyGameSettingsRequest? body,
        CancellationToken ct)
    {
        if (body is null || !PartyChallengeDefaults.IsValid(
            body.MinChallengeIntervalSeconds, body.MaxChallengeIntervalSeconds,
            body.VotesPerGuest, body.MaxChallengesPerSession))
        {
            return Results.BadRequest(new { error = "invalid_party_game_settings" });
        }

        var ok = await party.UpdateGameSettingsAsync(
            ownerUserId, albumId, body.GameEnabled,
            body.MinChallengeIntervalSeconds, body.MaxChallengeIntervalSeconds,
            body.VotesPerGuest, body.MaxChallengesPerSession, body.PriorityVotingEnabled, ct);
        if (!ok) return Results.NotFound();

        return Results.Ok(await party.GetOwnerStatusAsync(ownerUserId, albumId, ct));
    }
}
