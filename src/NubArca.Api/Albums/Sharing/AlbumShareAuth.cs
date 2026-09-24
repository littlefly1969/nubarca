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
    ///
    /// <para><b>SECURITY DECISION — the residual timing channel, accepted.</b>
    /// The two paths are identical in everything a caller reads and differ in
    /// how long they take. An unlisted address misses one indexed lookup and
    /// returns; a listed one additionally opens a transaction, takes a row
    /// lock, writes a challenge and commits. Delivery no longer counts — that
    /// was the large, network-scale difference and it is dispatched without
    /// being awaited — but a database-scale one remains, on the order of a
    /// single short write.</para>
    ///
    /// <para>This is accepted rather than overlooked, for three reasons. The
    /// route is rate-limited to ten attempts an hour per SOURCE IP — the policy
    /// partitions on the remote address, not on the email being probed — so a
    /// single source cannot collect the samples it would take to pull a
    /// sub-millisecond difference out of network jitter. That bound is per
    /// source: an attacker spreading requests across many addresses gets
    /// proportionally more samples, and nothing here pretends otherwise. The
    /// channel stays accepted because what it leaks is one bit per address at
    /// a cost that scales with the number of sources an attacker controls.
    /// Equalising it properly
    /// would mean doing the same write for addresses nobody listed — creating
    /// state, and a denial-of-service surface, on behalf of strangers — which
    /// is a worse trade than the channel it closes. And a fixed artificial
    /// delay is not a defence: it is a constant an attacker subtracts, and it
    /// would slow every legitimate visitor to hide something the rate limit
    /// already buries.</para>
    ///
    /// <para>What IS eliminated, and must stay eliminated: the status code and
    /// body are identical for listed, unlisted and malformed addresses,
    /// including under the resend cooldown and after exhausted attempts; and no
    /// answer waits on SMTP. Those are covered by tests. If the threat model
    /// ever changes — a share exposed to an attacker who can measure the server
    /// directly rather than across a network — the honest fix is to make the
    /// listed path's work unconditional, not to pad the unlisted one.</para>
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
    /// <summary>
    /// Accepted for processing. Says NOTHING about whether the address is
    /// listed, whether mail was sent, or whether a cooldown suppressed it.
    ///
    /// <para>There used to be a third value here for the resend cooldown, and
    /// it was an enumeration oracle: an unlisted address answered the same way
    /// twice, while a listed one answered differently the second time within a
    /// minute — so two requests read the owner's guest list. The cooldown still
    /// exists and still suppresses the mail; it is simply INVISIBLE from
    /// outside, which is the only place it was ever doing harm.</para>
    /// </summary>
    Accepted,

    /// <summary>The link itself does not open. The only refusal a caller sees.</summary>
    NotFound,
}

/// <summary>
/// The result of checking a code.
///
/// <para>There is deliberately NO reason on a failure. Exhausted attempts and a
/// wrong code used to be distinguishable, and that was a second enumeration
/// oracle: only a listed address can ever exhaust anything, so
/// <c>too_many_attempts</c> said "this address is on the list" to anybody
/// willing to guess six times. The distinction lives in the log, where it
/// helps an operator and tells a caller nothing.</para>
/// </summary>
public sealed record AlbumShareVerifyOutcome(bool Verified, string? DeviceToken = null);

public sealed class AlbumShareAuth : IAlbumShareAuth
{
    private readonly AppDbContext _db;
    private readonly AlbumShareTokens _tokens;
    private readonly IAlbumShareMailDispatcher _mail;
    private readonly TimeProvider _clock;
    private readonly ILogger<AlbumShareAuth> _logger;

    public AlbumShareAuth(
        AppDbContext db, AlbumShareTokens tokens, IAlbumShareMailDispatcher mail,
        TimeProvider clock, ILogger<AlbumShareAuth> logger)
    {
        _db = db;
        _tokens = tokens;
        _mail = mail;
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

        // ONE RESEND AT A TIME, per address. Reading the challenge, bumping its
        // generation and writing a new proof is read-modify-write: two resends
        // arriving together both read generation 1, both write 2, and the
        // loser's email carries a code that will never verify — a person
        // staring at six digits that are simply wrong. The transaction opens by
        // writing the GUEST's own row, a self-assignment that changes nothing
        // and exists to take that row's write lock, so the second resend reads
        // the first one's result instead of the state before it. It is the same
        // discipline the album mutations use, applied to an address.
        var owned = _db.Database.CurrentTransaction is null;
        var tx = owned ? await _db.Database.BeginTransactionAsync(cancellationToken) : null;
        try
        {
        await _db.AlbumShareGuests.Where(g => g.Id == guest.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(g => g.Email, g => g.Email), cancellationToken);

        var challenge = await _db.AlbumShareChallenges
            .FirstOrDefaultAsync(c => c.AlbumShareGuestId == guest.Id, cancellationToken);

        if (challenge?.LastSentAt is DateTime sent
            && now - sent < AlbumShareLimits.ResendInterval)
        {
            // SUPPRESSED, NOT REFUSED. Nothing is sent and nothing is written,
            // so the previous code stays valid — but the caller is told exactly
            // what an unlisted address is told, because any difference here is
            // a read of the guest list. The operator can still see it.
            _logger.LogInformation("album.share.code.suppressed LinkId={LinkId}", link.Id);
            if (owned) await tx!.CommitAsync(cancellationToken);
            return AlbumShareChallengeOutcome.Accepted;
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
            //
            // The read above happened under the guest's write lock, so this
            // value is the committed one and no concurrent resend can be
            // holding a stale copy of it.
            challenge.Generation += 1;
            challenge.Attempts = 0;
            challenge.VerifiedAt = null;
        }
        challenge.OtpProof = _tokens.OtpProof(challenge.Id, challenge.Generation, code);
        challenge.ExpiresAt = now + AlbumShareLimits.ChallengeLifetime;
        challenge.LastSentAt = now;

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // GET-OR-CREATE, CONCURRENTLY. Two first-time challenges for one
            // address both read null and both insert; the unique index on the
            // guest refuses the second. That refusal used to escape as a 500,
            // and a 500 is an answer an unlisted address never gets — so the
            // pair (202, 500) against (202, 202) read the owner's guest list,
            // which is precisely the oracle this file exists to avoid.
            //
            // The loser simply steps aside: the winner's code is in the post,
            // and sending a second one would invalidate the first.
            _db.ChangeTracker.Clear();
            var winner = await _db.AlbumShareChallenges.AsNoTracking()
                .AnyAsync(c => c.AlbumShareGuestId == guest.Id, cancellationToken);
            if (!winner) throw;
            _logger.LogInformation("album.share.code.raced LinkId={LinkId}", link.Id);
            if (owned) await tx!.RollbackAsync(cancellationToken);
            return AlbumShareChallengeOutcome.Accepted;
        }

        var albumName = await _db.Albums.AsNoTracking()
            .Where(a => a.Id == link.AlbumId).Select(a => a.Name)
            .FirstOrDefaultAsync(cancellationToken) ?? string.Empty;

        // QUEUED, NOT SENT HERE. An unlisted address returns as soon as the
        // lookup misses; a listed one used to wait for the SMTP round trip, and
        // that difference is measurable from outside — a slower answer means
        // "this address is on the list". The dispatcher is owned, bounded and
        // drained by a hosted service, so this is constant-time without being
        // unowned work.
        var message = new EmailMessage(
            guest.Email,
            guest.DisplayName ?? guest.Email,
            $"Il tuo codice per «{albumName}»",
            $"""
             Ecco il codice per aprire l'album «{albumName}»:

                 {code}

             Vale {(int)AlbumShareLimits.ChallengeLifetime.TotalMinutes} minuti ed è valido una volta sola.
             Se non hai chiesto tu questo codice, ignora questo messaggio.
             """);
        // COMMITTED BEFORE THE CODE LEAVES. Queueing a code whose proof might
        // still roll back would be a code nobody can use.
        if (owned) await tx!.CommitAsync(cancellationToken);
        _mail.Enqueue(message, link.Id);

        return AlbumShareChallengeOutcome.Accepted;
        }
        finally
        {
            if (tx is not null) await tx.DisposeAsync();
        }
    }

    public async Task<AlbumShareVerifyOutcome> VerifyAsync(
        string? token, string? email, string? code, string? userAgent,
        CancellationToken cancellationToken = default)
    {
        var link = await LiveLinkAsync(token, cancellationToken);
        if (link is null) return new AlbumShareVerifyOutcome(false);
        if (code is not { Length: 6 } || !code.All(char.IsAsciiDigit))
            return new AlbumShareVerifyOutcome(false);

        var normalized = AlbumShareTokens.NormalizeEmail(email);
        var guest = await _db.AlbumShareGuests.AsNoTracking().FirstOrDefaultAsync(
            g => g.AlbumShareLinkId == link.Id && g.Email == normalized && g.RevokedAt == null,
            cancellationToken);
        // An unlisted address takes the SAME exit as a wrong code. Only a listed
        // one can ever exhaust attempts, so any separate answer here would say
        // "this address is on the list" to whoever asked twice.
        if (guest is null) return new AlbumShareVerifyOutcome(false);

        var now = Now;

        // THE ATTEMPT IS SPENT BY A CONDITIONAL UPDATE, not by read-then-write.
        // Two phones submitting at once used to both read a live challenge and
        // both increment from the same value, so one of the two guesses was
        // free. Asking the database to increment only while the row still looks
        // live makes the count the database's to keep.
        var counted = await _db.AlbumShareChallenges
            .Where(c => c.AlbumShareGuestId == guest.Id
                && c.VerifiedAt == null
                && c.ExpiresAt > now
                && c.Attempts < AlbumShareLimits.MaxOtpAttempts)
            .ExecuteUpdateAsync(
                s => s.SetProperty(c => c.Attempts, c => c.Attempts + 1), cancellationToken);
        if (counted == 0)
        {
            // Expired, already spent, or out of guesses. One answer for all
            // three, and the reason only in the log.
            _logger.LogInformation("album.share.verify.closed LinkId={LinkId}", link.Id);
            return new AlbumShareVerifyOutcome(false);
        }

        var challenge = await _db.AlbumShareChallenges.AsNoTracking()
            .FirstOrDefaultAsync(c => c.AlbumShareGuestId == guest.Id, cancellationToken);
        if (challenge is null) return new AlbumShareVerifyOutcome(false);

        if (!_tokens.OtpMatches(challenge.Id, challenge.Generation, challenge.OtpProof, code))
            return new AlbumShareVerifyOutcome(false);

        // THE CODE IS SPENT BY THE SAME KIND OF STATEMENT, and exactly one
        // caller can win it. `VerifiedAt == null` in the predicate is the
        // compare; setting it is the swap. A correct code submitted twice at
        // the same instant therefore mints ONE device, not two — which is what
        // "one-time" has to mean to be worth saying.
        //
        // THE GENERATION IS IN THE PREDICATE, and it has to be. Without it a
        // verify that had already validated generation 1 could win this claim
        // after a concurrent resend wrote generation 2 — admitting somebody on
        // a code the resend was supposed to have killed, which breaks the one
        // property a resend exists to provide. Matching the proof as well
        // closes the same window from the other side, since a resend rewrites
        // both together.
        var claimed = await _db.AlbumShareChallenges
            .Where(c => c.Id == challenge.Id
                && c.VerifiedAt == null
                && c.ExpiresAt > now
                && c.Generation == challenge.Generation
                && c.OtpProof == challenge.OtpProof)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.VerifiedAt, now)
                .SetProperty(c => c.ExpiresAt, now), cancellationToken);
        if (claimed == 0)
        {
            // Somebody resent while this guess was in flight. The code that was
            // just validated is no longer the live one, and admitting on it
            // would be exactly the bug.
            _logger.LogInformation("album.share.verify.superseded LinkId={LinkId}", link.Id);
            return new AlbumShareVerifyOutcome(false);
        }

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
