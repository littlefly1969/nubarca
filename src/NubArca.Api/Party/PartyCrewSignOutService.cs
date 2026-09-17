using Microsoft.EntityFrameworkCore;
using NubArca.Api.Audit;
using NubArca.Api.Data;

namespace NubArca.Api.Party;

/// <summary>
/// A collaborator's own devices, from the collaborator's side.
///
/// <para>The HOST can already list and revoke these from the party's settings.
/// This exists because the person themselves must be able to as well — on a
/// borrowed phone, at the end of an evening, or when the second slot is taken
/// by a device they no longer have. Needing to ask the host to be signed out is
/// not a security property; it is a product that traps people.</para>
///
/// <para><b>Only their own, and only this party's.</b> Every query is keyed on
/// the collaborator the CONTEXT resolved to, never on anything the client
/// said, so there is no id to change and nothing to reach.</para>
/// </summary>
public interface IPartyCrewSignOutService
{
    /// <summary>This collaborator's live devices, with the calling one marked.</summary>
    Task<IReadOnlyList<PartyCrewDeviceDto>> DevicesAsync(
        PartyCrewAccessContext ctx, CancellationToken ct = default);

    /// <summary>Signs the CALLING device out of this party.</summary>
    Task SignOutAsync(PartyCrewAccessContext ctx, string? ip, CancellationToken ct = default);

    /// <summary>
    /// Drops one of this collaborator's devices. False when the grant is not
    /// theirs, is already gone, or never existed — one answer for all three.
    /// </summary>
    Task<bool> RevokeAsync(
        PartyCrewAccessContext ctx, Guid grantId, string? ip, CancellationToken ct = default);
}

public sealed class PartyCrewSignOutService : IPartyCrewSignOutService
{
    private readonly AppDbContext _db;
    private readonly TimeProvider _clock;
    private readonly IAuditLogger _audit;

    public PartyCrewSignOutService(AppDbContext db, TimeProvider clock, IAuditLogger audit)
    {
        _db = db;
        _clock = clock;
        _audit = audit;
    }

    public async Task<IReadOnlyList<PartyCrewDeviceDto>> DevicesAsync(
        PartyCrewAccessContext ctx, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        // A filtered select with a correlated lookup, not a Join projecting
        // into the DTO: see the twin in PartyCrewAuthService for why.
        var rows = await _db.PartyCollaboratorDeviceGrants
            .Where(g => g.PartyCollaboratorId == ctx.CollaboratorId && g.RevokedAt == null)
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
            r.Id, r.Label ?? "Browser", r.CreatedAt, r.LastUsedAt,
            r.PartyCrewDeviceId == ctx.DeviceId))];
    }

    public Task SignOutAsync(PartyCrewAccessContext ctx, string? ip, CancellationToken ct = default) =>
        RevokeGrantAsync(ctx, ctx.DeviceGrantId, "self", ip, ct);

    public Task<bool> RevokeAsync(
        PartyCrewAccessContext ctx, Guid grantId, string? ip, CancellationToken ct = default) =>
        RevokeGrantAsync(ctx, grantId, grantId == ctx.DeviceGrantId ? "self" : "other-device", ip, ct);

    /// <summary>
    /// The grant, not the DEVICE. The same phone may be helping at another
    /// party, and leaving one evening is not leaving both — which is exactly
    /// why the two-device limit is counted on grants in the first place.
    /// </summary>
    private async Task<bool> RevokeGrantAsync(
        PartyCrewAccessContext ctx, Guid grantId, string which, string? ip, CancellationToken ct)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var revoked = await _db.PartyCollaboratorDeviceGrants
            // Scoped to the resolved collaborator, so another collaborator's
            // grant — or the same person's grant on a different party — matches
            // nothing and answers the same not-found.
            .Where(g => g.Id == grantId
                && g.PartyCollaboratorId == ctx.CollaboratorId
                && g.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(g => g.RevokedAt, _ => (DateTime?)now), ct);
        if (revoked == 0) return false;

        await _audit.LogAsync(
            AuditActor.Crew(ctx.CollaboratorId),
            "party.crew.device.revoke", "PartyCollaboratorDeviceGrant", grantId, ip,
            new { partyId = ctx.PartyId, target = which }, ct);
        return true;
    }
}
