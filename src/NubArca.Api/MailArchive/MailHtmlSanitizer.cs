using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using MimeKit.Text;

namespace NubArca.Api.MailArchive;

// Allow-list sanitizer for e-mail HTML (contract §5). It rebuilds the markup
// from MimeKit's tokenizer, keeping only known-inert elements and attributes:
//
//   * script/style/iframe/object/embed/form controls/svg/meta/link/base… are
//     dropped WITH their content; html/head/body wrappers are unwrapped;
//   * no event handler, no class-less trick: only listed attributes survive;
//   * href keeps http(s)/mailto only; src keeps `cid:` (rewritten to the
//     owner's live attachment file) and small inline raster `data:` images —
//     a remote image is never loaded automatically;
//   * inline style is kept only without url()/expression()/@import/behavior.
//
// The frontend adds a sandboxed iframe and a CSP on top: this is the first of
// two independent layers, not the only one.
public static partial class MailHtmlSanitizer
{
    private const int MaxDataUriChars = 512 * 1024;

    private static readonly HashSet<string> AllowedTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "abbr", "address", "b", "bdi", "bdo", "big", "blockquote", "br", "caption", "center",
        "cite", "code", "col", "colgroup", "dd", "del", "dfn", "div", "dl", "dt", "em", "font",
        "h1", "h2", "h3", "h4", "h5", "h6", "hr", "i", "img", "ins", "kbd", "li", "mark", "ol",
        "p", "pre", "q", "s", "samp", "small", "span", "strike", "strong", "sub", "sup", "table",
        "tbody", "td", "tfoot", "th", "thead", "tr", "tt", "u", "ul", "var", "wbr",
    };

    // Dropped together with everything inside them.
    private static readonly HashSet<string> DroppedWithContent = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "iframe", "frame", "frameset", "object", "embed", "applet", "noscript",
        "noembed", "noframes", "template", "svg", "math", "title", "head", "select", "textarea",
        "button", "audio", "video", "canvas", "map", "xml",
    };

    private static readonly HashSet<string> VoidTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "br", "col", "hr", "img", "wbr",
    };

    private static readonly HashSet<string> AllowedAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "align", "alt", "bgcolor", "border", "cellpadding", "cellspacing", "color", "colspan",
        "dir", "face", "height", "lang", "nowrap", "rowspan", "scope", "size", "span", "start",
        "style", "summary", "title", "type", "valign", "width", "headers", "abbr", "cite",
    };

    /// <param name="resolveCid">Maps a Content-ID (without "cid:" and angle
    /// brackets) to a same-origin URL, or null to drop the reference.</param>
    public static string Sanitize(string html, Func<string, string?> resolveCid)
    {
        var output = new StringBuilder(html.Length);
        var tokenizer = new HtmlTokenizer(new StringReader(html)) { DecodeCharacterReferences = true };
        var skipping = new Stack<string>();
        var open = new Stack<string>();

        while (tokenizer.ReadNextToken(out var token))
        {
            switch (token)
            {
                case HtmlTagToken tag:
                    var name = tag.Name.ToLowerInvariant();
                    if (skipping.Count > 0)
                    {
                        if (tag.IsEndTag && skipping.Peek() == name)
                        {
                            skipping.Pop();
                        }
                        else if (!tag.IsEndTag && !tag.IsEmptyElement && DroppedWithContent.Contains(name))
                        {
                            skipping.Push(name);
                        }
                        break;
                    }
                    if (DroppedWithContent.Contains(name))
                    {
                        if (!tag.IsEndTag && !tag.IsEmptyElement)
                        {
                            skipping.Push(name);
                        }
                        break;
                    }
                    if (!AllowedTags.Contains(name))
                    {
                        break; // unwrapped: its text content still renders
                    }
                    if (tag.IsEndTag)
                    {
                        if (!VoidTags.Contains(name) && open.Contains(name))
                        {
                            // Close up to and including the matching element.
                            while (open.Count > 0)
                            {
                                var top = open.Pop();
                                output.Append("</").Append(top).Append('>');
                                if (top == name)
                                {
                                    break;
                                }
                            }
                        }
                        break;
                    }
                    WriteStartTag(output, name, tag, resolveCid);
                    if (!VoidTags.Contains(name) && !tag.IsEmptyElement)
                    {
                        open.Push(name);
                    }
                    break;

                case HtmlDataToken data when token is not HtmlScriptDataToken:
                    if (skipping.Count == 0)
                    {
                        output.Append(WebUtility.HtmlEncode(data.Data));
                    }
                    break;

                // Comments (incl. conditional comments), doctype, CDATA and
                // script data are never emitted.
            }
        }
        while (open.Count > 0)
        {
            output.Append("</").Append(open.Pop()).Append('>');
        }
        return output.ToString();
    }

    // The text an HTML body shows, for the search index only: the same
    // tokenizer, elements dropped with their content skipped, every tag a
    // word boundary, character references decoded.
    public static string ToText(string html)
    {
        var output = new StringBuilder(html.Length / 2);
        var tokenizer = new HtmlTokenizer(new StringReader(html)) { DecodeCharacterReferences = true };
        var skipping = new Stack<string>();
        while (tokenizer.ReadNextToken(out var token))
        {
            switch (token)
            {
                case HtmlTagToken tag:
                    var name = tag.Name.ToLowerInvariant();
                    if (skipping.Count > 0)
                    {
                        if (tag.IsEndTag && skipping.Peek() == name)
                        {
                            skipping.Pop();
                        }
                        else if (!tag.IsEndTag && !tag.IsEmptyElement && DroppedWithContent.Contains(name))
                        {
                            skipping.Push(name);
                        }
                    }
                    else if (!tag.IsEndTag && !tag.IsEmptyElement && DroppedWithContent.Contains(name))
                    {
                        skipping.Push(name);
                    }
                    output.Append(' ');
                    break;
                case HtmlDataToken data when token is not HtmlScriptDataToken:
                    if (skipping.Count == 0)
                    {
                        output.Append(data.Data);
                    }
                    break;
            }
        }
        return output.ToString();
    }

    private static void WriteStartTag(StringBuilder output, string name, HtmlTagToken tag, Func<string, string?> resolveCid)
    {
        output.Append('<').Append(name);
        foreach (var attribute in tag.Attributes)
        {
            var attrName = attribute.Name.ToLowerInvariant();
            var value = attribute.Value ?? string.Empty;
            string? safe = attrName switch
            {
                "href" when name == "a" => SafeHref(value),
                "src" when name == "img" => SafeImageSource(value, resolveCid),
                "style" => SafeStyle(value),
                _ when AllowedAttributes.Contains(attrName) => value,
                _ => null,
            };
            if (safe is null)
            {
                continue;
            }
            output.Append(' ').Append(attrName).Append("=\"").Append(WebUtility.HtmlEncode(safe)).Append('"');
        }
        if (name == "a")
        {
            output.Append(" target=\"_blank\" rel=\"noopener noreferrer nofollow\"");
        }
        if (name == "img")
        {
            output.Append(" loading=\"lazy\" referrerpolicy=\"no-referrer\"");
        }
        output.Append('>');
    }

    private static string? SafeHref(string value)
    {
        var trimmed = StripInvisible(value);
        if (trimmed.StartsWith('#'))
        {
            return trimmed;
        }
        return Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeMailto)
            ? uri.AbsoluteUri
            : null;
    }

    private static string? SafeImageSource(string value, Func<string, string?> resolveCid)
    {
        var trimmed = StripInvisible(value);
        if (trimmed.StartsWith("cid:", StringComparison.OrdinalIgnoreCase))
        {
            var cid = trimmed[4..].Trim().Trim('<', '>');
            return cid.Length == 0 ? null : resolveCid(cid);
        }
        if (trimmed.Length <= MaxDataUriChars && DataImagePattern().IsMatch(trimmed))
        {
            return trimmed;
        }
        return null; // remote or unknown: never fetched automatically
    }

    private static string? SafeStyle(string value)
    {
        var lowered = StripInvisible(value).ToLowerInvariant();
        if (lowered.Contains("url(") || lowered.Contains("expression") || lowered.Contains("@import")
            || lowered.Contains("behavior") || lowered.Contains("javascript:") || lowered.Contains("-moz-binding")
            || lowered.Contains('\\') || lowered.Contains("position:fixed") || lowered.Contains("position: fixed"))
        {
            return null;
        }
        return value;
    }

    // Browsers ignore whitespace and control characters inside schemes
    // ("java\tscript:"); compare against what the browser would see.
    private static string StripInvisible(string value) =>
        new string(value.Where(c => !char.IsControl(c) && !char.IsWhiteSpace(c) || c == ' ').ToArray()).Trim();

    [GeneratedRegex("^data:image/(png|gif|jpeg|jpg|webp|bmp);base64,[A-Za-z0-9+/=\\s]+$", RegexOptions.IgnoreCase)]
    private static partial Regex DataImagePattern();
}
