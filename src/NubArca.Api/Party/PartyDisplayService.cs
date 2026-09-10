using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using NubArca.Api.Data;
using NubArca.Api.Domain;

namespace NubArca.Api.Party;

/// <summary>
/// Mints and resolves a television's permission to show one party.
///
/// <para>Two rules hold everywhere in this file.</para>
///
/// <para>THE CLIENT NEVER NAMES A PARTY. Mint takes a session token and
/// nothing else; the party comes from the television's own assignment, read
/// server-side. There is no parameter for a caller to change, which is what
/// makes cross-party and cross-account impossible rather than merely
/// checked.</para>
///
/// <para>EXPIRY IS NOT THE REVOCATION BOUNDARY. <see cref="GrantLifetime"/>
/// bounds the damage a leaked token could do; what decides validity is the
/// whole chain re-read on every single request — session live, assignment
/// still `party`, assignment still THIS link, party still resolvable. So
/// un-pairing a television or pointing it elsewhere invalidates its grant in
/// the same instant, and the four hours never come into it.</para>
/// </summary>
public sealed class PartyDisplayService : IPartyDisplayService
{
    /// The header a display presents. A header and not a query parameter: a
    /// URL is written to access logs, browser history and referrers, and a
    /// credential must not be.
    public const string GrantHeader = "X-Party-Display-Grant";

    /// <summary>
    /// Four hours — the same figure the Personal Area unlock grant uses, and
    /// for the same reason: long enough that nobody is re-authenticating in the
    /// middle of an evening, short enough that a token which escapes does not
    /// outlive the party.
    ///
    /// <para>It is a BOUND, not the security model. See the class remarks.</para>
    /// </summary>
    internal static readonly TimeSpan GrantLifetime = TimeSpan.FromHours(4);

    private readonly AppDbContext _db;
    private readonly TimeProvider _clock;
    private readonly IPartyLinkService _links;
    private readonly ILogger<PartyDisplayService> _logger;

    public PartyDisplayService(
        AppDbContext db, TimeProvider clock, IPartyLinkService links,
        ILogger<PartyDisplayService> logger)
    {
        _db = db;
        _clock = clock;
        _links = links;
        _logger = logger;
    }

    public async Task<PartyDisplayGrantResult> MintAsync(
        string? sessionToken, CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var session = await LiveSessionAsync(sessionToken, now, cancellationToken);
        if (session is null) return PartyDisplayGrantResult.Fail(PartyDisplayGrantError.NoSession);

        // The assignment is the ONLY way a party enters this method.
        if (session.DisplayAssignment != TvDisplayAssignments.Party
            || session.AssignedPartyAlbumLinkId is not Guid linkId)
            return PartyDisplayGrantResult.Fail(PartyDisplayGrantError.NotAssigned);

        // And the party must still be showable — a revoked or ended one is not
        // something to mint a fresh credential for.
        if (await _links.ResolveDisplayAsync(linkId, cancellationToken) is null)
            return PartyDisplayGrantResult.Fail(PartyDisplayGrantError.NotAssigned);

        // One live grant per television. A remount must not leave the previous
        // credential usable behind it.
        await _db.PartyDisplayGrants
            .Where(g => g.TvSessionId == session.Id && g.RevokedAt == null)
            .ExecuteUpdateAsync(
                s => s.SetProperty(g => g.RevokedAt, _ => (DateTime?)now), cancellationToken);

        var raw = NewToken();
        var expiresAt = now.Add(GrantLifetime);
        _db.PartyDisplayGrants.Add(new PartyDisplayGrant
        {
            Id = Guid.NewGuid(),
            TvSessionId = session.Id,
            PartyAlbumLinkId = linkId,
            TokenHash = HashToken(raw),
            CreatedAt = now,
            ExpiresAt = expiresAt,
        });
        await _db.SaveChangesAsync(cancellationToken);

        // The line names the device and the link, never the token or its hash.
        _logger.LogInformation(
            "party.display.grant.mint TvSessionId={TvSessionId} LinkId={LinkId}",
            session.Id, linkId);
        return PartyDisplayGrantResult.Ok(raw, expiresAt);
    }

    public async Task<PartyAccess?> ResolveAsync(
        string? grantToken, CancellationToken cancellationToken = default)
    {
        var hash = HashTokenOrNull(grantToken);
        if (hash is null) return null;

        var now = _clock.GetUtcNow().UtcDateTime;
        // The grant, its television and that television's CURRENT assignment,
        // in one read. Every clause is part of the answer: dropping any of them
        // leaves a grant that outlives the thing it was granted for.
        var row = await _db.PartyDisplayGrants.AsNoTracking()
            .Where(g => g.TokenHash == hash && g.RevokedAt == null && g.ExpiresAt > now)
            .Join(_db.TvSessions.AsNoTracking(), g => g.TvSessionId, t => t.Id,
                (g, t) => new
                {
                    g.PartyAlbumLinkId,
                    SessionLive = t.RevokedAt == null && t.ExpiresAt > now,
                    t.DisplayAssignment,
                    t.AssignedPartyAlbumLinkId,
                })
            .FirstOrDefaultAsync(cancellationToken);
        if (row is null || !row.SessionLive) return null;

        // The assignment may have moved since the grant was minted. A grant for
        // party A presented by a television now showing party B is not a grant
        // for B — and it is not a grant for A either, because that television
        // is no longer assigned to it.
        if (row.DisplayAssignment != TvDisplayAssignments.Party
            || row.AssignedPartyAlbumLinkId != row.PartyAlbumLinkId)
            return null;

        // Finally the party's own policy — status, expiry, capabilities — from
        // the same path a guest takes, so a display can never see a party a
        // guest could not.
        return await _links.ResolveDisplayAsync(row.PartyAlbumLinkId, cancellationToken);
    }

    public async Task<int> RevokeForSessionAsync(
        Guid tvSessionId, CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        return await _db.PartyDisplayGrants
            .Where(g => g.TvSessionId == tvSessionId && g.RevokedAt == null)
            .ExecuteUpdateAsync(
                s => s.SetProperty(g => g.RevokedAt, _ => (DateTime?)now), cancellationToken);
    }

    private async Task<TvSession?> LiveSessionAsync(
        string? sessionToken, DateTime now, CancellationToken cancellationToken)
    {
        var hash = HashTokenOrNull(sessionToken);
        if (hash is null) return null;
        return await _db.TvSessions.AsNoTracking()
            .FirstOrDefaultAsync(
                x => x.SessionTokenHash == hash && x.RevokedAt == null && x.ExpiresAt > now,
                cancellationToken);
    }

    private static string NewToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    internal static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    private static string? HashTokenOrNull(string? token) =>
        string.IsNullOrWhiteSpace(token) ? null : HashToken(token);
}
