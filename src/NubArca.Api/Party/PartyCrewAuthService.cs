using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using NubArca.Api.Audit;
using NubArca.Api.Auth.Recovery;
using NubArca.Api.Data;
using NubArca.Api.Domain;

namespace NubArca.Api.Party;

/// <summary>Why a pairing step was refused. Deliberately coarse on the wire.</summary>
public enum PartyCrewAuthError
{
    /// <summary>
    /// The invite, the challenge, the collaborator or the party is not usable.
    ///
    /// <para>ONE value for all of them, on purpose. Telling a caller whether a
    /// token was unknown, expired, revoked or belonged to a party that no
    /// longer exists is an enumeration oracle, and none of those answers helps
    /// the person who is legitimately stuck.</para>
    /// </summary>
    Unavailable,

    /// <summary>Wrong code. Says nothing about how wrong, or how many are left.</summary>
    InvalidCode,

    /// <summary>Too many wrong codes: this challenge is spent.</summary>
    TooManyAttempts,

    /// <summary>A resend was asked for before the interval elapsed.</summary>
    ResendTooSoon,

    /// <summary>Outbound mail is not configured, so no second factor can be sent.</summary>
    MailUnavailable,

    /// <summary>
    /// Mail IS configured and the provider refused this message.
    ///
    /// <para>Its own value, because it is a different fact and a different
    /// thing to tell somebody: nothing is misconfigured on their side, the code
    /// simply did not go, and retrying may work. Never a success.</para>
    /// </summary>
    DeliveryFailed,

    /// <summary>The slot freed up between the check and the write, or never existed.</summary>
    DeviceLimitReached,
}

public sealed record PartyCrewAuthResult<T>(T? Value, PartyCrewAuthError? Error)
{
    public static PartyCrewAuthResult<T> Ok(T value) => new(value, null);
    public static PartyCrewAuthResult<T> Fail(PartyCrewAuthError error) => new(default, error);
}

/// <summary>A started challenge and the raw token its browser must carry.</summary>
public sealed record PartyCrewChallengeStart(
    PartyCrewChallengeStartedDto View, string RawChallengeToken);

/// <summary>A completed pairing and the raw device token its browser must carry.</summary>
public sealed record PartyCrewPairing(
    PartyCrewVerifyResultDto Result, string? RawDeviceToken);

public interface IPartyCrewAuthService
{
    Task<PartyCrewAuthResult<PartyCrewChallengeStart>> StartAsync(
        string rawInviteToken, string? userAgent, string? ip, CancellationToken ct = default);

    /// <summary>
    /// What a challenge in progress is about, for a browser that reloaded.
    ///
    /// <para>The pairing pages have real URLs, and a person who refreshes the
    /// code screen must not be sent back to a link they no longer hold. The
    /// cookie is still there; this is how the page gets its words back.</para>
    /// </summary>
    Task<PartyCrewAuthResult<PartyCrewChallengeStartedDto>> ChallengeAsync(
        string rawChallengeToken, CancellationToken ct = default);

    Task<PartyCrewAuthError?> ResendAsync(string rawChallengeToken, CancellationToken ct = default);

    Task<PartyCrewAuthResult<PartyCrewPairing>> VerifyAsync(
        string rawChallengeToken, string code, string? userAgent, string? existingDeviceToken,
        string? ip, CancellationToken ct = default);

    Task<PartyCrewAuthResult<IReadOnlyList<PartyCrewDeviceDto>>> ChallengeDevicesAsync(
        string rawChallengeToken, CancellationToken ct = default);

    Task<PartyCrewAuthError?> RevokeDuringChallengeAsync(
        string rawChallengeToken, Guid grantId, string? ip, CancellationToken ct = default);

    Task<PartyCrewAuthResult<PartyCrewPairing>> CompleteAsync(
        string rawChallengeToken, string? userAgent, string? existingDeviceToken, string? ip,
        CancellationToken ct = default);
}

/// <summary>
/// The two factors, and the two-device limit.
///
/// <para><b>Why two factors at all.</b> A single personal link is one
/// forward away from being somebody else's access, and a party's collaborator
/// link travels through exactly the channels — a chat, a screenshot, a group —
/// where that happens. So possession of the link proves only WHICH collaborator
/// is pairing; possession of the mailbox the OWNER chose proves it is them. The
/// owner is never asked to approve a device, because an owner woken at midnight
/// by a party approves everything.</para>
///
/// <para><b>The code alone is not enough either.</b> It is bound to the
/// challenge, and the challenge lives in an HttpOnly path-scoped cookie in the
/// browser that asked. Somebody reading the six digits over a shoulder, or out
/// of a forwarded email, has nothing to type them into.</para>
///
/// <para><b>And the limit is real.</b> Two devices per collaborator, counted on
/// live grants and enforced by serialising on the collaborator's own row — not
/// by counting and hoping. Two phones finishing their codes in the same second
/// must not both be admitted, and a test drives exactly that against
/// PostgreSQL.</para>
/// </summary>
public sealed class PartyCrewAuthService : IPartyCrewAuthService
{
    private readonly AppDbContext _db;
    private readonly TimeProvider _clock;
    private readonly PartyCrewTokens _tokens;
    private readonly IEmailSender _email;
    private readonly IAuditLogger _audit;
    private readonly IOptionsMonitor<MailOptions> _mail;
    private readonly ILogger<PartyCrewAuthService> _log;

    public PartyCrewAuthService(
        AppDbContext db,
        TimeProvider clock,
        PartyCrewTokens tokens,
        IEmailSender email,
        IAuditLogger audit,
        IOptionsMonitor<MailOptions> mail,
        ILogger<PartyCrewAuthService> log)
    {
        _db = db;
        _clock = clock;
        _tokens = tokens;
        _email = email;
        _audit = audit;
        _mail = mail;
        _log = log;
    }

    // ── 1. The link ─────────────────────────────────────────────────────────

    public async Task<PartyCrewAuthResult<PartyCrewChallengeStart>> StartAsync(
        string rawInviteToken, string? userAgent, string? ip, CancellationToken ct = default)
    {
        if (!PartyCrewTokens.LooksLikeToken(rawInviteToken))
            return PartyCrewAuthResult<PartyCrewChallengeStart>.Fail(PartyCrewAuthError.Unavailable);
        if (!_email.IsEnabled)
            return PartyCrewAuthResult<PartyCrewChallengeStart>.Fail(PartyCrewAuthError.MailUnavailable);

        var now = _clock.GetUtcNow().UtcDateTime;
        var hash = PartyCrewTokens.Hash(rawInviteToken);

        // The invite, the collaborator and the party in ONE query. Every reason
        // to refuse collapses into the same empty result, so there is nothing to
        // read off a response.
        var found = await _db.PartyCollaboratorInvites
            .Where(i => i.TokenHash == hash && i.RevokedAt == null && i.ConsumedAt == null && i.ExpiresAt > now)
            .Join(_db.PartyCollaborators.Where(c => c.RevokedAt == null),
                i => i.PartyCollaboratorId, c => c.Id, (i, c) => new { Invite = i, Collaborator = c })
            .Join(_db.Parties, x => x.Collaborator.PartyId, p => p.Id,
                (x, p) => new { x.Invite, x.Collaborator, Party = p })
            .FirstOrDefaultAsync(ct);
        if (found is null)
            return PartyCrewAuthResult<PartyCrewChallengeStart>.Fail(PartyCrewAuthError.Unavailable);

        // HOW MANY CODES THIS PERSON HAS ALREADY BEEN SENT. Re-opening the link
        // is what resets a challenge's own budget, so without this second bound
        // there is no bound at all: a leaked link would be a way to put an
        // email in somebody's inbox on demand. Counted per COLLABORATOR, not
        // per address of origin — an address is not who is being written to.
        var recent = await _db.PartyCollaboratorAuthChallenges
            .CountAsync(
                c => c.PartyCollaboratorId == found.Collaborator.Id
                    && c.CreatedAt > now - PartyCrewLimits.SendWindow, ct);
        if (recent >= PartyCrewLimits.MaxChallengesPerCollaborator)
            return PartyCrewAuthResult<PartyCrewChallengeStart>.Fail(PartyCrewAuthError.ResendTooSoon);

        var otp = PartyCrewTokens.NewOtp();
        var rawChallenge = PartyCrewTokens.NewToken();
        var challenge = new PartyCollaboratorAuthChallenge
        {
            Id = Guid.NewGuid(),
            PartyCollaboratorId = found.Collaborator.Id,
            PartyCollaboratorInviteId = found.Invite.Id,
            ChallengeTokenHash = PartyCrewTokens.Hash(rawChallenge),
            CreatedAt = now,
            ExpiresAt = now.Add(PartyCrewLimits.ChallengeLifetime),
            OtpSentAt = now,
            OtpSendCount = 1,
        };
        challenge.OtpProof = _tokens.OtpProof(challenge.Id, otp);
        _db.PartyCollaboratorAuthChallenges.Add(challenge);
        await _db.SaveChangesAsync(ct);

        // THE MAIL DECIDES WHETHER THIS CHALLENGE EXISTS. A provider that
        // refuses the message leaves a person staring at "check your email"
        // for a code nobody sent, so a refusal takes the challenge with it.
        if (!await SendCodeAsync(found.Collaborator, found.Party, otp, ct))
        {
            await _db.PartyCollaboratorAuthChallenges
                .Where(c => c.Id == challenge.Id)
                .ExecuteDeleteAsync(ct);
            return PartyCrewAuthResult<PartyCrewChallengeStart>.Fail(PartyCrewAuthError.DeliveryFailed);
        }

        // AND ONLY NOW do earlier attempts end. Revoking first would mean a
        // failed send had destroyed a challenge somebody was still using: the
        // previous browser would be logged out by an email that never arrived.
        await _db.PartyCollaboratorAuthChallenges
            .Where(c => c.PartyCollaboratorId == found.Collaborator.Id
                && c.Id != challenge.Id
                && c.RevokedAt == null && c.CompletedAt == null)
            .ExecuteUpdateAsync(u => u.SetProperty(c => c.RevokedAt, _ => (DateTime?)now), ct);

        return PartyCrewAuthResult<PartyCrewChallengeStart>.Ok(new PartyCrewChallengeStart(
            new PartyCrewChallengeStartedDto(
                found.Party.Title,
                found.Collaborator.RoleKey,
                MaskEmail(found.Collaborator.Email),
                challenge.ExpiresAt),
            rawChallenge));
    }

    public async Task<PartyCrewAuthResult<PartyCrewChallengeStartedDto>> ChallengeAsync(
        string rawChallengeToken, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var state = await LiveChallengeAsync(rawChallengeToken, now, ct);
        if (state is null || state.Challenge.CompletedAt is not null)
            return PartyCrewAuthResult<PartyCrewChallengeStartedDto>.Fail(PartyCrewAuthError.Unavailable);

        // The same party-safe view the link produced: a name, a role, a masked
        // address. Reloading learns nothing that following the link did not.
        return PartyCrewAuthResult<PartyCrewChallengeStartedDto>.Ok(new PartyCrewChallengeStartedDto(
            state.Party.Title,
            state.Collaborator.RoleKey,
            MaskEmail(state.Collaborator.Email),
            state.Challenge.ExpiresAt));
    }

    public async Task<PartyCrewAuthError?> ResendAsync(string rawChallengeToken, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var state = await LiveChallengeAsync(rawChallengeToken, now, ct);
        if (state is null) return PartyCrewAuthError.Unavailable;
        if (state.Challenge.VerifiedAt is not null) return PartyCrewAuthError.Unavailable;
        if (!_email.IsEnabled) return PartyCrewAuthError.MailUnavailable;

        // The floor between two sends. It bounds both a mailbox being used as a
        // nuisance channel and an attacker farming codes for one challenge.
        if (now - state.Challenge.OtpSentAt < PartyCrewLimits.ResendInterval)
            return PartyCrewAuthError.ResendTooSoon;

        // And the ceiling on the whole challenge. The interval only spaces
        // them: without this, one link is ten emails, a minute apart, for as
        // long as the challenge lives.
        if (state.Challenge.OtpSendCount >= PartyCrewLimits.MaxOtpSendsPerChallenge)
            return PartyCrewAuthError.TooManyAttempts;

        var otp = PartyCrewTokens.NewOtp();
        var proof = _tokens.OtpProof(state.Challenge.Id, otp);

        // SENT BEFORE THE PROOF IS REPLACED, and this order is the whole fix.
        // The other way round — replace, save, then send — means a provider
        // that refuses has killed the code already in somebody's inbox and
        // delivered nothing to replace it: zero working codes, and no way
        // forward but a new link. Refused here, the old code still works and
        // the interval has not restarted, so they can simply ask again.
        if (!await SendCodeAsync(state.Collaborator, state.Party, otp, ct))
            return PartyCrewAuthError.DeliveryFailed;

        // Accepted. NOW the previous code stops matching.
        state.Challenge.OtpProof = proof;
        state.Challenge.OtpSentAt = now;
        state.Challenge.OtpSendCount++;
        await _db.SaveChangesAsync(ct);
        return null;
    }

    // ── 2. The code ─────────────────────────────────────────────────────────

    public async Task<PartyCrewAuthResult<PartyCrewPairing>> VerifyAsync(
        string rawChallengeToken, string code, string? userAgent, string? existingDeviceToken,
        string? ip, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var state = await LiveChallengeAsync(rawChallengeToken, now, ct);
        if (state is null) return PartyCrewAuthResult<PartyCrewPairing>.Fail(PartyCrewAuthError.Unavailable);

        var challenge = state.Challenge;
        if (challenge.CompletedAt is not null)
            return PartyCrewAuthResult<PartyCrewPairing>.Fail(PartyCrewAuthError.Unavailable);

        // Already verified: this is a retry of a step that succeeded, and the
        // person must not be asked for a second code because the product could
        // not count to two. Fall straight through to the device decision.
        if (challenge.VerifiedAt is not null)
            return await PairAsync(state, userAgent, existingDeviceToken, ip, now, ct);

        if (challenge.FailedAttempts >= PartyCrewLimits.MaxOtpAttempts)
            return PartyCrewAuthResult<PartyCrewPairing>.Fail(PartyCrewAuthError.TooManyAttempts);

        if (!_tokens.OtpMatches(challenge.Id, challenge.OtpProof, code ?? string.Empty))
        {
            // Counted atomically: two wrong guesses arriving together must both
            // be charged, or the limit is advisory.
            var attempts = await _db.PartyCollaboratorAuthChallenges
                .Where(c => c.Id == challenge.Id)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(c => c.FailedAttempts, c => c.FailedAttempts + 1)
                    .SetProperty(c => c.LastAttemptAt, _ => (DateTime?)now), ct);
            _ = attempts;
            return PartyCrewAuthResult<PartyCrewPairing>.Fail(
                challenge.FailedAttempts + 1 >= PartyCrewLimits.MaxOtpAttempts
                    ? PartyCrewAuthError.TooManyAttempts
                    : PartyCrewAuthError.InvalidCode);
        }

        challenge.VerifiedAt = now;
        challenge.LastAttemptAt = now;
        await _db.SaveChangesAsync(ct);

        return await PairAsync(state, userAgent, existingDeviceToken, ip, now, ct);
    }

    // ── 3. The slot ─────────────────────────────────────────────────────────

    public async Task<PartyCrewAuthResult<IReadOnlyList<PartyCrewDeviceDto>>> ChallengeDevicesAsync(
        string rawChallengeToken, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var state = await LiveChallengeAsync(rawChallengeToken, now, ct);
        // A challenge that has not passed its code may not read anything: the
        // device list is the collaborator's own data.
        if (state?.Challenge.VerifiedAt is null || state.Challenge.CompletedAt is not null)
            return PartyCrewAuthResult<IReadOnlyList<PartyCrewDeviceDto>>.Fail(PartyCrewAuthError.Unavailable);

        return PartyCrewAuthResult<IReadOnlyList<PartyCrewDeviceDto>>.Ok(
            await DevicesAsync(state.Collaborator.Id, null, now, ct));
    }

    public async Task<PartyCrewAuthError?> RevokeDuringChallengeAsync(
        string rawChallengeToken, Guid grantId, string? ip, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var state = await LiveChallengeAsync(rawChallengeToken, now, ct);
        if (state?.Challenge.VerifiedAt is null || state.Challenge.CompletedAt is not null)
            return PartyCrewAuthError.Unavailable;

        // THE GRANT MUST BE THIS COLLABORATOR'S OWN. A verified challenge is
        // authority over one identity, not over a grant id somebody guessed:
        // the collaborator is part of the WHERE clause, so another
        // collaborator's device — or the same person's device on a different
        // party — matches nothing and answers the same not-found.
        var revoked = await _db.PartyCollaboratorDeviceGrants
            .Where(g => g.Id == grantId
                && g.PartyCollaboratorId == state.Collaborator.Id
                && g.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(g => g.RevokedAt, _ => (DateTime?)now), ct);
        if (revoked == 0) return PartyCrewAuthError.Unavailable;

        await _audit.LogAsync(
            AuditActor.Crew(state.Collaborator.Id),
            "party.crew.device.revoke", "PartyCollaboratorDeviceGrant",
            grantId, ip, new
            {
                partyId = state.Party.Id,
                during = "pairing",
            }, ct);
        return null;
    }

    public async Task<PartyCrewAuthResult<PartyCrewPairing>> CompleteAsync(
        string rawChallengeToken, string? userAgent, string? existingDeviceToken, string? ip,
        CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var state = await LiveChallengeAsync(rawChallengeToken, now, ct);
        if (state?.Challenge.VerifiedAt is null || state.Challenge.CompletedAt is not null)
            return PartyCrewAuthResult<PartyCrewPairing>.Fail(PartyCrewAuthError.Unavailable);

        return await PairAsync(state, userAgent, existingDeviceToken, ip, now, ct);
    }

    /// <summary>
    /// Create the device grant, or say the limit is reached — atomically.
    ///
    /// <para><b>Why a lock and not a count.</b> "Count the grants, then insert
    /// one" is two statements. Two devices whose codes are verified in the same
    /// instant both count one existing grant, both conclude there is room, and
    /// both insert: three devices, from a limit that was checked twice and
    /// enforced never. So the transaction OPENS by writing the collaborator's
    /// own row — a conditional self-assignment that changes nothing and exists
    /// to take that row's write lock — and every pairing for that collaborator
    /// is thereby ordered. The second one's count sees the first one's grant.
    /// It is the discipline <c>PartyDisplayService.MintAsync</c> uses on a
    /// television's session row, applied to a person.</para>
    ///
    /// <para>Opening with a write also means the transaction never upgrades a
    /// shared lock to an exclusive one, which is the shape SQLite refuses to
    /// wait on — so the same code is exercised by both test databases.</para>
    /// </summary>
    private async Task<PartyCrewAuthResult<PartyCrewPairing>> PairAsync(
        ChallengeState state, string? userAgent, string? existingDeviceToken, string? ip,
        DateTime now, CancellationToken ct)
    {
        var collaboratorId = state.Collaborator.Id;
        var owned = _db.Database.CurrentTransaction is null;
        var tx = owned ? await _db.Database.BeginTransactionAsync(ct) : null;
        try
        {
            var claimed = await _db.PartyCollaborators
                .Where(c => c.Id == collaboratorId && c.RevokedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.UpdatedAt, c => c.UpdatedAt), ct);
            if (claimed == 0)
            {
                if (owned) await tx!.RollbackAsync(ct);
                return PartyCrewAuthResult<PartyCrewPairing>.Fail(PartyCrewAuthError.Unavailable);
            }

            // The browser may already BE one of the two: a second pairing of a
            // device that already holds this grant is idempotent and consumes
            // nothing. Checked inside the lock, because otherwise it races the
            // very insert it is trying to avoid.
            PartyCrewDevice? device = null;
            if (PartyCrewTokens.LooksLikeToken(existingDeviceToken))
            {
                var deviceHash = PartyCrewTokens.Hash(existingDeviceToken!);
                device = await _db.PartyCrewDevices.FirstOrDefaultAsync(
                    d => d.TokenHash == deviceHash && d.RevokedAt == null && d.ExpiresAt > now, ct);
            }

            if (device is not null)
            {
                var already = await _db.PartyCollaboratorDeviceGrants.FirstOrDefaultAsync(
                    g => g.PartyCrewDeviceId == device.Id && g.PartyCollaboratorId == collaboratorId, ct);
                if (already is { RevokedAt: null })
                {
                    if (!await ClaimAsync(state, now, ct))
                    {
                        var lost = await LostAsync(tx, owned, ct);
                        tx = null;
                        return lost;
                    }
                    already.LastUsedAt = now;
                    await _db.SaveChangesAsync(ct);
                    if (owned) { await tx!.CommitAsync(ct); tx = null; }
                    return PartyCrewAuthResult<PartyCrewPairing>.Ok(
                        new PartyCrewPairing(await PairedAsync(state, ct), null));
                }
            }

            var active = await _db.PartyCollaboratorDeviceGrants
                .Where(g => g.PartyCollaboratorId == collaboratorId && g.RevokedAt == null)
                .Where(g => _db.PartyCrewDevices.Any(
                    d => d.Id == g.PartyCrewDeviceId && d.RevokedAt == null && d.ExpiresAt > now))
                .CountAsync(ct);

            if (active >= PartyCrewLimits.MaxDevicesPerCollaborator)
            {
                // NOT an error, and nothing is written. The challenge stays
                // verified so this same browser can free a slot and come back
                // without a second code — the limit is the product's rule, and
                // re-proving identity for it would be the product's cost
                // charged to the person.
                // Finished HERE, and the handle dropped: everything below runs
                // outside it, and the catch must not try to end it again.
                if (owned) { await tx!.RollbackAsync(ct); tx = null; }
                var devices = await DevicesAsync(collaboratorId, null, now, ct);
                return PartyCrewAuthResult<PartyCrewPairing>.Ok(new PartyCrewPairing(
                    new PartyCrewVerifyResultDto(
                        PartyCrewVerifyOutcome.DeviceLimitReached,
                        null, null, null, null, devices),
                    null));
            }

            // CLAIMED HERE, and not before: a challenge that meets the device
            // limit must stay verified so the person can free a slot and finish
            // without a second code. Claiming above and rolling back would also
            // work, but only because the rollback undoes it — this way the
            // order says what the product means.
            if (!await ClaimAsync(state, now, ct))
            {
                var lost = await LostAsync(tx, owned, ct);
                tx = null;
                return lost;
            }

            string? rawDeviceToken = null;
            if (device is null)
            {
                rawDeviceToken = PartyCrewTokens.NewToken();
                device = new PartyCrewDevice
                {
                    Id = Guid.NewGuid(),
                    TokenHash = PartyCrewTokens.Hash(rawDeviceToken),
                    DeviceLabel = PartyCrewDeviceLabel.From(userAgent),
                    UserAgent = Truncate(userAgent, PartyCrewLimits.MaxUserAgentLength),
                    CreatedAt = now,
                    LastSeenAt = now,
                    ExpiresAt = now.Add(PartyCrewLimits.DeviceLifetime),
                };
                _db.PartyCrewDevices.Add(device);
            }

            // A revoked grant for this pair is REUSED rather than duplicated:
            // the unique index is on (device, collaborator) regardless of
            // revocation, so a second row would be refused by the database.
            var grant = await _db.PartyCollaboratorDeviceGrants.FirstOrDefaultAsync(
                g => g.PartyCrewDeviceId == device.Id && g.PartyCollaboratorId == collaboratorId, ct);
            if (grant is null)
            {
                grant = new PartyCollaboratorDeviceGrant
                {
                    Id = Guid.NewGuid(),
                    PartyCrewDeviceId = device.Id,
                    PartyCollaboratorId = collaboratorId,
                    CreatedAt = now,
                };
                _db.PartyCollaboratorDeviceGrants.Add(grant);
            }
            else
            {
                grant.RevokedAt = null;
                grant.CreatedAt = now;
            }

            grant.LastUsedAt = now;
            await _db.SaveChangesAsync(ct);
            if (owned) { await tx!.CommitAsync(ct); tx = null; }

            await _audit.LogAsync(
                AuditActor.Crew(collaboratorId),
                "party.crew.device.pair", "PartyCollaboratorDeviceGrant",
                grant.Id, ip, new
                {
                    partyId = state.Party.Id,
                    role = state.Collaborator.RoleKey,
                }, ct);

            return PartyCrewAuthResult<PartyCrewPairing>.Ok(
                new PartyCrewPairing(await PairedAsync(state, ct), rawDeviceToken));
        }
        catch
        {
            if (owned && tx is not null) await tx.RollbackAsync(ct);
            throw;
        }
    }

    /// <summary>Spend the invite and the challenge together: one link, one device.</summary>
    /// <summary>
    /// Spend the challenge and the invite — once, and provably once.
    ///
    /// <para><b>Why this is a conditional UPDATE and not an assignment.</b> The
    /// challenge was read at the top of the request, so two requests carrying
    /// the same cookie both read <c>CompletedAt == null</c> and both believe
    /// they may finish. Writing the field from a tracked entity lets both
    /// succeed: one challenge, two devices, from a limit that was checked and
    /// never enforced. Written as a WHERE the database evaluates, exactly one
    /// of them changes a row — and the other is told it lost.</para>
    ///
    /// <para>The invite is claimed the same way and for the same reason. Its
    /// result was previously ignored, which made "already consumed" mean
    /// "carry on and create another grant".</para>
    ///
    /// <para>Returns false when either claim found nothing. The caller rolls
    /// back: no device, no grant, no session.</para>
    /// </summary>
    private async Task<bool> ClaimAsync(ChallengeState state, DateTime now, CancellationToken ct)
    {
        var challenge = await _db.PartyCollaboratorAuthChallenges
            .Where(c => c.Id == state.Challenge.Id
                && c.CompletedAt == null
                && c.RevokedAt == null
                && c.ExpiresAt > now)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.CompletedAt, _ => (DateTime?)now), ct);
        if (challenge != 1) return false;

        var invite = await _db.PartyCollaboratorInvites
            .Where(i => i.Id == state.Challenge.PartyCollaboratorInviteId && i.ConsumedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.ConsumedAt, _ => (DateTime?)now), ct);
        if (invite != 1) return false;

        // The tracked copy follows the database, so anything reading it later
        // in this request sees what actually happened.
        state.Challenge.CompletedAt = now;
        return true;
    }

    private async Task<PartyCrewVerifyResultDto> PairedAsync(ChallengeState state, CancellationToken ct)
    {
        var capabilities = await _db.PartyCollaboratorGrants
            .Where(g => g.PartyCollaboratorId == state.Collaborator.Id)
            .Select(g => g.CapabilityKey)
            .OrderBy(k => k)
            .ToListAsync(ct);
        return new PartyCrewVerifyResultDto(
            PartyCrewVerifyOutcome.Paired,
            state.Party.Id, state.Party.Title, state.Collaborator.RoleKey, capabilities, null);
    }

    // ── Shared ──────────────────────────────────────────────────────────────

    private sealed record ChallengeState(
        PartyCollaboratorAuthChallenge Challenge,
        PartyCollaborator Collaborator,
        Domain.Party Party);

    private async Task<ChallengeState?> LiveChallengeAsync(
        string? rawChallengeToken, DateTime now, CancellationToken ct)
    {
        if (!PartyCrewTokens.LooksLikeToken(rawChallengeToken)) return null;
        var hash = PartyCrewTokens.Hash(rawChallengeToken!);

        var found = await _db.PartyCollaboratorAuthChallenges
            .Where(c => c.ChallengeTokenHash == hash && c.RevokedAt == null && c.ExpiresAt > now)
            .Join(_db.PartyCollaborators.Where(c => c.RevokedAt == null),
                c => c.PartyCollaboratorId, col => col.Id, (c, col) => new { Challenge = c, Collaborator = col })
            .Join(_db.Parties, x => x.Collaborator.PartyId, p => p.Id,
                (x, p) => new { x.Challenge, x.Collaborator, Party = p })
            .FirstOrDefaultAsync(ct);

        return found is null
            ? null
            : new ChallengeState(found.Challenge, found.Collaborator, found.Party);
    }

    /// <summary>
    /// The answer for a request that lost the claim: nothing written, and the
    /// same generic refusal every other dead credential gets.
    /// </summary>
    private static async Task<PartyCrewAuthResult<PartyCrewPairing>> LostAsync(
        IDbContextTransaction? tx, bool owned, CancellationToken ct)
    {
        if (owned && tx is not null) await tx.RollbackAsync(ct);
        return PartyCrewAuthResult<PartyCrewPairing>.Fail(PartyCrewAuthError.Unavailable);
    }

    /// <summary>
    /// This collaborator's live devices, oldest first.
    ///
    /// <para>Written as a filtered select with a correlated lookup rather than
    /// a <c>Join</c> projecting straight into the DTO: the join form is not
    /// translatable on every provider the product runs on, and a query that
    /// works on PostgreSQL and throws on SQLite is a query that fails in a
    /// test rather than in review.</para>
    /// </summary>
    internal async Task<IReadOnlyList<PartyCrewDeviceDto>> DevicesAsync(
        Guid collaboratorId, Guid? currentDeviceId, DateTime now, CancellationToken ct)
    {
        var rows = await _db.PartyCollaboratorDeviceGrants
            .Where(g => g.PartyCollaboratorId == collaboratorId && g.RevokedAt == null)
            .Where(g => _db.PartyCrewDevices.Any(
                d => d.Id == g.PartyCrewDeviceId && d.RevokedAt == null && d.ExpiresAt > now))
            .OrderBy(g => g.CreatedAt)
            .Select(g => new
            {
                g.Id,
                g.PartyCrewDeviceId,
                g.CreatedAt,
                g.LastUsedAt,
                Label = _db.PartyCrewDevices
                    .Where(d => d.Id == g.PartyCrewDeviceId)
                    .Select(d => d.DeviceLabel)
                    .FirstOrDefault(),
            })
            .ToListAsync(ct);

        return [.. rows.Select(r => new PartyCrewDeviceDto(
            r.Id,
            r.Label ?? "Browser",
            r.CreatedAt,
            r.LastUsedAt,
            currentDeviceId is not null && r.PartyCrewDeviceId == currentDeviceId))];
    }

    /// <summary>
    /// Hand the code to the mail subsystem, and say whether it took it.
    ///
    /// <para>The return value is the whole point. <c>IsEnabled</c> says the
    /// installation is CONFIGURED for mail; it says nothing about whether this
    /// message was accepted. A provider that refuses — a bad credential, a
    /// blocked recipient, a relay that is down — used to produce a screen that
    /// said "check your email" about a code that does not exist, which is the
    /// worst possible answer: the person waits, retries, and concludes the
    /// product is broken rather than the mail.</para>
    /// </summary>
    private async Task<bool> SendCodeAsync(
        PartyCollaborator collaborator, Domain.Party party, string otp, CancellationToken ct)
    {
        var language = await _db.Users
            .Where(u => u.Id == party.OwnerUserId)
            .Select(u => u.UiLanguage)
            .FirstOrDefaultAsync(ct);

        var message = PartyCrewEmail.Compose(
            language ?? "it", collaborator.Email, collaborator.DisplayName,
            party.Title, collaborator.RoleKey, otp);

        // The code is never logged, and neither is the address. A failure to
        // deliver is an operational fact; its contents are not.
        var accepted = await _email.SendAsync(message, ct);
        if (!accepted)
        {
            // The collaborator by ID, and nothing else: not the code, not the
            // address, not the provider's complaint.
            _log.LogWarning(
                "Party Crew one-time code could not be handed to the mail server for collaborator {Collaborator}.",
                collaborator.Id);
        }
        return accepted;
    }

    /// <summary>
    /// <c>l••••@example.com</c>: enough to recognise a mailbox you own, not
    /// enough to learn one you do not. A forwarded link must not leak an address.
    /// </summary>
    internal static string MaskEmail(string email)
    {
        var at = email.IndexOf('@');
        if (at <= 0) return "•••";
        var local = email[..at];
        var domain = email[(at + 1)..];
        var head = local[..1];
        return $"{head}{new string('•', Math.Clamp(local.Length - 1, 1, 4))}@{domain}";
    }

    private static string? Truncate(string? value, int max) =>
        string.IsNullOrWhiteSpace(value) ? null
            : value.Length <= max ? value : value[..max];

    private string? Origin => PartyLinkPreview.Origin(_mail.CurrentValue.PublicOrigin);
}

/// <summary>
/// A name for a device that a person recognises and nobody can track.
///
/// <para>Derived from the coarse platform words in a user agent, and nothing
/// else. No canvas, no fonts, no screen size, no plugin list — a fingerprint
/// would identify this browser across every site it visits, to solve the much
/// smaller problem of telling two rows apart in a list of two.</para>
/// </summary>
public static class PartyCrewDeviceLabel
{
    public static string From(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent)) return "Browser";
        var ua = userAgent;
        var platform =
            ua.Contains("iPhone", StringComparison.OrdinalIgnoreCase) ? "iPhone"
            : ua.Contains("iPad", StringComparison.OrdinalIgnoreCase) ? "iPad"
            : ua.Contains("Android", StringComparison.OrdinalIgnoreCase) ? "Android"
            : ua.Contains("Macintosh", StringComparison.OrdinalIgnoreCase) ? "Mac"
            : ua.Contains("Windows", StringComparison.OrdinalIgnoreCase) ? "Windows"
            : ua.Contains("Linux", StringComparison.OrdinalIgnoreCase) ? "Linux"
            : null;
        var browser =
            ua.Contains("Edg/", StringComparison.Ordinal) ? "Edge"
            : ua.Contains("OPR/", StringComparison.Ordinal) ? "Opera"
            : ua.Contains("Firefox/", StringComparison.Ordinal) ? "Firefox"
            : ua.Contains("Chrome/", StringComparison.Ordinal) ? "Chrome"
            : ua.Contains("Safari/", StringComparison.Ordinal) ? "Safari"
            : null;

        return (platform, browser) switch
        {
            (null, null) => "Browser",
            (not null, null) => platform!,
            (null, not null) => browser!,
            _ => $"{browser} su {platform}",
        };
    }
}
