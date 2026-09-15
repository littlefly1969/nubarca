using System.Text;
using NubArca.Api.Domain;

namespace NubArca.Api.Party;

/// <summary>
/// The small HTML document a chat app reads to draw the card for a party's link.
///
/// <para>Open Graph tags and nothing a person would read: the frontend's nginx
/// sends only link-preview fetchers here. Every value is HTML-encoded, because the
/// title is the host's own text. Absolute addresses are built on the
/// installation's configured public origin — the one password-reset links use —
/// and never on the incoming Host header; without one the card simply carries no
/// picture and no address.</para>
/// </summary>
public static class PartyLinkPreview
{
    private const string ProductName = "NubArca";

    /// <summary>The configured public origin, or null when there is none worth trusting.</summary>
    public static string? Origin(string? configured) =>
        Uri.TryCreate(configured, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            ? uri.GetLeftPart(UriPartial.Authority)
            : null;

    /// <summary>The card for anything that does not open a party: the product, nothing else.</summary>
    public static string Generic(string? origin) => Document(
        ProductName,
        description: null,
        image: origin is null ? null : $"{origin}/brand/nubarca-pwa-512.png",
        url: null,
        largeImage: false);

    public static string ForParty(
        string? origin, string path, string title, string description, string? coverPath) =>
        Document(
            title,
            description,
            image: origin is not null && coverPath is not null ? origin + coverPath : null,
            url: origin is null ? null : origin + path,
            largeImage: true);

    /// <summary>One line about the party, in the phase a guest opening the link would find it.</summary>
    public static string Describe(PartyGuestPhase phase, DateTime? eventStartsAt, bool english) =>
        phase switch
        {
            PartyGuestPhase.Before => eventStartsAt is DateTime at
                ? $"{(english ? "You're invited" : "Sei invitato")} · {FormatDay(at, english)}"
                : english ? "You're invited" : "Sei invitato",
            PartyGuestPhase.Live => english ? "The party is on" : "La festa è in corso",
            _ => english ? "Thank you for being there" : "Grazie di esserci stati",
        };

    private static readonly string[] ItalianMonths =
    [
        "gennaio", "febbraio", "marzo", "aprile", "maggio", "giugno",
        "luglio", "agosto", "settembre", "ottobre", "novembre", "dicembre",
    ];

    private static readonly string[] EnglishMonths =
    [
        "January", "February", "March", "April", "May", "June",
        "July", "August", "September", "October", "November", "December",
    ];

    /// <summary>
    /// The DAY, in words, in the installation's own timezone — and deliberately no
    /// hour: the page formats that in the reader's timezone, and a card that
    /// named a different time from the page it opens would be worse than none.
    /// Month names are spelled out here rather than taken from a culture, so the
    /// card reads the same on a container built without ICU.
    /// </summary>
    internal static string FormatDay(DateTime at, bool english)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(
            DateTime.SpecifyKind(at, DateTimeKind.Utc), TimeZoneInfo.Local);
        var month = (english ? EnglishMonths : ItalianMonths)[local.Month - 1];
        return $"{local.Day} {month} {local.Year}";
    }

    private static string Document(
        string title, string? description, string? image, string? url, bool largeImage)
    {
        // Exactly the five characters that matter inside a double-quoted
        // attribute or text: everything else — "è", "·", an emoji in a party's
        // name — stays as the host wrote it, readable in the card and in the HTML.
        static string E(string value) => value
            .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
            .Replace("\"", "&quot;").Replace("'", "&#39;");
        var html = new StringBuilder();
        html.Append("<!doctype html><html><head><meta charset=\"utf-8\">");
        // A party's page is for the people holding its link, never for a search index.
        html.Append("<meta name=\"robots\" content=\"noindex, nofollow\">");
        html.Append("<title>").Append(E(title)).Append("</title>");
        html.Append("<meta property=\"og:site_name\" content=\"").Append(ProductName).Append("\">");
        html.Append("<meta property=\"og:type\" content=\"website\">");
        html.Append("<meta property=\"og:title\" content=\"").Append(E(title)).Append("\">");
        if (description is not null)
        {
            html.Append("<meta property=\"og:description\" content=\"").Append(E(description)).Append("\">");
            html.Append("<meta name=\"description\" content=\"").Append(E(description)).Append("\">");
        }
        if (image is not null)
        {
            html.Append("<meta property=\"og:image\" content=\"").Append(E(image)).Append("\">");
            html.Append("<meta name=\"twitter:image\" content=\"").Append(E(image)).Append("\">");
        }
        if (url is not null)
        {
            html.Append("<meta property=\"og:url\" content=\"").Append(E(url)).Append("\">");
        }
        html.Append("<meta name=\"twitter:card\" content=\"")
            .Append(largeImage && image is not null ? "summary_large_image" : "summary")
            .Append("\">");
        html.Append("</head><body>").Append(E(title)).Append("</body></html>");
        return html.ToString();
    }
}
