namespace NubArca.Api.Http;

/// <summary>
/// RFC 7240 <c>Prefer: return=minimal</c>, for the few owner routes whose full
/// answer is a whole list.
///
/// <para>The guest list's mutations answer with the entire list, which is right
/// for a screen that shows it all and ruinous for one that pages a thousand
/// groups: every edit would download every group again. A client that holds the
/// list in pages says so with this preference and receives only what changed —
/// the route, its authorization, its audit and its refusals are exactly the
/// same. Honoured preferences are acknowledged with <c>Preference-Applied</c>,
/// as the RFC asks.</para>
/// </summary>
public static class PreferHeader
{
    public const string ReturnMinimal = "return=minimal";

    public static bool WantsMinimal(HttpContext context)
    {
        foreach (var header in context.Request.Headers["Prefer"])
        {
            if (header is null) continue;
            foreach (var preference in header.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                if (string.Equals(preference, ReturnMinimal, StringComparison.OrdinalIgnoreCase))
                {
                    context.Response.Headers["Preference-Applied"] = ReturnMinimal;
                    return true;
                }
            }
        }
        return false;
    }
}
