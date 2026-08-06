using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Domain;
using NubArca.Api.Users;

namespace NubArca.Api.Auth;

internal static class CookieSessionValidator
{
    // The role string written into the principal when User.IsAdmin is true.
    // Centralised here so login and the revalidator agree.
    public const string AdminRole = "Admin";

    // Called by the cookie middleware on every authenticated request. Loads
    // the current User row by the NameIdentifier claim and:
    //   * rejects the principal if the user is missing or disabled (the
    //     existing slice-10 contract);
    //   * **re-issues** the principal with a synced admin role claim if the
    //     DB state diverges from the cookie. A revoked admin loses access
    //     within one request without needing a re-login; a newly-granted
    //     admin gains access the same way.
    public static async Task ValidateAsync(CookieValidatePrincipalContext context)
    {
        var idClaim = context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(idClaim, out var userId))
        {
            await RejectAsync(context);
            return;
        }

        var users = context.HttpContext.RequestServices.GetService<IUserService>();
        if (users is null)
        {
            // DB not configured (host started without ConnectionStrings:Postgres).
            // Without a user store we cannot honour a cookie, so reject.
            await RejectAsync(context);
            return;
        }

        var user = await users.GetByIdAsync(userId, context.HttpContext.RequestAborted);
        if (user is null || user.DisabledAt is not null)
        {
            await RejectAsync(context);
            return;
        }

        SyncAdminClaim(context, user);
    }

    private static void SyncAdminClaim(CookieValidatePrincipalContext context, User user)
    {
        var principal = context.Principal!;
        var hasAdminClaim = principal.IsInRole(AdminRole);
        if (hasAdminClaim == user.IsAdmin)
        {
            return;
        }

        // Build a new identity preserving every existing claim except role,
        // then add the admin role back if the user currently warrants it.
        var identity = principal.Identity as ClaimsIdentity;
        var authType = identity?.AuthenticationType
            ?? CookieAuthenticationDefaults.AuthenticationScheme;
        var preserved = principal.Claims
            .Where(c => c.Type != ClaimTypes.Role || c.Value != AdminRole)
            .ToList();
        if (user.IsAdmin)
        {
            preserved.Add(new Claim(ClaimTypes.Role, AdminRole));
        }

        context.ReplacePrincipal(new ClaimsPrincipal(new ClaimsIdentity(preserved, authType)));
        context.ShouldRenew = true;
    }

    private static Task RejectAsync(CookieValidatePrincipalContext context)
    {
        context.RejectPrincipal();
        return context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    }
}
