using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NubArca.Api.Audit;
using NubArca.Api.Auth.Recovery;
using NubArca.Api.Data;
using NubArca.Api.Domain;

namespace NubArca.Api.Party;

/// <summary>Why an owner's Party Crew mutation was refused.</summary>
public enum PartyCrewError
{
    NotFound,
    InvalidName,
    InvalidEmail,
    InvalidRole,
    /// <summary>The role exists in the domain but this release does not offer it.</summary>
    RoleNotAssignable,
    /// <summary>Another live collaborator on this party already uses that address.</summary>
    EmailInUse,
    VersionConflict,
    /// <summary>Party Crew needs outbound mail: the second factor is an email.</summary>
    MailUnavailable,
}

public sealed record PartyCrewResult<T>(T? Value, PartyCrewError? Error)
{
    public static PartyCrewResult<T> Ok(T value) => new(value, null);
    public static PartyCrewResult<T> Fail(PartyCrewError error) => new(default, error);
    public bool Succeeded => Error is null;
}

public interface IPartyCrewService
{
    Task<PartyCrewOverviewDto?> OverviewAsync(Guid ownerUserId, Guid partyId, CancellationToken ct = default);

    Task<PartyCrewResult<PartyCollaboratorInviteDto>> CreateAsync(
        Guid ownerUserId, Guid partyId, PartyCollaboratorWriteDto body, string? ip, CancellationToken ct = default);

    Task<PartyCrewResult<PartyCollaboratorDto>> UpdateAsync(
        Guid ownerUserId, Guid partyId, Guid collaboratorId, PartyCollaboratorUpdateDto body,
        string? ip, CancellationToken ct = default);

    Task<PartyCrewResult<PartyCollaboratorInviteDto>> RotateInviteAsync(
        Guid ownerUserId, Guid partyId, Guid collaboratorId, string? ip, CancellationToken ct = default);

    Task<PartyCrewError?> RevokeAsync(
        Guid ownerUserId, Guid partyId, Guid collaboratorId, string? ip, CancellationToken ct = default);

    Task<PartyCrewError?> RevokeDeviceAsync(
        Guid ownerUserId, Guid partyId, Guid collaboratorId, Guid grantId, string? ip, CancellationToken ct = default);
}

/// <summary>
/// The OWNER's side of Party Crew: who helps with this party, as what, and on
/// which devices.
///
/// <para><b>Ownership is the first question on every call</b>, combined into
/// the query rather than checked after it, and a party that is not this
/// owner's is the same generic not-found as one that does not exist — the rule
/// the whole product uses.</para>
///
/// <para><b>The owner delegates once, and never approves a device.</b> That is
/// the security decision this service exists to express: authority is granted
/// here, by choosing a person's address and role, and every later device proves
/// itself against that address without waking anybody up. An owner who had to
/// approve each phone would approve them all, at midnight, from a party.</para>
/// </summary>
public sealed class PartyCrewService : IPartyCrewService
{
    // Deliberately permissive and purely structural. An address is validated by
    // sending to it, not by a regular expression opinionated about what a domain
    // may contain — that is exactly how a valid address gets refused.
    private static readonly Regex EmailShape =
        new(@"^[^@\s]+@[^@\s.]+(\.[^@\s.]+)+$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly AppDbContext _db;
    private readonly TimeProvider _clock;
    private readonly IEmailSender _email;
    private readonly IAuditLogger _audit;
    private readonly IOptionsMonitor<MailOptions> _mail;

    public PartyCrewService(
        AppDbContext db,
        TimeProvider clock,
        IEmailSender email,
        IAuditLogger audit,
        IOptionsMonitor<MailOptions> mail)
    {
        _db = db;
        _clock = clock;
        _email = email;
        _audit = audit;
        _mail = mail;
    }

    /// <summary>
    /// The operator's configured public origin, or null.
    ///
    /// <para>Never a request's Host header: a link that travels in a chat must
    /// not point wherever the request that minted it happened to arrive from.
    /// </para>
    /// </summary>
    private string? Origin => PartyLinkPreview.Origin(_mail.CurrentValue.PublicOrigin);

    /// <summary>Party Crew needs outbound mail AND somewhere to send people.</summary>
    internal bool CanPair => _email.IsEnabled && Origin is not null;

    public async Task<PartyCrewOverviewDto?> OverviewAsync(
        Guid ownerUserId, Guid partyId, CancellationToken ct = default)
    {
        if (!await OwnsAsync(ownerUserId, partyId, ct)) return null;
        var now = _clock.GetUtcNow().UtcDateTime;

        var collaborators = await _db.PartyCollaborators
            .Where(c => c.PartyId == partyId && c.RevokedAt == null)
            .OrderBy(c => c.CreatedAt)
            .ToListAsync(ct);

        var ids = collaborators.Select(c => c.Id).ToList();
        var grants = await _db.PartyCollaboratorGrants
            .Where(g => ids.Contains(g.PartyCollaboratorId))
            .Select(g => new { g.PartyCollaboratorId, g.CapabilityKey })
            .ToListAsync(ct);

        // A device counts only while BOTH halves are live: the grant and the
        // device behind it. An expired browser is not a slot somebody is using.
        var devices = await _db.PartyCollaboratorDeviceGrants
            .Where(g => ids.Contains(g.PartyCollaboratorId) && g.RevokedAt == null)
            .Join(_db.PartyCrewDevices.Where(d => d.RevokedAt == null && d.ExpiresAt > now),
                g => g.PartyCrewDeviceId, d => d.Id,
                (g, d) => new { g.Id, g.PartyCollaboratorId, g.CreatedAt, g.LastUsedAt, d.DeviceLabel })
            .OrderBy(x => x.CreatedAt)
            .ToListAsync(ct);

        var invites = await _db.PartyCollaboratorInvites
            .Where(i => ids.Contains(i.PartyCollaboratorId)
                && i.RevokedAt == null && i.ConsumedAt == null && i.ExpiresAt > now)
            .Select(i => new { i.PartyCollaboratorId, i.ExpiresAt })
            .ToListAsync(ct);

        var list = collaborators.Select(c =>
        {
            var mine = devices.Where(d => d.PartyCollaboratorId == c.Id).ToList();
            var invite = invites.FirstOrDefault(i => i.PartyCollaboratorId == c.Id);
            return new PartyCollaboratorDto(
                c.Id, c.DisplayName, c.Email, c.RoleKey, c.Version, c.CreatedAt,
                grants.Where(g => g.PartyCollaboratorId == c.Id)
                    .Select(g => g.CapabilityKey).OrderBy(k => k, StringComparer.Ordinal).ToList(),
                mine.Count,
                PartyCrewLimits.MaxDevicesPerCollaborator,
                invite is not null,
                invite?.ExpiresAt,
                mine.Select(d => new PartyCrewDeviceDto(
                    d.Id, Label(d.DeviceLabel), d.CreatedAt, d.LastUsedAt, false)).ToList());
        }).ToList();

        return new PartyCrewOverviewDto(
            list,
            CanPair,
            [PartyCrewRoles.CoOrganizer, PartyCrewRoles.Director]);
    }

    public async Task<PartyCrewResult<PartyCollaboratorInviteDto>> CreateAsync(
        Guid ownerUserId, Guid partyId, PartyCollaboratorWriteDto body, string? ip,
        CancellationToken ct = default)
    {
        if (!await OwnsAsync(ownerUserId, partyId, ct))
            return PartyCrewResult<PartyCollaboratorInviteDto>.Fail(PartyCrewError.NotFound);

        var validation = Validate(body.DisplayName, body.Email, body.RoleKey);
        if (validation is not null) return PartyCrewResult<PartyCollaboratorInviteDto>.Fail(validation.Value);

        // Refused BEFORE anything is written: a collaborator whose link cannot
        // be completed is worse than no collaborator, because the owner would
        // believe they had delegated something.
        if (!CanPair)
            return PartyCrewResult<PartyCollaboratorInviteDto>.Fail(PartyCrewError.MailUnavailable);

        var name = body.DisplayName.Trim();
        var email = body.Email.Trim();
        var normalized = Normalize(email);
        var now = _clock.GetUtcNow().UtcDateTime;

        if (await _db.PartyCollaborators.AnyAsync(
                c => c.PartyId == partyId && c.RevokedAt == null && c.NormalizedEmail == normalized, ct))
            return PartyCrewResult<PartyCollaboratorInviteDto>.Fail(PartyCrewError.EmailInUse);

        var collaborator = new PartyCollaborator
        {
            Id = Guid.NewGuid(),
            PartyId = partyId,
            DisplayName = name,
            Email = email,
            NormalizedEmail = normalized,
            RoleKey = body.RoleKey,
            Version = 1,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.PartyCollaborators.Add(collaborator);
        ApplyPreset(collaborator.Id, body.RoleKey, now);

        var (invite, url) = MintInvite(collaborator.Id, now);
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(ownerUserId, "party.crew.collaborator.create", "PartyCollaborator",
            collaborator.Id, ip, new { partyId, role = body.RoleKey }, ct);

        return PartyCrewResult<PartyCollaboratorInviteDto>.Ok(
            new PartyCollaboratorInviteDto(collaborator.Id, url, invite.ExpiresAt));
    }

    public async Task<PartyCrewResult<PartyCollaboratorDto>> UpdateAsync(
        Guid ownerUserId, Guid partyId, Guid collaboratorId, PartyCollaboratorUpdateDto body,
        string? ip, CancellationToken ct = default)
    {
        if (!await OwnsAsync(ownerUserId, partyId, ct))
            return PartyCrewResult<PartyCollaboratorDto>.Fail(PartyCrewError.NotFound);

        var validation = Validate(body.DisplayName, body.Email, body.RoleKey);
        if (validation is not null) return PartyCrewResult<PartyCollaboratorDto>.Fail(validation.Value);

        var collaborator = await _db.PartyCollaborators
            .FirstOrDefaultAsync(c => c.Id == collaboratorId && c.PartyId == partyId && c.RevokedAt == null, ct);
        if (collaborator is null) return PartyCrewResult<PartyCollaboratorDto>.Fail(PartyCrewError.NotFound);
        if (collaborator.Version != body.Version)
            return PartyCrewResult<PartyCollaboratorDto>.Fail(PartyCrewError.VersionConflict);

        var now = _clock.GetUtcNow().UtcDateTime;
        var normalized = Normalize(body.Email.Trim());
        var emailChanged = normalized != collaborator.NormalizedEmail;
        var roleChanged = body.RoleKey != collaborator.RoleKey;

        if (emailChanged && await _db.PartyCollaborators.AnyAsync(
                c => c.PartyId == partyId && c.Id != collaboratorId
                    && c.RevokedAt == null && c.NormalizedEmail == normalized, ct))
            return PartyCrewResult<PartyCollaboratorDto>.Fail(PartyCrewError.EmailInUse);

        collaborator.DisplayName = body.DisplayName.Trim();
        collaborator.Email = body.Email.Trim();
        collaborator.NormalizedEmail = normalized;
        collaborator.RoleKey = body.RoleKey;
        collaborator.Version++;
        collaborator.UpdatedAt = now;

        // ONE unit of work, because the pieces below are not the same kind of
        // write. `ExecuteDelete`/`ExecuteUpdate` hit the database immediately
        // while the tracked edits above wait for `SaveChanges`, so without a
        // transaction a failure between them would leave a collaborator with
        // their old role and NO capabilities — authority silently gone, and
        // nothing to say so.
        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        if (roleChanged)
        {
            // The whole set, replaced. A role is one decision and its preset is
            // the answer; merging would leave authority from a role the person
            // no longer holds. It takes effect on their NEXT request — no
            // re-pairing, because the device proved a person, not a permission.
            await _db.PartyCollaboratorGrants
                .Where(g => g.PartyCollaboratorId == collaboratorId)
                .ExecuteDeleteAsync(ct);
            ApplyPreset(collaboratorId, body.RoleKey, now);
        }

        if (emailChanged)
        {
            // EVERY device goes. They were each verified against an address the
            // owner has now replaced, which means they were verified against
            // somebody who may not be the person the owner means. Open invites
            // and challenges go with them for the same reason.
            await RevokeEverythingAsync(collaboratorId, now, ct);
        }

        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        // Audited AFTER the commit: a trail that describes a change which was
        // rolled back is worse than no line at all.
        if (emailChanged)
        {
            await _audit.LogAsync(ownerUserId, "party.crew.collaborator.email-change", "PartyCollaborator",
                collaboratorId, ip, new { partyId }, ct);
        }
        if (roleChanged)
        {
            await _audit.LogAsync(ownerUserId, "party.crew.collaborator.role-change", "PartyCollaborator",
                collaboratorId, ip, new { partyId, role = body.RoleKey }, ct);
        }

        var overview = await OverviewAsync(ownerUserId, partyId, ct);
        var dto = overview?.Collaborators.FirstOrDefault(c => c.Id == collaboratorId);
        return dto is null
            ? PartyCrewResult<PartyCollaboratorDto>.Fail(PartyCrewError.NotFound)
            : PartyCrewResult<PartyCollaboratorDto>.Ok(dto);
    }

    public async Task<PartyCrewResult<PartyCollaboratorInviteDto>> RotateInviteAsync(
        Guid ownerUserId, Guid partyId, Guid collaboratorId, string? ip, CancellationToken ct = default)
    {
        if (!await OwnsAsync(ownerUserId, partyId, ct))
            return PartyCrewResult<PartyCollaboratorInviteDto>.Fail(PartyCrewError.NotFound);
        if (!CanPair)
            return PartyCrewResult<PartyCollaboratorInviteDto>.Fail(PartyCrewError.MailUnavailable);

        var exists = await _db.PartyCollaborators.AnyAsync(
            c => c.Id == collaboratorId && c.PartyId == partyId && c.RevokedAt == null, ct);
        if (!exists) return PartyCrewResult<PartyCollaboratorInviteDto>.Fail(PartyCrewError.NotFound);

        var now = _clock.GetUtcNow().UtcDateTime;
        var (invite, url) = MintInvite(collaboratorId, now);
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(ownerUserId, "party.crew.invite.rotate", "PartyCollaborator",
            collaboratorId, ip, new { partyId }, ct);

        return PartyCrewResult<PartyCollaboratorInviteDto>.Ok(
            new PartyCollaboratorInviteDto(collaboratorId, url, invite.ExpiresAt));
    }

    public async Task<PartyCrewError?> RevokeAsync(
        Guid ownerUserId, Guid partyId, Guid collaboratorId, string? ip, CancellationToken ct = default)
    {
        if (!await OwnsAsync(ownerUserId, partyId, ct)) return PartyCrewError.NotFound;

        var collaborator = await _db.PartyCollaborators
            .FirstOrDefaultAsync(c => c.Id == collaboratorId && c.PartyId == partyId && c.RevokedAt == null, ct);
        if (collaborator is null) return PartyCrewError.NotFound;

        var now = _clock.GetUtcNow().UtcDateTime;
        collaborator.RevokedAt = now;
        collaborator.Version++;
        collaborator.UpdatedAt = now;

        // One unit of work, for the reason UpdateAsync states: the device
        // revocations are immediate and the collaborator's own row is not, and
        // "their devices are dead but they are still listed as helping" is not
        // a state the host should ever be shown.
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        await RevokeEverythingAsync(collaboratorId, now, ct);
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        await _audit.LogAsync(ownerUserId, "party.crew.collaborator.revoke", "PartyCollaborator",
            collaboratorId, ip, new { partyId }, ct);
        return null;
    }

    public async Task<PartyCrewError?> RevokeDeviceAsync(
        Guid ownerUserId, Guid partyId, Guid collaboratorId, Guid grantId, string? ip,
        CancellationToken ct = default)
    {
        if (!await OwnsAsync(ownerUserId, partyId, ct)) return PartyCrewError.NotFound;

        // The grant, the collaborator AND the party in one query: an owner of
        // party A must not reach a grant belonging to party B by knowing its id.
        var revoked = await _db.PartyCollaboratorDeviceGrants
            .Where(g => g.Id == grantId && g.RevokedAt == null
                && _db.PartyCollaborators.Any(c =>
                    c.Id == g.PartyCollaboratorId && c.Id == collaboratorId && c.PartyId == partyId))
            .ExecuteUpdateAsync(
                s => s.SetProperty(g => g.RevokedAt, _ => (DateTime?)_clock.GetUtcNow().UtcDateTime), ct);
        if (revoked == 0) return PartyCrewError.NotFound;

        await _audit.LogAsync(ownerUserId, "party.crew.device.revoke", "PartyCollaboratorDeviceGrant",
            grantId, ip, new { partyId, collaboratorId, by = "owner" }, ct);
        return null;
    }

    // ── Shared ──────────────────────────────────────────────────────────────

    private Task<bool> OwnsAsync(Guid ownerUserId, Guid partyId, CancellationToken ct) =>
        _db.Parties.AnyAsync(p => p.Id == partyId && p.OwnerUserId == ownerUserId, ct);

    private static PartyCrewError? Validate(string? name, string? email, string? roleKey)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > PartyCrewLimits.MaxDisplayNameLength)
            return PartyCrewError.InvalidName;
        var trimmed = email?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed)
            || trimmed.Length > PartyCrewLimits.MaxEmailLength
            || !EmailShape.IsMatch(trimmed))
            return PartyCrewError.InvalidEmail;
        if (!PartyCrewRoles.IsKnown(roleKey)) return PartyCrewError.InvalidRole;
        // Known but not offered: DJ, Accoglienza and Festeggiato exist in the
        // domain and are refused here, so enabling them later is a UI decision
        // rather than a migration.
        if (!PartyCrewRoles.IsAssignable(roleKey)) return PartyCrewError.RoleNotAssignable;
        return null;
    }

    private static string Normalize(string email) => email.Trim().ToLowerInvariant();

    private void ApplyPreset(Guid collaboratorId, string roleKey, DateTime now)
    {
        foreach (var capability in PartyCrewRoles.Preset(roleKey))
        {
            _db.PartyCollaboratorGrants.Add(new PartyCollaboratorGrant
            {
                Id = Guid.NewGuid(),
                PartyCollaboratorId = collaboratorId,
                CapabilityKey = capability,
                CreatedAt = now,
            });
        }
    }

    /// <summary>
    /// A fresh link, and the death of every earlier one.
    ///
    /// <para>"Send a new link" must mean the old one stops working: two live
    /// links would double the ways in and halve the meaning of revoking one.
    /// </para>
    /// </summary>
    private (PartyCollaboratorInvite Invite, string Url) MintInvite(Guid collaboratorId, DateTime now)
    {
        foreach (var previous in _db.PartyCollaboratorInvites.Local
                     .Where(i => i.PartyCollaboratorId == collaboratorId && i.RevokedAt == null))
        {
            previous.RevokedAt = now;
        }
        _db.PartyCollaboratorInvites
            .Where(i => i.PartyCollaboratorId == collaboratorId
                && i.RevokedAt == null && i.ConsumedAt == null)
            .ExecuteUpdate(s => s.SetProperty(i => i.RevokedAt, _ => (DateTime?)now));

        var raw = PartyCrewTokens.NewToken();
        var invite = new PartyCollaboratorInvite
        {
            Id = Guid.NewGuid(),
            PartyCollaboratorId = collaboratorId,
            TokenHash = PartyCrewTokens.Hash(raw),
            CreatedAt = now,
            ExpiresAt = now.Add(PartyCrewLimits.InviteLifetime),
        };
        _db.PartyCollaboratorInvites.Add(invite);

        // Built on the OPERATOR's configured origin, never a request Host
        // header: a link in somebody's chat must not point wherever the request
        // that minted it happened to arrive from.
        return (invite, $"{Origin!.TrimEnd('/')}{PartyCrewTokens.InvitePath(raw)}");
    }

    /// <summary>Every credential a collaborator holds, gone in one stroke.</summary>
    private async Task RevokeEverythingAsync(Guid collaboratorId, DateTime now, CancellationToken ct)
    {
        await _db.PartyCollaboratorDeviceGrants
            .Where(g => g.PartyCollaboratorId == collaboratorId && g.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(g => g.RevokedAt, _ => (DateTime?)now), ct);
        await _db.PartyCollaboratorInvites
            .Where(i => i.PartyCollaboratorId == collaboratorId && i.RevokedAt == null && i.ConsumedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.RevokedAt, _ => (DateTime?)now), ct);
        await _db.PartyCollaboratorAuthChallenges
            .Where(c => c.PartyCollaboratorId == collaboratorId && c.RevokedAt == null && c.CompletedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.RevokedAt, _ => (DateTime?)now), ct);
    }

    internal static string Label(string? stored) =>
        string.IsNullOrWhiteSpace(stored) ? "Browser" : stored;
}
