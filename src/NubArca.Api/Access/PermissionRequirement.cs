using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace NubArca.Api.Access;

// "The caller must hold ALL of these permission keys" — or, for the `Any`
// variant, at least one of them. One requirement carries the whole set rather
// than several requirements carrying one each, because ASP.NET Core succeeds a
// policy when ANY handler succeeds for a requirement: modelling "A and B" as
// two requirements would work, but modelling it as one keeps the failure a
// single, obvious decision, and modelling "A or B" that way would be wrong.
public sealed class PermissionRequirement : IAuthorizationRequirement
{
    public PermissionRequirement(params string[] permissionKeys)
        : this(true, permissionKeys)
    {
    }

    private PermissionRequirement(bool requireAll, string[] permissionKeys)
    {
        if (permissionKeys.Length == 0)
        {
            throw new ArgumentException("A permission requirement needs at least one key.", nameof(permissionKeys));
        }
        foreach (var key in permissionKeys)
        {
            if (!PermissionCatalog.IsKnown(key))
            {
                // A typo here would produce a policy nobody can ever satisfy,
                // which reads at runtime as an unexplained 403. Fail at startup.
                throw new ArgumentException($"Unknown permission key '{key}'.", nameof(permissionKeys));
            }
        }
        RequireAll = requireAll;
        PermissionKeys = permissionKeys;
    }

    // "Any of these opens the door." Used where two different administrative
    // authorities legitimately need the same read — the role catalogue is read
    // by the Users editor and by the Roles editor — while the mutations stay
    // behind the single permission that owns them.
    public static PermissionRequirement Any(params string[] permissionKeys) =>
        new(false, permissionKeys);

    public bool RequireAll { get; }

    public IReadOnlyList<string> PermissionKeys { get; }
}

// Authorizes against CURRENT database state for the authenticated user id, not
// against a claim minted at login. That is the whole point: a permission taken
// away is gone on the next request, without a second session subsystem and
// without waiting out the cookie.
//
// Registered SCOPED (see Program.cs). The injected IServiceProvider is then the
// request's own, so the permission service resolved from it — and its DbContext
// and per-request memo — live and die with this request. A singleton handler
// would hold the root provider and answer every later request from the first
// one's cached permissions.
public sealed class PermissionAuthorizationHandler : AuthorizationHandler<PermissionRequirement>
{
    private readonly IServiceProvider _services;

    public PermissionAuthorizationHandler(IServiceProvider services)
    {
        _services = services;
    }

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        PermissionRequirement requirement)
    {
        var idClaim = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(idClaim, out var userId))
        {
            return;
        }

        // Resolved rather than constructor-injected because a host started
        // without a database registers no user store at all; that must present
        // as "nothing is authorized", not as a DI failure at startup.
        var permissions = _services.GetService<IUserPermissionService>();
        if (permissions is null)
        {
            // No user store configured (a host started without a database).
            // Nothing can be authorized, so the requirement simply stays unmet.
            return;
        }

        var effective = await permissions.GetEffectiveAsync(userId);
        var satisfied = requirement.RequireAll
            ? effective.HasAll(requirement.PermissionKeys)
            : effective.HasAny(requirement.PermissionKeys);
        if (satisfied)
        {
            context.Succeed(requirement);
        }
    }
}
