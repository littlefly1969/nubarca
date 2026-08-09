using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using NubArca.Api.Data;
using NubArca.Api.Domain;

namespace NubArca.Api.Tv;

// TV Personal Area: per-user access secret + per-TV-session unlock grants.
//
// Security model (mirrors PrivateVaultService):
//   * The secret is created ONLY from the authenticated web account UI; it is
//     hashed with the ASP.NET Core PasswordHasher (PBKDF2) and the plaintext
//     never leaves the verify path — not stored, logged, or echoed.
//   * The paired TV session authenticates the DEVICE; the secret is a second,
//     local authorization step. Personal access always requires BOTH.
//   * An unlock mints an opaque 256-bit grant; only its SHA-256 hex is stored,
//     bound to (session, owner, secret generation) with a bounded lifetime. The
//     primary lifecycle is the explicit lock on leaving the Personal Area.
//   * Wrong secret, malformed secret, and "none configured" all collapse into
//     ONE generic Invalid outcome — a TV session must not learn credential or
//     account state from the error shape.
//   * Brute-force: per-session persisted counter with a progressive, bounded
//     cooldown (see below) on top of the per-IP fixed-window rate limit policy
//     applied at the endpoint.
//
// TWO SCHEMES, ONE CREDENTIAL ROW (see TvPersonalSecretSchemes). The current
// scheme is the 9-symbol DIRECTIONAL code, entered blind on the remote: the TV
// renders no symbol, no digit and no per-press highlight, so a person watching
// the television learns nothing from the screen. The retired 6-digit numeric
// PIN stays VERIFIABLE — an already-paired television must not stop working the
// moment this ships — but nothing creates one any more, and no current client
// offers numeric entry. The row's scheme decides which format is accepted; the
// hash comparison itself is identical for both.
public sealed class TvPersonalAreaService : ITvPersonalAreaService
{
    public const string UnlockHeader = "X-Tv-Personal-Unlock";

    // Retired numeric PIN (verify-only).
    public const int PinLength = 6;

    // Directional code. Five symbols, nine presses:
    //     5^9 = 1,953,125
    // which is ~2x the 10^6 space of the numeric PIN it replaces, so the move to
    // an alphabet the remote can enter blind does NOT weaken the secret. The
    // alphabet is deliberately only the five buttons whose position a user finds
    // without looking: the four directions and the centre. MENU/BACK/HOME, the
    // microphone and the media transport keys are excluded — they carry system
    // or navigation meaning and cannot be spent as secret symbols.
    public const int DpadCodeLength = 9;
    public const string DpadAlphabet = "UDLRS";

    // Grant lifetime: the secondary server-side cap. Long enough for an evening
    // of TV use; the explicit lock-on-exit is the primary lifecycle.
    internal static readonly TimeSpan GrantLifetime = TimeSpan.FromHours(4);

    // Progressive cooldown: the first FreeAttempts consecutive failures only
    // count; every further failure locks the session for
    // BaseCooldown * 2^(failures - FreeAttempts), capped at MaxCooldown.
    // (5 wrong tries → 30s, then 60s, 120s … up to 15 min. Never permanent.)
    internal const int FreeAttempts = 5;
    internal static readonly TimeSpan BaseCooldown = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan MaxCooldown = TimeSpan.FromMinutes(15);

    private readonly AppDbContext _db;
    private readonly TimeProvider _clock;
    private readonly IPasswordHasher<TvPersonalPin> _hasher;

    public TvPersonalAreaService(
        AppDbContext db, TimeProvider clock, IPasswordHasher<TvPersonalPin> hasher)
    {
        _db = db;
        _clock = clock;
        _hasher = hasher;
    }

    // ── owner-side PIN lifecycle ────────────────────────────────────────────

    public async Task<TvPersonalPinStatusDto> GetPinStatusAsync(
        Guid ownerUserId, CancellationToken cancellationToken = default)
    {
        var row = await _db.TvPersonalPins
            .AsNoTracking()
            .Where(p => p.OwnerUserId == ownerUserId)
            .Select(p => new { p.CreatedAt, p.UpdatedAt, p.Scheme })
            .FirstOrDefaultAsync(cancellationToken);
        return row is null
            ? new TvPersonalPinStatusDto(false)
            : new TvPersonalPinStatusDto(true, row.UpdatedAt ?? row.CreatedAt, row.Scheme);
    }

    // Owner-authenticated set/change/reset of the DIRECTIONAL code. This is the
    // only creation path: a legacy numeric row is REPLACED by it (same atomic
    // transaction, same generation bump, same grant revocation), which is how an
    // installation crosses over without ever holding two live schemes.
    public async Task<TvPersonalPinSetResult> SetDpadCodeAsync(
        Guid ownerUserId, string? code, string? confirmCode,
        CancellationToken cancellationToken = default)
    {
        if (!IsValidDpadCodeFormat(code))
        {
            return new TvPersonalPinSetResult(TvPersonalPinSetOutcome.InvalidPin);
        }
        // Compare the NORMALIZED forms. Two spellings that hash to the same
        // secret are the same secret, so refusing them as a "mismatch" would be
        // a false negative; comparing the raw inputs would produce exactly that.
        if (!string.Equals(NormalizeDpadCode(code), NormalizeDpadCode(confirmCode), StringComparison.Ordinal))
        {
            return new TvPersonalPinSetResult(TvPersonalPinSetOutcome.PinMismatch);
        }

        var normalized = NormalizeDpadCode(code)!;
        try
        {
            return await SetValidatedSecretAsync(
                ownerUserId, normalized, TvPersonalSecretSchemes.Dpad, cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Lost a create race (unique OwnerUserId; whole transaction rolled
            // back). The row now exists — retry once as a change.
            _db.ChangeTracker.Clear();
            return await SetValidatedSecretAsync(
                ownerUserId, normalized, TvPersonalSecretSchemes.Dpad, cancellationToken);
        }
    }

    private async Task<TvPersonalPinSetResult> SetValidatedSecretAsync(
        Guid ownerUserId, string secret, string scheme, CancellationToken cancellationToken)
    {
        // Create-or-change in ONE transaction: hash update, generation bump,
        // revocation of every outstanding grant of this owner, and reset of the
        // owner's TV-session cooldown state commit together (or not at all).
        // A stale grant then fails IMMEDIATELY on its next use (the generation
        // check), not merely at expiry.
        var now = _clock.GetUtcNow().UtcDateTime;
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);

        var row = await _db.TvPersonalPins
            .FirstOrDefaultAsync(p => p.OwnerUserId == ownerUserId, cancellationToken);
        var outcome = row is null ? TvPersonalPinSetOutcome.Created : TvPersonalPinSetOutcome.Changed;
        if (row is null)
        {
            row = new TvPersonalPin
            {
                Id = Guid.NewGuid(),
                OwnerUserId = ownerUserId,
                Scheme = scheme,
                Generation = 1,
                CreatedAt = now,
            };
            row.PinHash = _hasher.HashPassword(row, secret);
            _db.TvPersonalPins.Add(row);
        }
        else
        {
            row.PinHash = _hasher.HashPassword(row, secret);
            // Replacing a legacy numeric PIN with a directional code goes through
            // exactly this path, so the crossover inherits the generation bump and
            // the grant revocation below instead of needing its own migration.
            row.Scheme = scheme;
            row.Generation += 1;
            row.UpdatedAt = now;
        }

        var grantsRevoked = await _db.TvPersonalUnlockGrants
            .Where(g => g.OwnerUserId == ownerUserId && g.RevokedAt == null)
            .ExecuteUpdateAsync(
                s => s.SetProperty(g => g.RevokedAt, _ => (DateTime?)now), cancellationToken);

        // A PIN change is also the owner's recovery path from a lockout: clear
        // the failure/cooldown state on every one of their TV sessions.
        await _db.TvSessions
            .Where(s => s.OwnerUserId == ownerUserId
                && (s.PersonalPinFailedAttempts != 0 || s.PersonalPinLockedUntil != null))
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(x => x.PersonalPinFailedAttempts, _ => 0)
                    .SetProperty(x => x.PersonalPinLockedUntil, _ => (DateTime?)null),
                cancellationToken);

        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new TvPersonalPinSetResult(
            outcome, row.UpdatedAt ?? row.CreatedAt, grantsRevoked, row.Scheme);
    }

    // ── TV-side unlock / lock / status ──────────────────────────────────────

    public async Task<TvPersonalUnlockOutcome> UnlockAsync(
        string? sessionToken, string? secret, CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var session = await ResolveLiveSessionAsync(sessionToken, now, cancellationToken);
        if (session is null)
        {
            return new TvPersonalUnlockOutcome(TvPersonalUnlockStatus.SessionInvalid);
        }

        if (session.PersonalPinLockedUntil is DateTime lockedUntil && lockedUntil > now)
        {
            var retryAfter = (int)Math.Ceiling((lockedUntil - now).TotalSeconds);
            return new TvPersonalUnlockOutcome(
                TvPersonalUnlockStatus.Throttled, RetryAfterSeconds: Math.Max(1, retryAfter),
                TvSessionId: session.Id, OwnerUserId: session.OwnerUserId);
        }

        // Load the row FIRST: which format is acceptable is a property of the
        // stored scheme, not of what the client happened to send. A presented
        // secret that does not fit the row's scheme fails exactly like a wrong
        // one — same bucket, same cooldown, no probe of which scheme is in use.
        var pinRow = await _db.TvPersonalPins.FirstOrDefaultAsync(
            p => p.OwnerUserId == session.OwnerUserId, cancellationToken);
        var presented = pinRow is null ? null : NormalizeForScheme(secret, pinRow.Scheme);
        var verification = presented is null || pinRow is null
            ? PasswordVerificationResult.Failed
            : _hasher.VerifyHashedPassword(pinRow, pinRow.PinHash, presented);
        if (verification == PasswordVerificationResult.Failed)
        {
            // One generic failure for a wrong secret / a malformed secret / a
            // secret in the wrong scheme / no credential row at all.
            // Every failure counts toward the progressive cooldown.
            session.PersonalPinFailedAttempts += 1;
            if (session.PersonalPinFailedAttempts >= FreeAttempts)
            {
                var exponent = Math.Min(30, session.PersonalPinFailedAttempts - FreeAttempts);
                var cooldownSeconds = Math.Min(
                    MaxCooldown.TotalSeconds,
                    BaseCooldown.TotalSeconds * Math.Pow(2, exponent));
                session.PersonalPinLockedUntil = now.AddSeconds(cooldownSeconds);
            }
            await _db.SaveChangesAsync(cancellationToken);
            return new TvPersonalUnlockOutcome(
                TvPersonalUnlockStatus.Invalid,
                TvSessionId: session.Id, OwnerUserId: session.OwnerUserId);
        }

        // Success: reset the throttle, rotate this session's grants (an unlock
        // supersedes any previous grant of the same session), mint a new one.
        session.PersonalPinFailedAttempts = 0;
        session.PersonalPinLockedUntil = null;
        if (verification == PasswordVerificationResult.SuccessRehashNeeded)
        {
            pinRow!.PinHash = _hasher.HashPassword(pinRow, presented!);
            pinRow.UpdatedAt = now;
        }

        // Opportunistically drop this session's dead grants so the table stays
        // lean, and revoke any still-live ones (single active grant per session).
        await _db.TvPersonalUnlockGrants
            .Where(g => g.TvSessionId == session.Id
                && (g.RevokedAt != null || g.ExpiresAt <= now))
            .ExecuteDeleteAsync(cancellationToken);
        await _db.TvPersonalUnlockGrants
            .Where(g => g.TvSessionId == session.Id && g.RevokedAt == null)
            .ExecuteUpdateAsync(
                s => s.SetProperty(g => g.RevokedAt, _ => (DateTime?)now), cancellationToken);

        var raw = NewGrantToken();
        var expiresAt = now.Add(GrantLifetime);
        _db.TvPersonalUnlockGrants.Add(new TvPersonalUnlockGrant
        {
            Id = Guid.NewGuid(),
            TvSessionId = session.Id,
            OwnerUserId = session.OwnerUserId,
            TokenHash = HashToken(raw),
            PinGeneration = pinRow!.Generation,
            CreatedAt = now,
            ExpiresAt = expiresAt,
        });
        await _db.SaveChangesAsync(cancellationToken);

        return new TvPersonalUnlockOutcome(
            TvPersonalUnlockStatus.Unlocked,
            new TvPersonalUnlockDto(raw, expiresAt),
            TvSessionId: session.Id, OwnerUserId: session.OwnerUserId);
    }

    public async Task<Guid?> LockAsync(
        string? sessionToken, CancellationToken cancellationToken = default)
    {
        var hash = HashTokenOrNull(sessionToken);
        if (hash is null)
        {
            return null;
        }

        // Deliberately matched on the ROW (not liveness): locking a just-revoked
        // or just-expired session still clears its grants — idempotent and safe.
        var session = await _db.TvSessions
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.SessionTokenHash == hash, cancellationToken);
        if (session is null)
        {
            return null;
        }

        var now = _clock.GetUtcNow().UtcDateTime;
        await _db.TvPersonalUnlockGrants
            .Where(g => g.TvSessionId == session.Id && g.RevokedAt == null)
            .ExecuteUpdateAsync(
                s => s.SetProperty(g => g.RevokedAt, _ => (DateTime?)now), cancellationToken);
        return session.Id;
    }

    public async Task<TvPersonalStatusDto?> GetStatusAsync(
        string? sessionToken, string? grantToken, CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var session = await ResolveLiveSessionAsync(sessionToken, now, cancellationToken);
        if (session is null)
        {
            return null;
        }

        // pinConfigured / scheme are DESIGNED owner-private signals to the
        // owner's own paired TV. They drive two different messages and must not
        // be conflated: NO row means the pairing is incomplete (the TV tears
        // down), while a LEGACY row means the pairing is fine and only the
        // credential needs upgrading ("configure the new TV code from your
        // account"). Neither is reachable without a live paired session, and
        // neither reveals hash, generation or attempt state.
        var row = await _db.TvPersonalPins
            .AsNoTracking()
            .Where(p => p.OwnerUserId == session.OwnerUserId)
            .Select(p => new { p.Scheme })
            .FirstOrDefaultAsync(cancellationToken);
        var pinConfigured = row is not null;
        var unlocked = pinConfigured
            && await CheckGrantAsync(session, grantToken, now, cancellationToken)
                == TvPersonalAccessStatus.Ok;
        return new TvPersonalStatusDto(pinConfigured, unlocked, row?.Scheme);
    }

    public async Task<TvPersonalAccessResult> ResolveAccessAsync(
        string? sessionToken, string? grantToken, CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var session = await ResolveLiveSessionAsync(sessionToken, now, cancellationToken);
        if (session is null)
        {
            return new TvPersonalAccessResult(TvPersonalAccessStatus.SessionInvalid);
        }

        var check = await CheckGrantAsync(session, grantToken, now, cancellationToken);
        return new TvPersonalAccessResult(check, session.Id, session.OwnerUserId);
    }

    public async Task<TvPersonalHomeDto?> GetHomeAsync(
        Guid ownerUserId, CancellationToken cancellationToken = default)
    {
        // Display name only — deliberately no email/id/roles/profile fields.
        var displayName = await _db.Users
            .AsNoTracking()
            .Where(u => u.Id == ownerUserId)
            .Select(u => u.DisplayName)
            .FirstOrDefaultAsync(cancellationToken);
        return displayName is null
            ? null
            : new TvPersonalHomeDto(displayName, GalleryAvailable: true);
    }

    // ── internals ───────────────────────────────────────────────────────────

    internal static bool IsValidPinFormat(string? pin)
        => pin is { Length: PinLength } && pin.All(char.IsAsciiDigit);

    // A directional code is exactly DpadCodeLength symbols drawn from
    // DpadAlphabet. Case is normalized (a client may send lowercase) but nothing
    // else is: no whitespace stripping, no separators, no aliases. A permissive
    // parser here would silently let two different keystroke sequences hash to
    // the same secret.
    internal static bool IsValidDpadCodeFormat(string? code)
        => NormalizeDpadCode(code) is not null;

    internal static string? NormalizeDpadCode(string? code)
    {
        if (code is not { Length: DpadCodeLength }) return null;
        Span<char> buffer = stackalloc char[DpadCodeLength];
        for (var i = 0; i < DpadCodeLength; i++)
        {
            var symbol = char.ToUpperInvariant(code[i]);
            if (!DpadAlphabet.Contains(symbol, StringComparison.Ordinal)) return null;
            buffer[i] = symbol;
        }
        return new string(buffer);
    }

    // The canonical secret string for a stored scheme, or null when the
    // presented value does not belong to it. Verification hashes THIS, so the
    // stored hash of a directional code is over its normalized upper-case form.
    private static string? NormalizeForScheme(string? presented, string scheme) => scheme switch
    {
        TvPersonalSecretSchemes.Dpad => NormalizeDpadCode(presented),
        TvPersonalSecretSchemes.LegacyPin => IsValidPinFormat(presented) ? presented : null,
        // An unknown scheme is a corrupt row, not a permissive one: refuse.
        _ => null,
    };

    private async Task<TvSession?> ResolveLiveSessionAsync(
        string? sessionToken, DateTime now, CancellationToken cancellationToken)
    {
        var hash = HashTokenOrNull(sessionToken);
        if (hash is null)
        {
            return null;
        }

        var session = await _db.TvSessions
            .FirstOrDefaultAsync(x => x.SessionTokenHash == hash, cancellationToken);
        return session is null || session.RevokedAt is not null || session.ExpiresAt <= now
            ? null
            : session;
    }

    // The single personal-access gate: the grant must exist, be presented by the
    // SAME session it was minted for, belong to the same owner, be unexpired,
    // match the owner's CURRENT PIN generation, and be unrevoked. A generation
    // mismatch is reported as its own status (checked before the revocation
    // flag — a PIN change both revokes and bumps) so clients can show the
    // "PIN was changed" notice; every other failure is one generic bucket.
    private async Task<TvPersonalAccessStatus> CheckGrantAsync(
        TvSession session, string? grantToken, DateTime now,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(grantToken))
        {
            return TvPersonalAccessStatus.GrantInvalid;
        }

        var hash = HashToken(grantToken);
        var grant = await _db.TvPersonalUnlockGrants
            .AsNoTracking()
            .FirstOrDefaultAsync(g => g.TokenHash == hash, cancellationToken);
        if (grant is null
            || grant.TvSessionId != session.Id
            || grant.OwnerUserId != session.OwnerUserId
            || grant.ExpiresAt <= now)
        {
            return TvPersonalAccessStatus.GrantInvalid;
        }

        var currentGeneration = await _db.TvPersonalPins
            .AsNoTracking()
            .Where(p => p.OwnerUserId == session.OwnerUserId)
            .Select(p => (int?)p.Generation)
            .FirstOrDefaultAsync(cancellationToken);
        if (currentGeneration != grant.PinGeneration)
        {
            return TvPersonalAccessStatus.GrantStalePinChanged;
        }

        return grant.RevokedAt is null
            ? TvPersonalAccessStatus.Ok
            : TvPersonalAccessStatus.GrantInvalid;
    }

    private static string NewGrantToken()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    internal static string HashToken(string token)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static string? HashTokenOrNull(string? token)
        => string.IsNullOrWhiteSpace(token) ? null : HashToken(token);
}
