using Microsoft.EntityFrameworkCore;
using NubArca.Api.Auth.Recovery;
using NubArca.Api.Data;
using NubArca.Api.Domain;

namespace NubArca.Api.Albums.Sharing;

/// <summary>
/// Proving you are on the owner's list, for a second-factor share.
///
/// <para>Separate from <see cref="IAlbumShareService"/> because it answers a
/// different question with different collaborators: that one decides what a
/// link allows, this one decides who is holding it. Only this half needs to
/// send mail.</para>
/// </summary>
public interface IAlbumShareAuth
{
    /// <summary>
    /// Sends a code, if the address is one the owner listed.
    ///
    /// <para>The result is DELIBERATELY the same either way. A link that
    /// answered differently for a listed and an unlisted address would be a way
    /// to enumerate the owner's guest list, which is exactly the information
    /// the list exists to protect.</para>
    /// </summary>
    Task<AlbumShareChallengeOutcome> ChallengeAsync(
        string? token, string? email, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks a code and, on success, returns the raw device token to put in an
    /// HTTP-only cookie. Null on every failure.
    /// </summary>
    Task<AlbumShareVerifyOutcome> VerifyAsync(
        string? token, string? email, string? code, string? userAgent,
        CancellationToken cancellationToken = default);
}

public enum AlbumShareChallengeOutcome
{
    /// <summary>Accepted for processing. Says nothing about whether mail was sent.</summary>
    Accepted,
    /// <summary>The link itself does not open. The only refusal a caller ever sees here.</summary>
    NotFound,
    /// <summary>A code went out moments ago; asking again that fast is a mistake, not an attack.</summary>
    TooSoon,
}

public sealed record AlbumShareVerifyOutcome(
    bool Verified, string? DeviceToken = null, string? ErrorCode = null);

public sealed class AlbumShareAuth : IAlbumShareAuth
{
    private readonly AppDbContext _db;
    private readonly AlbumShareTokens _tokens;
    private readonly IEmailSender _email;
    private readonly TimeProvider _clock;
    private readonly ILogger<AlbumShareAuth> _logger;

    public AlbumShareAuth(
        AppDbContext db, AlbumShareTokens tokens, IEmailSender email,
        TimeProvider clock, ILogger<AlbumShareAuth> logger)
    {
        _db = db;
        _tokens = tokens;
        _email = email;
        _clock = clock;
        _logger = logger;
    }

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    public async Task<AlbumShareChallengeOutcome> ChallengeAsync(
        string? token, string? email, CancellationToken cancellationToken = default)
    {
        var link = await LiveLinkAsync(token, cancellationToken);
        if (link is null) return AlbumShareChallengeOutcome.NotFound;

        // A malformed address gets ACCEPTED, not rejected. Validating it here
        // would tell a caller that the address shape mattered, and the answer
        // for "not on the list" has to be the same answer.
        var normalized = AlbumShareTokens.NormalizeEmail(email);
        var guest = !AlbumShareTokens.IsPlausibleEmail(email)
            ? null
            : await _db.AlbumShareGuests.FirstOrDefaultAsync(
                g => g.AlbumShareLinkId == link.Id && g.Email == normalized && g.RevokedAt == null,
                cancellationToken);
        if (guest is null) return AlbumShareChallengeOutcome.Accepted;

        var now = Now;
        var challenge = await _db.AlbumShareChallenges
            .FirstOrDefaultAsync(c => c.AlbumShareGuestId == guest.Id, cancellationToken);

        if (challenge?.LastSentAt is DateTime sent
            && now - sent < AlbumShareLimits.ResendInterval)
        {
            return AlbumShareChallengeOutcome.TooSoon;
        }

        var code = AlbumShareTokens.NewOtp();
        if (challenge is null)
        {
            challenge = new AlbumShareChallenge
            {
                Id = Guid.NewGuid(),
                AlbumShareLinkId = link.Id,
                AlbumShareGuestId = guest.Id,
                Generation = 1,
                CreatedAt = now,
            };
            _db.AlbumShareChallenges.Add(challenge);
        }
        else
        {
            // A RESEND BUMPS THE GENERATION, and the proof covers it — so the
            // code from the previous email stops verifying the moment this one
            // is written. Two live codes would double an attacker's chances for
            // no benefit to anybody.
            challenge.Generation += 1;
            challenge.Attempts = 0;
            challenge.VerifiedAt = null;
        }
        challenge.OtpProof = _tokens.OtpProof(challenge.Id, challenge.Generation, code);
        challenge.ExpiresAt = now + AlbumShareLimits.ChallengeLifetime;
        challenge.LastSentAt = now;
        await _db.SaveChangesAsync(cancellationToken);

        var albumName = await _db.Albums.AsNoTracking()
            .Where(a => a.Id == link.AlbumId).Select(a => a.Name)
            .FirstOrDefaultAsync(cancellationToken) ?? string.Empty;

        // Delivery failing does not change the answer, for the same reason an
        // unlisted address does not: the caller must not be able to tell.
        var delivered = await _email.SendAsync(new EmailMessage(
            guest.Email,
            guest.DisplayName ?? guest.Email,
            $"Il tuo codice per «{albumName}»",
            $"""
             Ecco il codice per aprire l'album «{albumName}»:

                 {code}

             Vale {(int)AlbumShareLimits.ChallengeLifetime.TotalMinutes} minuti ed è valido una volta sola.
             Se non hai chiesto tu questo codice, ignora questo messaggio.
             """), cancellationToken);
        if (!delivered)
        {
            // The line names the link, never the address and never the code.
            _logger.LogWarning(
                "album.share.code.undelivered LinkId={LinkId}", link.Id);
        }
        return AlbumShareChallengeOutcome.Accepted;
    }

    public async Task<AlbumShareVerifyOutcome> VerifyAsync(
        string? token, string? email, string? code, string? userAgent,
        CancellationToken cancellationToken = default)
    {
        var link = await LiveLinkAsync(token, cancellationToken);
        if (link is null) return new AlbumShareVerifyOutcome(false);
        if (code is not { Length: 6 } || !code.All(char.IsAsciiDigit))
            return new AlbumShareVerifyOutcome(false, ErrorCode: AlbumShareErrors.InvalidCode);

        var normalized = AlbumShareTokens.NormalizeEmail(email);
        var guest = await _db.AlbumShareGuests.AsNoTracking().FirstOrDefaultAsync(
            g => g.AlbumShareLinkId == link.Id && g.Email == normalized && g.RevokedAt == null,
            cancellationToken);
        if (guest is null)
            return new AlbumShareVerifyOutcome(false, ErrorCode: AlbumShareErrors.InvalidCode);

        var now = Now;
        var challenge = await _db.AlbumShareChallenges
            .FirstOrDefaultAsync(c => c.AlbumShareGuestId == guest.Id, cancellationToken);
        if (challenge is null || challenge.ExpiresAt <= now)
            return new AlbumShareVerifyOutcome(false, ErrorCode: AlbumShareErrors.InvalidCode);
        if (challenge.Attempts >= AlbumShareLimits.MaxOtpAttempts)
            return new AlbumShareVerifyOutcome(false, ErrorCode: AlbumShareErrors.TooManyAttempts);

        // The attempt is counted BEFORE the comparison, so a crash or a
        // cancelled request between the two cannot buy a free guess.
        challenge.Attempts += 1;
        await _db.SaveChangesAsync(cancellationToken);

        if (!_tokens.OtpMatches(challenge.Id, challenge.Generation, challenge.OtpProof, code))
            return new AlbumShareVerifyOutcome(false, ErrorCode: AlbumShareErrors.InvalidCode);

        // SPENT ON USE. A code that still worked after it had been accepted
        // would be a code sitting in somebody's inbox indefinitely.
        challenge.VerifiedAt = now;
        challenge.ExpiresAt = now;

        var raw = AlbumShareTokens.NewDeviceToken();
        _db.AlbumShareDevices.Add(new AlbumShareDevice
        {
            Id = Guid.NewGuid(),
            AlbumShareLinkId = link.Id,
            AlbumShareGuestId = guest.Id,
            TokenHash = AlbumShareTokens.Hash(raw),
            UserAgent = userAgent is { Length: > 0 }
                ? userAgent[..Math.Min(userAgent.Length, AlbumShareLimits.MaxUserAgentLength)]
                : null,
            CreatedAt = now,
            LastSeenAt = now,
            ExpiresAt = now + AlbumShareLimits.DeviceLifetime,
        });
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("album.share.verified LinkId={LinkId}", link.Id);
        return new AlbumShareVerifyOutcome(true, raw);
    }

    /// <summary>
    /// The link behind a token, when it is live. Deliberately does NOT consult
    /// the second factor: this is the half that establishes it.
    /// </summary>
    private async Task<AlbumShareLink?> LiveLinkAsync(string? token, CancellationToken ct)
    {
        if (!AlbumShareTokens.LooksLikeToken(token)) return null;
        var hash = AlbumShareTokens.Hash(token!);
        var now = Now;
        var link = await _db.AlbumShareLinks
            .FirstOrDefaultAsync(x => x.TokenHash == hash, ct);
        if (link is null || !link.Enabled || link.RevokedAt is not null
            || (link.ExpiresAt is DateTime expiry && expiry <= now)
            || !link.RequireSecondFactor)
        {
            return null;
        }
        var albumOk = await _db.Albums.AsNoTracking().AnyAsync(
            a => a.Id == link.AlbumId && a.OwnerUserId == link.OwnerUserId, ct);
        return albumOk ? link : null;
    }
}
