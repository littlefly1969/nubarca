using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using NubArca.Api.Data;
using NubArca.Api.Domain;

namespace NubArca.Api.Party;

// SECURITY MODEL
// --------------
// A party link's public token is a PUBLIC capability (printed as a QR at a
// party for anyone to scan) — not a secret credential. Two requirements are in
// tension: (a) "store only the token hash, never persist the raw token", and
// (b) "a paired TV re-renders the QR on every browse", which needs the server
// to reproduce the URL. We reconcile both by DERIVING the token deterministically
// from the row's random Id via HMAC-SHA256(serverSecret, Id): the raw token is
// never written to the DB (only its SHA-256 hash is), yet any owner-authorized
// surface can reproduce it on demand. The Id is a random GUID that is never
// exposed in any DTO/log, so the derived token stays unguessable. Re-enabling
// party mode creates a NEW row (new Id → new token), so an old QR/link dies.
public sealed class PartyLinkService : IPartyLinkService
{
    // Fallback used when Party:TokenSecret is not configured. Deriving from the
    // never-exposed row Id keeps tokens unguessable even with a known default;
    // production should still set Party__TokenSecret for defence in depth.
    // This literal is token key material: changing it invalidates every already
    // issued party QR/link signed WITHOUT a configured secret. Production
    // configures Party__TokenSecret, so this fallback is a dev/test convenience
    // there rather than live key material.
    private const string DefaultSecret = "nubarca-party-token-secret-v1";

    private readonly AppDbContext _db;
    private readonly TimeProvider _clock;
    private readonly IPartyService _parties;
    private readonly IPartyCapabilityPolicy _capabilities;
    private readonly byte[] _secret;

    public PartyLinkService(
        AppDbContext db,
        TimeProvider clock,
        IPartyService parties,
        IPartyCapabilityPolicy capabilities,
        IConfiguration config)
    {
        _db = db;
        _clock = clock;
        _parties = parties;
        _capabilities = capabilities;
        var configured = config["Party:TokenSecret"];
        _secret = Encoding.UTF8.GetBytes(
            string.IsNullOrWhiteSpace(configured) ? DefaultSecret : configured);
    }

    public async Task<PartyEnableResult?> EnableAsync(
        Guid ownerUserId, Guid albumId, Guid createdByUserId,
        bool? uploadEnabled = null,
        bool? requireApproval = null,
        bool? requireMessageApproval = null,
        CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;

        // The party is the root, so it is established FIRST — found when this
        // album is already somebody's party, created (with its `main` media
        // source) when it is not. Nothing is saved by that call: the party, its
        // media source and the capability below all land in one transaction, so
        // a party can never exist because a link failed to be written.
        //
        // Show-on-TV is deliberately NOT touched. A party and a television are
        // two independent publication decisions, and forcing one from the other
        // is exactly the coupling this slice removes.
        var party = await _parties.EnsureForAlbumAsync(ownerUserId, albumId, cancellationToken);
        if (party is null)
        {
            return null;
        }

        // Enabling the public capability IS publishing the party, so a party
        // still in Draft moves with it — through the same domain transition the
        // Party API uses, never by assigning a status here. A party the host has
        // already taken further (Live, or explicitly Ended) is left alone: this
        // entry point mints a QR, and re-opening an event is a decision the
        // Party surface owns.
        if (PartyLifecycle.Target(party.Status, PartyLifecycleAction.Publish) is string published)
        {
            party.Status = published;
            party.Version++;
            party.UpdatedAt = now;
        }

        // Reuse the current active link so the view token (and any printed QR)
        // stays stable when the owner only toggles the upload sub-switch. A fresh
        // link (new tokens) is minted only when none is active — e.g. after a
        // disable, which is how re-enabling rotates the token.
        var link = await _db.PartyAlbumLinks
            .Where(p => p.AlbumId == albumId && p.OwnerUserId == ownerUserId
                && p.Enabled && p.RevokedAt == null
                && (p.ExpiresAt == null || p.ExpiresAt > now))
            .OrderByDescending(p => p.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (link is not null)
        {
            if (uploadEnabled.HasValue)
            {
                link.UploadEnabled = uploadEnabled.Value;
            }
            if (requireApproval.HasValue)
            {
                link.RequireUploadApproval = requireApproval.Value;
            }
            if (requireMessageApproval.HasValue)
            {
                link.RequireMessageApproval = requireMessageApproval.Value;
            }
            // Heal an older link that predates the upload token (defensive).
            link.UploadTokenHash ??= HashToken(DeriveUploadToken(link.Id));
            link.UpdatedAt = now;
        }
        else
        {
            link = new PartyAlbumLink
            {
                Id = Guid.NewGuid(),
                PartyId = party.Id,
                // Compatibility projections of the party and its main source.
                OwnerUserId = ownerUserId,
                AlbumId = albumId,
                Enabled = true,
                UploadEnabled = uploadEnabled ?? true,
                RequireUploadApproval = requireApproval ?? false,
                RequireMessageApproval = requireMessageApproval ?? false,
                CreatedAt = now,
                UpdatedAt = now,
                RevokedAt = null,
                ExpiresAt = null,
                CreatedByUserId = createdByUserId,
            };
            link.TokenHash = HashToken(DeriveToken(link.Id));
            link.UploadTokenHash = HashToken(DeriveUploadToken(link.Id));
            _db.PartyAlbumLinks.Add(link);
        }

        // Converge a reused link on the party this album actually resolves to.
        // Normally already true — the migration gave every existing link its
        // party, and a new one is built with it above — so this is the cheap
        // guarantee that a live QR and the party it belongs to never drift.
        if (link.PartyId != party.Id)
        {
            link.PartyId = party.Id;
        }

        await _db.SaveChangesAsync(cancellationToken);
        return new PartyEnableResult(albumId, party.Id, link.Id, BuildPartyUrl(DeriveToken(link.Id)));
    }

    public async Task<bool> DisableAsync(
        Guid ownerUserId, Guid albumId,
        CancellationToken cancellationToken = default)
    {
        var owns = await _db.Albums
            .AsNoTracking()
            .AnyAsync(a => a.Id == albumId && a.OwnerUserId == ownerUserId, cancellationToken);
        if (!owns)
        {
            return false;
        }

        var now = _clock.GetUtcNow().UtcDateTime;
        await _db.PartyAlbumLinks
            .Where(p => p.AlbumId == albumId && p.OwnerUserId == ownerUserId
                && p.Enabled && p.RevokedAt == null)
            .ExecuteUpdateAsync(
                s => s.SetProperty(p => p.Enabled, false)
                      .SetProperty(p => p.RevokedAt, _ => now)
                      .SetProperty(p => p.UpdatedAt, _ => now),
                cancellationToken);
        return true;
    }

    public async Task<AlbumPartyStatusDto?> GetOwnerStatusAsync(
        Guid ownerUserId, Guid albumId,
        CancellationToken cancellationToken = default)
    {
        var album = await _db.Albums
            .AsNoTracking()
            .Where(a => a.Id == albumId && a.OwnerUserId == ownerUserId)
            .Select(a => new { a.ShowOnTv })
            .FirstOrDefaultAsync(cancellationToken);
        if (album is null)
        {
            return null;
        }

        // The party this album is the `main` source of, if it has ever been one.
        // Reported so the owner surface holds the product's identity without a
        // second request; null simply means party mode was never enabled here.
        var partyId = await _db.PartyMediaSources
            .AsNoTracking()
            .Where(s => s.AlbumId == albumId && s.Role == PartyMediaSourceRoles.Main)
            .Join(_db.Parties.AsNoTracking().Where(p => p.OwnerUserId == ownerUserId),
                s => s.PartyId, p => p.Id, (s, p) => new { p.Id, p.CreatedAt })
            .OrderBy(p => p.CreatedAt)
            .Select(p => (Guid?)p.Id)
            .FirstOrDefaultAsync(cancellationToken);

        var now = _clock.GetUtcNow().UtcDateTime;
        var active = await _db.PartyAlbumLinks
            .AsNoTracking()
            .Where(p => p.AlbumId == albumId && p.OwnerUserId == ownerUserId
                && p.Enabled && p.RevokedAt == null
                && (p.ExpiresAt == null || p.ExpiresAt > now))
            .OrderByDescending(p => p.CreatedAt)
            .Select(p => new
            {
                p.Id, p.UploadEnabled, p.UploadTokenHash, p.RequireUploadApproval,
                p.RequireMessageApproval,
                p.PhotoSlideSeconds, p.MaxVideoSlideSeconds,
                p.MaxPhotoUploadsPerParticipant, p.MaxVideoUploadsPerParticipant,
                p.MaxMessagesPerParticipant,
                p.GameEnabled, p.MinChallengeIntervalSeconds,
                p.MaxChallengeIntervalSeconds, p.VotesPerGuest,
                p.MaxChallengesPerSession,
            })
            .FirstOrDefaultAsync(cancellationToken);

        // Party mode is an ACTIVE LINK and nothing else. Show-on-TV is reported
        // beside it because the panel still shows both switches, but a party
        // held without a television in the room is a party.
        var partyMode = active is not null;
        var viewUrl = partyMode ? BuildPartyUrl(DeriveToken(active!.Id)) : null;
        var uploadOn = partyMode && active!.UploadEnabled && active.UploadTokenHash is not null;
        var uploadUrl = uploadOn ? BuildUploadUrl(DeriveUploadToken(active!.Id)) : null;
        var requireApproval = partyMode && active!.RequireUploadApproval;
        return new AlbumPartyStatusDto(
            albumId, partyId, album.ShowOnTv, partyMode, viewUrl, uploadOn, uploadUrl, requireApproval,
            // Defaults when no link exists yet, so the settings panel renders the
            // values a first enable would actually produce.
            active?.PhotoSlideSeconds ?? PartySlideshowDefaults.PhotoSeconds,
            active?.MaxVideoSlideSeconds ?? PartySlideshowDefaults.MaxVideoSeconds,
            active?.MaxPhotoUploadsPerParticipant ?? 0,
            active?.MaxVideoUploadsPerParticipant ?? 0,
            active?.MaxMessagesPerParticipant ?? 0,
            // Same rule as the upload approval flag: an inert link cannot be
            // holding a party to an approval mode.
            partyMode && active!.RequireMessageApproval,
            partyMode && active!.GameEnabled,
            active?.MinChallengeIntervalSeconds ?? PartyChallengeDefaults.MinIntervalSeconds,
            active?.MaxChallengeIntervalSeconds ?? PartyChallengeDefaults.MaxIntervalSeconds,
            active?.VotesPerGuest ?? PartyChallengeDefaults.VotesPerGuest,
            active?.MaxChallengesPerSession);
    }

    public async Task<bool> UpdateSlideshowSettingsAsync(
        Guid ownerUserId,
        Guid albumId,
        int? photoSlideSeconds,
        int? maxVideoSlideSeconds,
        int? maxPhotoUploadsPerParticipant,
        int? maxVideoUploadsPerParticipant,
        int? maxMessagesPerParticipant,
        CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        // The ACTIVE link only. A revoked/superseded row is inert and must not
        // be edited back into relevance.
        var link = await _db.PartyAlbumLinks
            .Where(p => p.AlbumId == albumId && p.OwnerUserId == ownerUserId
                && p.Enabled && p.RevokedAt == null
                && (p.ExpiresAt == null || p.ExpiresAt > now))
            .OrderByDescending(p => p.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (link is null)
        {
            return false;
        }

        // Only what was supplied changes. Nothing here touches TokenHash,
        // UploadTokenHash, Enabled, UploadEnabled or RequireUploadApproval, so a
        // guest holding the QR keeps working across a settings change — and the
        // participant counters are in another table entirely, so lowering a
        // quota leaves what people already uploaded exactly where it is.
        if (photoSlideSeconds is int photo) link.PhotoSlideSeconds = photo;
        if (maxVideoSlideSeconds is int video) link.MaxVideoSlideSeconds = video;
        if (maxPhotoUploadsPerParticipant is int maxPhotos) link.MaxPhotoUploadsPerParticipant = maxPhotos;
        if (maxVideoUploadsPerParticipant is int maxVideos) link.MaxVideoUploadsPerParticipant = maxVideos;
        if (maxMessagesPerParticipant is int maxMessages) link.MaxMessagesPerParticipant = maxMessages;
        link.UpdatedAt = now;
        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> UpdateGameSettingsAsync(
        Guid ownerUserId, Guid albumId, bool gameEnabled,
        int minChallengeIntervalSeconds, int maxChallengeIntervalSeconds,
        int votesPerGuest, int? maxChallengesPerSession,
        CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var link = await _db.PartyAlbumLinks
            .Where(p => p.AlbumId == albumId && p.OwnerUserId == ownerUserId
                && p.Enabled && p.RevokedAt == null
                && (p.ExpiresAt == null || p.ExpiresAt > now))
            .OrderByDescending(p => p.CreatedAt).FirstOrDefaultAsync(cancellationToken);
        if (link is null) return false;
        await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken);
        link.GameEnabled = gameEnabled;
        link.MinChallengeIntervalSeconds = minChallengeIntervalSeconds;
        link.MaxChallengeIntervalSeconds = maxChallengeIntervalSeconds;
        link.VotesPerGuest = votesPerGuest;
        link.MaxChallengesPerSession = maxChallengesPerSession;
        link.UpdatedAt = now;
        await _db.SaveChangesAsync(cancellationToken);
        if (!gameEnabled)
        {
            // Disabling is an immediate return to the legacy slideshow. Do not
            // retain a hidden hold that could reappear if the game is enabled
            // again later; the challenge remains eligible for a future session.
            await _db.PartyChallengeSessions
                .Where(s => s.PartyAlbumLinkId == link.Id)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(s => s.Mode, PartyPlaybackModes.Media)
                    .SetProperty(s => s.ActiveChallengeId, (Guid?)null)
                    .SetProperty(s => s.NextChallengeAt, (DateTime?)null)
                    .SetProperty(s => s.Version, s => s.Version + 1)
                    .SetProperty(s => s.UpdatedAt, now), cancellationToken);
        }
        await tx.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<IReadOnlyDictionary<Guid, PartyLinkUrls>> GetActivePartyUrlsAsync(
        Guid ownerUserId, IReadOnlyCollection<Guid> albumIds,
        CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<Guid, PartyLinkUrls>();
        if (albumIds.Count == 0)
        {
            return result;
        }

        // This method HANDS OUT public URLs, which is the one thing a host who
        // may not run parties must not be able to do. Anything a guest presents
        // or is given goes through the owner's role; the resolvers below do the
        // same at the other end of the same rule.
        var capabilities = await _capabilities.ForOwnerAsync(ownerUserId, cancellationToken);
        if (!capabilities.Access)
        {
            return result;
        }

        var now = _clock.GetUtcNow().UtcDateTime;

        // Albums with an active link. Show-on-TV is deliberately absent: the two
        // TV callers already filter their own album list on it, and a party does
        // not need a television to exist.
        var rows = await _db.PartyAlbumLinks
            .AsNoTracking()
            .Where(p => p.OwnerUserId == ownerUserId
                && albumIds.Contains(p.AlbumId)
                && p.Enabled && p.RevokedAt == null
                && (p.ExpiresAt == null || p.ExpiresAt > now))
            .Join(_db.Albums.AsNoTracking().Where(a => a.OwnerUserId == ownerUserId),
                p => p.AlbumId, a => a.Id,
                (p, a) => new
                {
                    p.AlbumId, p.Id, p.CreatedAt, p.UploadEnabled, p.UploadTokenHash,
                    p.PhotoSlideSeconds, p.MaxVideoSlideSeconds,
                })
            .ToListAsync(cancellationToken);

        foreach (var group in rows.GroupBy(r => r.AlbumId))
        {
            var latest = group.OrderByDescending(r => r.CreatedAt).First();
            var uploadUrl = latest.UploadEnabled && latest.UploadTokenHash is not null
                ? BuildUploadUrl(DeriveUploadToken(latest.Id))
                : null;
            result[group.Key] = new PartyLinkUrls(
                BuildPartyUrl(DeriveToken(latest.Id)),
                uploadUrl,
                new PartySlideshowTimingDto(latest.PhotoSlideSeconds, latest.MaxVideoSlideSeconds));
        }
        return result;
    }

    public async Task<PartyAccess?> ResolvePublicAsync(
        string token, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var hash = HashToken(token);
        var link = await _db.PartyAlbumLinks
            .AsNoTracking()
            .Where(p => p.TokenHash == hash)
            .Select(p => new LinkRow(
                p.Id, p.PartyId, p.Enabled, p.RevokedAt, p.ExpiresAt,
                p.RequireUploadApproval,
                p.MaxPhotoUploadsPerParticipant, p.MaxVideoUploadsPerParticipant,
                p.RequireMessageApproval, p.MaxMessagesPerParticipant))
            .FirstOrDefaultAsync(cancellationToken);

        return await BuildAccessAsync(link, isUploadGrant: false, cancellationToken);
    }

    public async Task<PartyAccess?> ResolveUploadAsync(
        string uploadToken, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(uploadToken))
        {
            return null;
        }

        var hash = HashToken(uploadToken);

        // Match the SEPARATE upload-token hash. A view token hashes to
        // TokenHash, never UploadTokenHash, so it can never authorize an upload
        // here (and vice-versa); the upload SUB-SWITCH is then required by
        // BuildAccessAsync, which is the only place either rule lives.
        var link = await _db.PartyAlbumLinks
            .AsNoTracking()
            .Where(p => p.UploadTokenHash == hash)
            .Select(p => new LinkRow(
                p.Id, p.PartyId, p.Enabled && p.UploadEnabled, p.RevokedAt, p.ExpiresAt,
                p.RequireUploadApproval,
                p.MaxPhotoUploadsPerParticipant, p.MaxVideoUploadsPerParticipant,
                p.RequireMessageApproval, p.MaxMessagesPerParticipant))
            .FirstOrDefaultAsync(cancellationToken);

        return await BuildAccessAsync(link, isUploadGrant: true, cancellationToken);
    }

    // What a link row carries into the seam. `Live` already folds in whichever
    // switches the caller's capability needs, so the validity rule below is one
    // expression for both tokens rather than two that must be kept in step.
    private sealed record LinkRow(
        Guid Id, Guid PartyId, bool Live, DateTime? RevokedAt, DateTime? ExpiresAt,
        bool RequireUploadApproval,
        int MaxPhotoUploadsPerParticipant, int MaxVideoUploadsPerParticipant,
        bool RequireMessageApproval, int MaxMessagesPerParticipant);

    // THE SEAM.
    //
    //     token -> PartyAlbumLink -> Party -> PartyMediaSource(main) -> Album
    //
    // Walked once, here, so every public Party endpoint receives a resolved
    // context and every service downstream keeps working on the
    // (ownerUserId, albumId) pair it already handles correctly. Nothing about
    // this walk is repeated anywhere else, and no service acquires a second,
    // PartyId-shaped copy of itself.
    //
    // Four independent things must all be true, and each is re-read on EVERY
    // request rather than trusted from when the QR was printed:
    //   * the capability is live (enabled, not revoked, not expired, and for an
    //     upload token the upload sub-switch is on);
    //   * the party has not closed guest access;
    //   * the party still has a `main` album, and it still belongs to the party's
    //     owner;
    //   * the OWNER's role still permits running a party at all.
    // Every failure returns null and becomes one generic 404 upstream, so an
    // unknown token, a revoked party and a host who lost the permission are
    // indistinguishable from outside.
    private async Task<PartyAccess?> BuildAccessAsync(
        LinkRow? link, bool isUploadGrant, CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        if (link is null || !link.Live || link.RevokedAt is not null
            || (link.ExpiresAt is not null && link.ExpiresAt <= now))
        {
            return null;
        }

        var party = await _db.Parties
            .AsNoTracking()
            .Where(p => p.Id == link.PartyId)
            .Select(p => new
            {
                p.Id, p.OwnerUserId, p.Status,
                p.GuestAccessExpiresAt, p.LibraryAccessExpiresAt,
            })
            .FirstOrDefaultAsync(cancellationToken);
        if (party is null)
        {
            return null;
        }

        // THE PUBLIC EXPERIENCE POLICY, asked once. It decides which of the
        // three surfaces this guest is looking at and how much of it is open —
        // the party's own windows included, which close every capability of the
        // party at once rather than one QR at a time.
        //
        // Null is one generic unavailable: a party still in Draft, a guest
        // window that closed while the party was being prepared or held, and
        // both windows expired are indistinguishable from outside, exactly as
        // an unknown token is.
        var experience = PartyGuestExperience.Resolve(
            party.Status, party.GuestAccessExpiresAt, party.LibraryAccessExpiresAt, now);
        if (experience is null)
        {
            return null;
        }

        // The `main` media source is what the rest of NubArca is handed. Ordered
        // so a party that acquires more sources later still resolves the same
        // album today rather than whichever row the database returned first.
        var mainAlbumId = await _db.PartyMediaSources
            .AsNoTracking()
            .Where(s => s.PartyId == party.Id && s.Role == PartyMediaSourceRoles.Main)
            .OrderBy(s => s.SortOrder)
            .ThenBy(s => s.AlbumId)
            .Select(s => (Guid?)s.AlbumId)
            .FirstOrDefaultAsync(cancellationToken);
        if (mainAlbumId is not Guid albumId)
        {
            return null;
        }

        // The album must still belong to the party's owner, so moving it out
        // severs public access even if a stale source row lingered. Show-on-TV
        // is deliberately NOT part of this: a party is not a television.
        var albumOk = await _db.Albums
            .AsNoTracking()
            .AnyAsync(a => a.Id == albumId && a.OwnerUserId == party.OwnerUserId, cancellationToken);
        if (!albumOk)
        {
            return null;
        }

        var capabilities = await _capabilities.ForOwnerAsync(party.OwnerUserId, cancellationToken);
        if (!capabilities.Access)
        {
            return null;
        }

        // A LIVE capability outside the party is not a capability. Folding the
        // phase in here is what keeps every endpoint's single check honest: a
        // surface the guest surface stops drawing cannot be reached by typing
        // its route either, and no endpoint acquires a lifecycle test of its own.
        if (!experience.AllowsLiveCapabilities)
        {
            capabilities = capabilities with
            {
                Contributions = false,
                Games = false,
                Print = false,
                FaceSearch = false,
            };
        }

        // An upload grant carries the link's approval mode and per-guest quotas
        // so the contribution paths need no second query; a view grant leaves
        // them at their defaults, exactly as before.
        return isUploadGrant
            ? new PartyAccess(
                party.Id, party.OwnerUserId, albumId, link.Id, capabilities, experience,
                link.RequireUploadApproval,
                link.MaxPhotoUploadsPerParticipant, link.MaxVideoUploadsPerParticipant,
                link.RequireMessageApproval, link.MaxMessagesPerParticipant)
            : new PartyAccess(
                party.Id, party.OwnerUserId, albumId, link.Id, capabilities, experience);
    }

    // view token = URL-safe base64 of HMAC-SHA256(secret, linkId). ~43 chars, 256-bit.
    private string DeriveToken(Guid linkId) => Derive(linkId.ToByteArray());

    public string DeriveViewToken(Guid linkId) => DeriveToken(linkId);

    // upload token = HMAC over linkId ++ "upload" — a DISTINCT high-entropy value
    // from the view token for the same link, so the two are independently
    // matchable and revocable.
    private string DeriveUploadToken(Guid linkId)
        => Derive([.. linkId.ToByteArray(), .. UploadContext]);

    // print token = HMAC over linkId ++ "print" — a THIRD distinct value for the
    // same link, purpose-bound to physical printing. Reading an album and
    // putting paper through a printer are different powers; deriving printing
    // from its own context is what keeps the view token from ever being one.
    internal string DerivePrintToken(Guid linkId)
        => Derive([.. linkId.ToByteArray(), .. PrintContext]);

    private static readonly byte[] UploadContext = Encoding.UTF8.GetBytes("upload");
    private static readonly byte[] PrintContext = Encoding.UTF8.GetBytes("print");

    private string Derive(byte[] input)
    {
        using var hmac = new HMACSHA256(_secret);
        var mac = hmac.ComputeHash(input);
        return Convert.ToBase64String(mac)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    internal static string HashToken(string token)
    {
        var bytes = Encoding.UTF8.GetBytes(token);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexStringLower(hash);
    }

    internal static string BuildPrintUrl(string printToken) => $"/party/{printToken}/print";

    internal static string BuildGameUrl(string viewToken) => $"/party/{viewToken}/game";

    internal static string BuildTvStageUrl(string viewToken) => $"/party/{viewToken}/tv";

    private static string BuildPartyUrl(string token) => $"/party/{token}";
    private static string BuildUploadUrl(string uploadToken) => $"/party/{uploadToken}/upload";
}
