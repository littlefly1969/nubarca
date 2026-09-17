using Microsoft.EntityFrameworkCore;
using NubArca.Api.Access;
using NubArca.Api.Data;
using NubArca.Api.Domain;

namespace NubArca.Api.Party;

/// <summary>
/// Everything a Party Crew request is allowed to be, resolved from the cookie.
///
/// <para><b>The client supplies none of it.</b> Not the party, not the owner,
/// not the album, not the collaborator, not the capabilities. A crew request
/// carries one opaque device token and the server walks
/// <c>device → grant → collaborator → party → owner</c> itself — which is what
/// stops a collaborator on party A reaching party B by changing an id in a URL.
/// </para>
/// </summary>
public sealed record PartyCrewAccessContext(
    Guid PartyId,
    /// <summary>
    /// The party's owner, and the identity every downstream service runs as.
    ///
    /// <para>A collaborator id is NEVER passed where an owner id is expected.
    /// The existing services are owner-scoped and correct; Party Crew adapts at
    /// the entrance and reuses them, exactly as the guest seam does.</para>
    /// </summary>
    Guid OwnerUserId,
    Guid CollaboratorId,
    string CollaboratorName,
    string RoleKey,
    string PartyTitle,
    /// <summary>The party's main album, or null when the owner has not linked one.</summary>
    Guid? MainAlbumId,
    /// <summary>
    /// What this request may do: the collaborator's grants, already narrowed by
    /// what the OWNER is still permitted to run.
    /// </summary>
    IReadOnlySet<string> Capabilities,
    Guid DeviceId,
    Guid DeviceGrantId)
{
    public bool Can(string capability) => Capabilities.Contains(capability);
}

public interface IPartyCrewAccessResolver
{
    /// <summary>
    /// The context for one raw device token, or null. Re-read in full on every
    /// request; nothing is cached into the cookie.
    /// </summary>
    Task<PartyCrewAccessContext?> ResolveAsync(string? rawDeviceToken, CancellationToken ct = default);
}

/// <summary>
/// The Party Crew seam, walked once per request.
///
/// <para>Ten facts are re-read every time, and any one of them failing is the
/// same generic nothing:</para>
///
/// <list type="number">
///   <item>the cookie carries something shaped like a token;</item>
///   <item>a device matches its hash;</item>
///   <item>that device is not revoked and not expired;</item>
///   <item>it holds an unrevoked grant;</item>
///   <item>the grant's collaborator is not revoked;</item>
///   <item>the collaborator's party still exists;</item>
///   <item>the collaborator's capability grants are read fresh;</item>
///   <item>the OWNER still holds <c>party.access</c>;</item>
///   <item>each capability's required owner permission is still held;</item>
///   <item>the party's main album is resolved server-side.</item>
/// </list>
///
/// <para><b>Nothing is cached into the credential.</b> The cookie is an opaque
/// token and carries no party, no role and no capability, so revoking a
/// collaborator, changing their role, or taking a permission off the OWNER's
/// role takes effect on the very next request — with nobody signing out and no
/// token rotated. That is the same property the guest seam has, and it is why
/// delegation here is safe to hand out.</para>
///
/// <para><b>Delegation can never exceed its source.</b> The owner's effective
/// permissions are an upper bound applied AFTER the collaborator's own grants:
/// a director holding <c>activities.control</c> whose host lost
/// <c>party.games</c> holds nothing, because the host cannot run a game either.
/// </para>
/// </summary>
public sealed class PartyCrewAccessResolver : IPartyCrewAccessResolver
{
    private readonly AppDbContext _db;
    private readonly TimeProvider _clock;
    private readonly IUserPermissionService _permissions;

    public PartyCrewAccessResolver(
        AppDbContext db, TimeProvider clock, IUserPermissionService permissions)
    {
        _db = db;
        _clock = clock;
        _permissions = permissions;
    }

    public async Task<PartyCrewAccessContext?> ResolveAsync(
        string? rawDeviceToken, CancellationToken ct = default)
    {
        if (!PartyCrewTokens.LooksLikeToken(rawDeviceToken)) return null;
        var now = _clock.GetUtcNow().UtcDateTime;
        var hash = PartyCrewTokens.Hash(rawDeviceToken!);

        // One query for the whole chain. Every join carries its own liveness
        // condition, so there is no state in which a row is found and then
        // separately rejected.
        var found = await _db.PartyCrewDevices
            .Where(d => d.TokenHash == hash && d.RevokedAt == null && d.ExpiresAt > now)
            .Join(_db.PartyCollaboratorDeviceGrants.Where(g => g.RevokedAt == null),
                d => d.Id, g => g.PartyCrewDeviceId, (d, g) => new { Device = d, Grant = g })
            .Join(_db.PartyCollaborators.Where(c => c.RevokedAt == null),
                x => x.Grant.PartyCollaboratorId, c => c.Id,
                (x, c) => new { x.Device, x.Grant, Collaborator = c })
            .Join(_db.Parties, x => x.Collaborator.PartyId, p => p.Id,
                (x, p) => new
                {
                    x.Device,
                    x.Grant,
                    x.Collaborator,
                    Party = p,
                    MainAlbumId = _db.PartyMediaSources
                        .Where(m => m.PartyId == p.Id && m.Role == PartyMediaSourceRoles.Main)
                        .Select(m => (Guid?)m.AlbumId)
                        .FirstOrDefault(),
                })
            .FirstOrDefaultAsync(ct);
        if (found is null) return null;

        var granted = await _db.PartyCollaboratorGrants
            .Where(g => g.PartyCollaboratorId == found.Collaborator.Id)
            .Select(g => g.CapabilityKey)
            .ToListAsync(ct);

        // The owner's own authority, read from the database on every request
        // through the SAME service the authenticated endpoints use.
        var effective = await _permissions.GetEffectiveAsync(found.Party.OwnerUserId, ct);
        if (!effective.Has(Permissions.PartyAccess)) return null;

        var capabilities = granted
            .Where(PartyCrewCapabilities.IsKnown)
            .Where(key =>
            {
                var required = PartyCrewCapabilities.OwnerPermissionFor(key);
                return required is null || effective.Has(required);
            })
            .ToHashSet(StringComparer.Ordinal);

        // Touch the grant so a person can tell their two devices apart by when
        // each was last used. Fire-and-forget on the read path would be a write
        // per request; once a minute is enough to be useful.
        if (found.Grant.LastUsedAt is null || now - found.Grant.LastUsedAt.Value > TimeSpan.FromMinutes(1))
        {
            await _db.PartyCollaboratorDeviceGrants
                .Where(g => g.Id == found.Grant.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(g => g.LastUsedAt, _ => (DateTime?)now), ct);
            await _db.PartyCrewDevices
                .Where(d => d.Id == found.Device.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.LastSeenAt, _ => now), ct);
        }

        return new PartyCrewAccessContext(
            found.Party.Id,
            found.Party.OwnerUserId,
            found.Collaborator.Id,
            found.Collaborator.DisplayName,
            found.Collaborator.RoleKey,
            found.Party.Title,
            found.MainAlbumId,
            capabilities,
            found.Device.Id,
            found.Grant.Id);
    }
}
