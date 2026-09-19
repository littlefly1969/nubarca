namespace NubArca.Api.Party;

/// <summary>
/// WHICH HALF of the contribution page a link opens.
///
/// <para>The party's contribution surface is one page on one token with two
/// things a guest can leave there — media, or a greeting for the slideshow.
/// Which half it opens on is a query parameter rather than a second route,
/// because the two share a token, a session and an enablement switch, and
/// splitting them would mean duplicating all three.</para>
///
/// <para>This is the SERVER's side of that contract; the browser's mirror is
/// <c>frontend/src/pages/partyContributionMode.ts</c>. It exists because the
/// guest context now states where the composer lives instead of letting the
/// client derive it: a client that derives a URL from a boolean is a client
/// that will eventually offer a surface the server does not have.</para>
/// </summary>
public static class PartyContributionModes
{
    public const string Param = "mode";
    public const string Media = "media";
    public const string MessageMode = "message";

    /// <summary>
    /// <paramref name="contributionUrl"/> opened on the composer.
    ///
    /// <para>The URL is used AS GIVEN and never rebuilt from parts: it is the
    /// link service's own, it is relative, and a token must not be
    /// re-serialised on its way through a string builder. A URL that already
    /// carries a query keeps it.</para>
    /// </summary>
    public static string Message(string contributionUrl) =>
        contributionUrl.Contains('?')
            ? $"{contributionUrl}&{Param}={MessageMode}"
            : $"{contributionUrl}?{Param}={MessageMode}";
}
