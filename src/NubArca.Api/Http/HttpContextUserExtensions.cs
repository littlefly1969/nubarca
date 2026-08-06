using System.Security.Claims;

namespace NubArca.Api.Http;

// Same logic as Program.cs's local `CurrentUserId(HttpContext)` helper.
// Endpoint modules under Endpoints/ use this extension; Program.cs keeps its
// own local helper for the many endpoints not yet extracted into modules.
public static class HttpContextUserExtensions
{
    public static Guid? GetCurrentUserId(this HttpContext httpContext)
    {
        var claim = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(claim, out var id) ? id : null;
    }
}
