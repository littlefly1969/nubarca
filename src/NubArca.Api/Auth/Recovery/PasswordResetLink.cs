namespace NubArca.Api.Auth.Recovery;

// Builds the URL a recovery email carries.
//
// The token goes in the FRAGMENT, never the path or the query. A fragment is
// never sent to the server, so it cannot appear in a reverse-proxy access log,
// an API log, a Referer header on an outbound link, or an error report. The
// frontend reads it from location.hash, keeps it in component memory, and calls
// history.replaceState immediately so it also leaves the address bar and the
// back/forward history.
public static class PasswordResetLink
{
    public const string Path = "/reset-password";

    public static string Build(string publicOrigin, string rawToken)
    {
        var origin = publicOrigin.TrimEnd('/');
        return $"{origin}{Path}#token={Uri.EscapeDataString(rawToken)}";
    }

    // The same URL with the token replaced by a placeholder. Anything that has
    // to mention the link — a log line, a diagnostic, an error message — uses
    // this, so no code path can accidentally write a live credential.
    public static string Redact(string publicOrigin)
    {
        var origin = publicOrigin.TrimEnd('/');
        return $"{origin}{Path}#token=<redacted>";
    }
}
