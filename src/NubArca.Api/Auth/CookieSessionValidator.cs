using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Access;
using NubArca.Api.Domain;
using NubArca.Api.Users;

namespace NubArca.Api.Auth;

internal static class CookieSessionValidator
{
    // The claim carrying User.SecurityVersion. A credential change increments
    // the row; a cookie minted before it therefore disagrees and is rejected on
    // its very next request. That is the whole session-invalidation mechanism —
    // no server-side session table, no second auth subsystem, just one integer
    // compared against the one the cookie already carries.
    public const string SecurityVersionClaim = "nubarca:sv";

    // The version a cookie issued BEFORE this claim existed is treated as. It is
    // deliberately the migration's default (1) rather than "whatever the row says
    // now": adopting the current value would let a pre-upgrade cookie survive a
    // password reset that happened after the upgrade, which is exactly the case
    // the version exists to kill.
    private const int LegacyCookieSecurityVersion = 1;

    // Called by the cookie middleware on every authenticated request. Loads the
    // current User row by the NameIdentifier claim and:
    //   * rejects the principal if the user is missing or disabled (the
    //     existing slice-10 contract);
    //   * rejects it if the cookie's security version is behind the row's, so a
    //     password change or recovery signs out the sessions that predate it;
    //   * re-issues the principal with the current role claim when the two
    //     disagree, so a role change is reflected within one request.
    //
    // Feature PERMISSIONS are not carried in the cookie at all: the permission
    // handler reads them from the database per request, which is why a granted
    // or revoked permission takes effect immediately without a re-login.
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

        if (ReadSecurityVersion(context.Principal!) != user.SecurityVersion)
        {
            await RejectAsync(context);
            return;
        }

        SyncRoleClaim(context, user);
    }

    public static IEnumerable<Claim> BuildClaims(User user) =>
    [
        new(ClaimTypes.NameIdentifier, user.Id.ToString()),
        new(ClaimTypes.Email, user.Email),
        new(ClaimTypes.Name, user.DisplayName),
        new(ClaimTypes.Role, user.RoleKey),
        new(SecurityVersionClaim, user.SecurityVersion.ToString(CultureInfo.InvariantCulture)),
    ];

    private static int ReadSecurityVersion(ClaimsPrincipal principal)
    {
        var raw = principal.FindFirstValue(SecurityVersionClaim);
        if (raw is null)
        {
            return LegacyCookieSecurityVersion;
        }
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var version)
            ? version
            // A malformed claim is not a legacy cookie; it is a cookie nobody
            // should have. -1 matches no row.
            : -1;
    }

    private static void SyncRoleClaim(CookieValidatePrincipalContext context, User user)
    {
        var principal = context.Principal!;
        if (principal.IsInRole(user.RoleKey)
            && principal.FindAll(ClaimTypes.Role).Count() == 1)
        {
            return;
        }

        // Build a new identity preserving every existing claim except role, then
        // add the role the row currently says. The security-version claim is
        // preserved as-is: it already matched, and re-stamping it here would
        // quietly repair a cookie the check above is meant to reject.
        var identity = principal.Identity as ClaimsIdentity;
        var authType = identity?.AuthenticationType
            ?? CookieAuthenticationDefaults.AuthenticationScheme;
        var preserved = principal.Claims
            .Where(c => c.Type != ClaimTypes.Role)
            .ToList();
        preserved.Add(new Claim(ClaimTypes.Role, user.RoleKey));

        context.ReplacePrincipal(new ClaimsPrincipal(new ClaimsIdentity(preserved, authType)));
        context.ShouldRenew = true;
    }

    private static Task RejectAsync(CookieValidatePrincipalContext context)
    {
        context.RejectPrincipal();
        return context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    }
}
