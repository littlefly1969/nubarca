using Microsoft.EntityFrameworkCore;
using NubArca.Api.Ai.Documents.Sources;
using NubArca.Api.Data;

namespace NubArca.Api.Access;

// Document indexing skips an owner without document-search.access, so the
// documents a user already holds are never indexed while they lack it. Every
// path that can GIVE a user the key (a role edit, a role change, re-enabling
// an account, the CLI) asks this to queue one owner pass for them.
//
// The sink is idempotent and does nothing while document indexing is off, so
// a grant is never more than one small insert per user.
public sealed class DocumentSearchGrants
{
    private readonly AppDbContext _db;
    private readonly IDocumentSourceChangeSink _documentSources;

    public DocumentSearchGrants(AppDbContext db, IDocumentSourceChangeSink documentSources)
    {
        _db = db;
        _documentSources = documentSources;
    }

    // Read from the database, never from the request's permission memo, so a
    // caller can ask before and after its own save.
    public async Task<bool> HeldAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var user = await _db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new { u.RoleKey, u.DisabledAt })
            .FirstOrDefaultAsync(cancellationToken);
        if (user is null || user.DisabledAt is not null) return false;
        if (RoleKeys.IsAdministrator(user.RoleKey)) return true;
        return await _db.RolePermissions.AsNoTracking().AnyAsync(
            p => p.RoleKey == user.RoleKey && p.PermissionKey == Permissions.DocumentSearchAccess,
            cancellationToken);
    }

    public async Task CatchUpAsync(Guid userId, bool heldBefore, CancellationToken cancellationToken = default)
    {
        if (!heldBefore && await HeldAsync(userId, cancellationToken))
        {
            await _documentSources.OwnerSourcesChangedAsync(userId, cancellationToken);
        }
    }

    // A role just gained the key: every enabled user assigned to it. A role
    // holds a household's accounts, not a backfill's candidates, so the ids
    // are read in one query.
    public async Task CatchUpRoleAsync(string roleKey, CancellationToken cancellationToken = default)
    {
        var owners = await _db.Users.AsNoTracking()
            .Where(u => u.RoleKey == roleKey && u.DisabledAt == null)
            .Select(u => u.Id)
            .ToListAsync(cancellationToken);
        foreach (var owner in owners)
        {
            await _documentSources.OwnerSourcesChangedAsync(owner, cancellationToken);
        }
    }
}
