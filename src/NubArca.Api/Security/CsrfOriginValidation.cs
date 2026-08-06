using Microsoft.AspNetCore.Http;

namespace NubArca.Api.Security;

// Centralized CSRF defence-in-depth for unsafe (state-changing) requests to the
// JSON API (slice 54.2). The auth cookie is HttpOnly + SameSite=Lax, which is
// the primary browser-side CSRF mitigation; this middleware adds an explicit
// same-origin Origin/Referer check so a cross-site write driven by a victim's
// ambient cookie is rejected before it reaches any endpoint.
//
// Policy:
//   * Safe methods (GET/HEAD/OPTIONS/TRACE) are never blocked.
//   * Only the "/api" surface is guarded. The public "/s/{token}" share route
//     is GET-only, and "/health" is GET, so neither is in scope.
//   * For an unsafe "/api" request, the Origin header (or, when absent, the
//     Referer) must match the request's own scheme/host/port. A clearly
//     cross-origin value is rejected with 403.
//   * When BOTH Origin and Referer are absent, the request is allowed. Such a
//     caller is a non-browser client (curl / server-to-server / API tooling /
//     the test harness): it cannot be steered by a victim's ambient cookie the
//     way a browser can, so it is not a CSRF vector. Browsers always attach an
//     Origin on cross-site unsafe fetch/XHR, so the attack we care about is
//     always caught. This choice is covered by tests.
//
// Runs after ForwardedHeaders so Request.Scheme / Request.Host reflect the
// reverse proxy.
public static class CsrfOriginValidation
{
    private static readonly HashSet<string> SafeMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        HttpMethods.Get, HttpMethods.Head, HttpMethods.Options, HttpMethods.Trace,
    };

    public static async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        var request = context.Request;
        if (RequiresCheck(request) && !IsSameOrigin(request))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = "Cross-origin request rejected." });
            return;
        }

        await next(context);
    }

    private static bool RequiresCheck(HttpRequest request)
        => !SafeMethods.Contains(request.Method)
           && request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase);

    private static bool IsSameOrigin(HttpRequest request)
    {
        var origin = request.Headers.Origin.ToString();
        if (!string.IsNullOrEmpty(origin))
        {
            return OriginMatches(request, origin);
        }

        var referer = request.Headers.Referer.ToString();
        if (!string.IsNullOrEmpty(referer))
        {
            return OriginMatches(request, referer);
        }

        // No Origin and no Referer — non-browser client (see class comment).
        return true;
    }

    private static bool OriginMatches(HttpRequest request, string headerValue)
    {
        if (!Uri.TryCreate(headerValue, UriKind.Absolute, out var uri))
        {
            // Malformed header — treat as cross-origin (fail closed).
            return false;
        }

        var expectedPort = request.Host.Port
            ?? (string.Equals(request.Scheme, "https", StringComparison.OrdinalIgnoreCase) ? 443 : 80);

        return string.Equals(uri.Scheme, request.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(uri.Host, request.Host.Host, StringComparison.OrdinalIgnoreCase)
            && uri.Port == expectedPort;
    }
}
