using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Tv;

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
/// still `party`, assignment still THIS link, and that party's projected
/// presentation still `game`. So un-pairing a television, pointing it
/// elsewhere, switching the game off, the party leaving its live phase, or a
/// finished game's closing card ending all invalidate the grant in the same
/// instant, and the four hours never come into it.</para>
///
/// <para>THE CAPABILITY FOLLOWS THE PRESENTATION. A grant is minted, and
/// honoured, only while <see cref="ITvPartyPresentationService"/> — the same
/// projection the control plane sends the television — says `game`. There is
/// no second account of "may this screen show the game" to drift from the
/// first.</para>
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
    private readonly ITvPartyPresentationService _presentation;
    private readonly ILogger<PartyDisplayService> _logger;

    public PartyDisplayService(
        AppDbContext db, TimeProvider clock, ITvPartyPresentationService presentation,
        ILogger<PartyDisplayService> logger)
    {
        _db = db;
        _clock = clock;
        _presentation = presentation;
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
            return Refused(session.Id, "not_assigned");

        // And the party must want its GAME on this screen right now — the same
        // projection the control plane sends the television. A party that is
        // not showable, not live, has its game switched off, or whose finished
        // game's closing card has ended is not something to mint a credential for.
        var party = await _presentation.ProjectAsync(linkId, cancellationToken);
        if (party.Presentation != TvPartyPresentations.Game)
            return Refused(session.Id, party.Showable ? "not_game" : "party_unavailable");

        // THE BOUNDARY IS THE TELEVISION'S OWN ROW.
        //
        // "Revoke the previous grant, insert a new one" is two statements, and
        // two mints of the same television arriving together — a remount racing
        // a renewal, a retry racing its own original — would otherwise both
        // revoke the SAME earlier grant and both insert, leaving two usable
        // credentials. So the transaction opens with a conditional update of
        // the session row whose WHERE clause is the whole claim (still live,
        // still assigned to THIS link) and which assigns a column to itself:
        // it changes nothing, and exists to take that row's write lock. A second
        // mint blocks on it, and when the first commits its revoke runs against
        // a fresh snapshot that includes the grant the first one just wrote.
        // It is the vote/close discipline of the Party Game, applied to a
        // device. Opening with a write also means the transaction never
        // upgrades a shared lock, which is the shape SQLite refuses to wait on.
        var owned = _db.Database.CurrentTransaction is null;
        var tx = owned ? await _db.Database.BeginTransactionAsync(cancellationToken) : null;
        try
        {
            var claimed = await _db.TvSessions
                .Where(t => t.Id == session.Id && t.RevokedAt == null && t.ExpiresAt > now
                    && t.DisplayAssignment == TvDisplayAssignments.Party
                    && t.AssignedPartyAlbumLinkId == linkId)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.LastSeenAt, t => t.LastSeenAt),
                    cancellationToken);
            if (claimed == 0)
            {
                // The assignment moved (or the session ended) between the read
                // above and the lock. Nothing has been written.
                if (owned) await tx!.RollbackAsync(cancellationToken);
                return Refused(session.Id, "assignment_changed");
            }

            // One live grant per television. A remount must not leave the
            // previous credential usable behind it.
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
            if (owned) await tx!.CommitAsync(cancellationToken);

            // The line names the device and the link, never the token or its hash.
            _logger.LogInformation(
                "party.display.grant.mint TvSessionId={TvSessionId} LinkId={LinkId}",
                session.Id, linkId);
            return PartyDisplayGrantResult.Ok(raw, expiresAt);
        }
        finally
        {
            if (owned && tx is not null) await tx.DisposeAsync();
        }
    }

    // A refusal is worth a line — it is how an operator tells "the television
    // keeps asking and the party is off" from "the television is not asking" —
    // but it names the device and a reason class, never a credential.
    private PartyDisplayGrantResult Refused(Guid tvSessionId, string reason)
    {
        _logger.LogInformation(
            "party.display.grant.refused TvSessionId={TvSessionId} Reason={Reason}",
            tvSessionId, reason);
        return PartyDisplayGrantResult.Fail(PartyDisplayGrantError.NotAssigned);
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

        // Finally the party itself, through the same projection the control
        // plane uses: its own policy (status, windows, host permission — the
        // guest's path, so a display never sees a party a guest could not), and
        // its presentation still `game`. A grant that was good a second ago
        // stops authorising the moment the party hands the screen back to its
        // slideshow, without anything having to be written.
        var party = await _presentation.ProjectAsync(row.PartyAlbumLinkId, cancellationToken);
        return party.Presentation == TvPartyPresentations.Game ? party.Access : null;
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
