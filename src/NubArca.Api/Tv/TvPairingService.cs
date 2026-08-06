using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NubArca.Api.Data;
using NubArca.Api.Domain;

namespace NubArca.Api.Tv;

public sealed class TvPairingService : ITvPairingService
{
    // The limited TV session cookie. Its name is the wire contract with every
    // paired television, so the 0.3.0 identity cutover deliberately un-paired the
    // fleet once rather than carrying the former name forward: a one-time re-pair
    // was accepted in exchange for a single coherent identity. Renaming it again
    // would cost another re-pair.
    public const string CookieName = "NubArca.TvSession";
    public const string PairingSecretHeader = "X-Tv-Pairing-Secret";

    private const string CodeAlphabet = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";
    private readonly AppDbContext _db;
    private readonly TimeProvider _clock;
    private readonly TvSessionOptions _options;
    private readonly IPasswordHasher<TvPersonalPin> _pinHasher;

    public TvPairingService(
        AppDbContext db, TimeProvider clock, IOptions<TvSessionOptions> options,
        IPasswordHasher<TvPersonalPin> pinHasher)
    {
        _db = db;
        _clock = clock;
        _options = options.Value;
        _pinHasher = pinHasher;
    }

    public async Task<TvPairingStartedDto> StartAsync(
        string approvalBaseUrl, CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var rawSecret = NewToken();
        string code;
        do
        {
            code = NewPublicCode();
        }
        while (await _db.TvPairingRequests.AnyAsync(x => x.PublicCode == code, cancellationToken));

        var pairing = new TvPairingRequest
        {
            Id = Guid.NewGuid(),
            PublicCode = code,
            SecretHash = HashToken(rawSecret),
            Status = TvPairingStatuses.Pending,
            CreatedAt = now,
            ExpiresAt = now.AddMinutes(Math.Clamp(_options.PairingLifetimeMinutes, 1, 30)),
        };
        _db.TvPairingRequests.Add(pairing);
        await _db.SaveChangesAsync(cancellationToken);

        // Keep the secret in the URL fragment. Browsers do not send fragments
        // to the web server, reverse proxy, access logs, or Referer headers.
        var url = $"{approvalBaseUrl.TrimEnd('/')}/tv/pair?code={Uri.EscapeDataString(code)}#secret={Uri.EscapeDataString(rawSecret)}";
        return new TvPairingStartedDto(code, rawSecret, url, pairing.ExpiresAt);
    }

    public async Task<TvPairingPollResult?> PollAsync(
        string publicCode, string? pairingSecret, string? userAgent,
        CancellationToken cancellationToken = default)
    {
        var pairing = await FindPairingAsync(publicCode, pairingSecret, cancellationToken);
        if (pairing is null)
        {
            return null;
        }

        var now = _clock.GetUtcNow().UtcDateTime;
        if (pairing.ExpiresAt <= now && pairing.Status != TvPairingStatuses.Paired)
        {
            pairing.Status = TvPairingStatuses.Expired;
            await _db.SaveChangesAsync(cancellationToken);
            return new TvPairingPollResult(new(TvPairingStatuses.Expired, pairing.ExpiresAt));
        }

        if (pairing.Status == TvPairingStatuses.Approved && pairing.ApprovedByUserId is Guid ownerUserId)
        {
            await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
            var claimed = await _db.TvPairingRequests
                .Where(x => x.Id == pairing.Id && x.Status == TvPairingStatuses.Approved)
                .ExecuteUpdateAsync(
                    updates => updates.SetProperty(x => x.Status, TvPairingStatuses.Claiming),
                    cancellationToken);
            if (claimed == 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                await _db.Entry(pairing).ReloadAsync(cancellationToken);
                return new TvPairingPollResult(new(pairing.Status, pairing.ExpiresAt));
            }

            // Invariant guard: a completed pairing implies the owner has a
            // Personal Area PIN (ApproveAsync commits both atomically). If the
            // PIN row vanished between approval and claim (manual intervention,
            // corrupted data), refuse to mint a session — expire the pairing so
            // the TV starts over instead of producing a PIN-less association.
            var ownerHasPin = await _db.TvPersonalPins
                .AnyAsync(p => p.OwnerUserId == ownerUserId, cancellationToken);
            if (!ownerHasPin)
            {
                pairing.Status = TvPairingStatuses.Expired;
                await _db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return new TvPairingPollResult(new(TvPairingStatuses.Expired, pairing.ExpiresAt));
            }

            var rawSessionToken = NewToken();
            var session = new TvSession
            {
                Id = Guid.NewGuid(),
                OwnerUserId = ownerUserId,
                SessionTokenHash = HashToken(rawSessionToken),
                CreatedAt = now,
                LastSeenAt = now,
                ExpiresAt = now.AddDays(Math.Clamp(_options.SessionLifetimeDays, 1, 365)),
                UserAgent = Truncate(userAgent, 500),
            };
            _db.TvSessions.Add(session);
            pairing.TvSessionId = session.Id;
            pairing.Status = TvPairingStatuses.Paired;
            await _db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new TvPairingPollResult(
                new(TvPairingStatuses.Paired, pairing.ExpiresAt), rawSessionToken, session.ExpiresAt);
        }

        return new TvPairingPollResult(new(pairing.Status, pairing.ExpiresAt));
    }

    public async Task<TvPairingApproveResult> ApproveAsync(
        string publicCode, string? pairingSecret, Guid ownerUserId,
        string? personalPin, string? personalPinConfirmation,
        CancellationToken cancellationToken = default)
    {
        var pairing = await FindPairingAsync(publicCode, pairingSecret, cancellationToken);
        if (pairing is null)
        {
            return new TvPairingApproveResult(TvPairingApproveStatus.NotFound);
        }

        var now = _clock.GetUtcNow().UtcDateTime;
        if (pairing.ExpiresAt <= now || pairing.Status != TvPairingStatuses.Pending)
        {
            if (pairing.ExpiresAt <= now && pairing.Status == TvPairingStatuses.Pending)
            {
                pairing.Status = TvPairingStatuses.Expired;
                await _db.SaveChangesAsync(cancellationToken);
            }
            return new TvPairingApproveResult(TvPairingApproveStatus.NotFound);
        }

        // Atomic first pairing: an owner without a Personal Area PIN must create
        // one HERE — the PIN row and the approval are committed by the SAME
        // SaveChanges (one database transaction), so an abandoned or failed PIN
        // step leaves the pairing pending and nothing partially associated. An
        // owner who already has a PIN approves normally; any supplied PIN fields
        // are deliberately ignored (an existing PIN is never replaced here).
        var pinCreated = false;
        var hasPin = await _db.TvPersonalPins
            .AnyAsync(p => p.OwnerUserId == ownerUserId, cancellationToken);
        if (!hasPin)
        {
            if (string.IsNullOrEmpty(personalPin))
            {
                return new TvPairingApproveResult(TvPairingApproveStatus.PinRequired);
            }
            if (!TvPersonalAreaService.IsValidPinFormat(personalPin))
            {
                return new TvPairingApproveResult(TvPairingApproveStatus.InvalidPin);
            }
            if (!string.Equals(personalPin, personalPinConfirmation, StringComparison.Ordinal))
            {
                return new TvPairingApproveResult(TvPairingApproveStatus.PinMismatch);
            }

            var pinRow = new TvPersonalPin
            {
                Id = Guid.NewGuid(),
                OwnerUserId = ownerUserId,
                Generation = 1,
                CreatedAt = now,
            };
            pinRow.PinHash = _pinHasher.HashPassword(pinRow, personalPin);
            _db.TvPersonalPins.Add(pinRow);
            pinCreated = true;
        }

        pairing.Status = TvPairingStatuses.Approved;
        pairing.ApprovedAt = now;
        pairing.ApprovedByUserId = ownerUserId;
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException) when (pinCreated)
        {
            // Lost a race with a concurrent PIN creation for the same owner
            // (unique OwnerUserId). The whole commit — including the approval —
            // rolled back. The PIN now exists, so approve on its own.
            _db.ChangeTracker.Clear();
            var retry = await _db.TvPairingRequests
                .FirstAsync(x => x.Id == pairing.Id, cancellationToken);
            if (retry.Status != TvPairingStatuses.Pending || retry.ExpiresAt <= now)
            {
                return new TvPairingApproveResult(TvPairingApproveStatus.NotFound);
            }
            retry.Status = TvPairingStatuses.Approved;
            retry.ApprovedAt = now;
            retry.ApprovedByUserId = ownerUserId;
            await _db.SaveChangesAsync(cancellationToken);
            pairing = retry;
            pinCreated = false;
        }

        return new TvPairingApproveResult(
            TvPairingApproveStatus.Approved,
            new TvPairingStatusDto(TvPairingStatuses.Approved, pairing.ExpiresAt),
            pairing.Id,
            pinCreated);
    }

    public async Task<TvSessionDto?> GetSessionAsync(
        string? sessionToken, bool heartbeat, CancellationToken cancellationToken = default)
    {
        var hash = HashTokenOrNull(sessionToken);
        if (hash is null)
        {
            return null;
        }

        var session = await _db.TvSessions.FirstOrDefaultAsync(
            x => x.SessionTokenHash == hash, cancellationToken);
        var now = _clock.GetUtcNow().UtcDateTime;
        if (session is null || session.RevokedAt is not null || session.ExpiresAt <= now)
        {
            return null;
        }

        if (heartbeat)
        {
            session.LastSeenAt = now;
            await _db.SaveChangesAsync(cancellationToken);
        }

        // Surface the paired owner's UI language so the TV app localizes in the
        // owner's language. Bare code only — never owner identity. Falls back to
        // the Italian default if the row is missing.
        var ownerLanguage = await _db.Users
            .AsNoTracking()
            .Where(u => u.Id == session.OwnerUserId)
            .Select(u => u.UiLanguage)
            .FirstOrDefaultAsync(cancellationToken) ?? UiLanguages.Default;

        return new TvSessionDto("active", session.ExpiresAt, session.LastSeenAt, ownerLanguage);
    }

    public async Task<bool> RevokeSessionAsync(
        string? sessionToken, CancellationToken cancellationToken = default)
    {
        var hash = HashTokenOrNull(sessionToken);
        if (hash is null)
        {
            return false;
        }

        var session = await _db.TvSessions.FirstOrDefaultAsync(
            x => x.SessionTokenHash == hash, cancellationToken);
        if (session is null || session.RevokedAt is not null)
        {
            return false;
        }

        session.RevokedAt = _clock.GetUtcNow().UtcDateTime;
        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<Guid?> ResolveOwnerUserIdAsync(
        string? sessionToken, CancellationToken cancellationToken = default)
    {
        var hash = HashTokenOrNull(sessionToken);
        if (hash is null)
        {
            return null;
        }

        var now = _clock.GetUtcNow().UtcDateTime;
        var session = await _db.TvSessions
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.SessionTokenHash == hash, cancellationToken);
        if (session is null || session.RevokedAt is not null || session.ExpiresAt <= now)
        {
            return null;
        }

        return session.OwnerUserId;
    }

    public async Task<IReadOnlyList<TvDeviceDto>> ListOwnerSessionsAsync(
        Guid ownerUserId, CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var sessions = await _db.TvSessions
            .AsNoTracking()
            .Where(x => x.OwnerUserId == ownerUserId)
            .OrderByDescending(x => x.CreatedAt)
            .ToListAsync(cancellationToken);

        return sessions
            .Select(s => new TvDeviceDto(
                s.Id,
                s.DeviceLabel,
                s.UserAgent,
                DeviceStatus(s, now),
                s.CreatedAt,
                s.LastSeenAt,
                s.ExpiresAt,
                s.RevokedAt))
            .ToList();
    }

    public async Task<bool> RevokeOwnerSessionAsync(
        Guid ownerUserId, Guid sessionId, CancellationToken cancellationToken = default)
    {
        var session = await _db.TvSessions
            .FirstOrDefaultAsync(
                x => x.Id == sessionId && x.OwnerUserId == ownerUserId, cancellationToken);
        if (session is null)
        {
            return false;
        }

        // Idempotent: revoking an already-revoked session is a no-op success.
        if (session.RevokedAt is null)
        {
            session.RevokedAt = _clock.GetUtcNow().UtcDateTime;
            await _db.SaveChangesAsync(cancellationToken);
        }

        return true;
    }

    private static string DeviceStatus(TvSession session, DateTime now)
    {
        if (session.RevokedAt is not null) return "revoked";
        if (session.ExpiresAt <= now) return "expired";
        return "active";
    }

    private async Task<TvPairingRequest?> FindPairingAsync(
        string publicCode, string? pairingSecret, CancellationToken cancellationToken)
    {
        var normalizedCode = publicCode.Trim().ToUpperInvariant();
        var pairing = await _db.TvPairingRequests.FirstOrDefaultAsync(
            x => x.PublicCode == normalizedCode, cancellationToken);
        if (pairing is null || string.IsNullOrWhiteSpace(pairingSecret))
        {
            return null;
        }

        var suppliedHash = HashToken(pairingSecret);
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(pairing.SecretHash), Encoding.ASCII.GetBytes(suppliedHash))
            ? pairing
            : null;
    }

    internal static string HashToken(string token)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    private static string? HashTokenOrNull(string? token)
        => string.IsNullOrWhiteSpace(token) ? null : HashToken(token);

    private static string NewToken()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string NewPublicCode()
    {
        Span<byte> bytes = stackalloc byte[8];
        RandomNumberGenerator.Fill(bytes);
        Span<char> chars = stackalloc char[8];
        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = CodeAlphabet[bytes[i] % CodeAlphabet.Length];
        }
        return new string(chars);
    }

    private static string? Truncate(string? value, int maxLength)
        => string.IsNullOrWhiteSpace(value) ? null : value[..Math.Min(value.Length, maxLength)];
}
