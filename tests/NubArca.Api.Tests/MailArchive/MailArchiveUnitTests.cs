using NubArca.Api.MailArchive;
using Xunit;

namespace NubArca.Api.Tests.MailArchive;

public sealed class MailHtmlSanitizerTests
{
    private static string Clean(string html) => MailHtmlSanitizer.Sanitize(html, cid => cid == "logo" ? "/api/files/1/content" : null);

    [Theory]
    [InlineData("<script>alert(1)</script>ok", "ok")]
    [InlineData("<p onclick=\"x()\">a</p>", "<p>a</p>")]
    [InlineData("<iframe src=\"https://evil.test\"></iframe>b", "b")]
    [InlineData("<svg><script>alert(1)</script></svg>c", "c")]
    [InlineData("<style>body{}</style>d", "d")]
    [InlineData("<!-- <script>x</script> -->e", "e")]
    [InlineData("<custom>f</custom>", "f")]
    public void Active_content_is_removed(string html, string expected) => Assert.Equal(expected, Clean(html));

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("java\tscript:alert(1)")]
    [InlineData("JaVaScRiPt:alert(1)")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    [InlineData("vbscript:msgbox")]
    public void Dangerous_links_lose_their_href(string href)
    {
        var html = Clean($"<a href=\"{href}\">x</a>");
        Assert.DoesNotContain("href", html);
        Assert.Contains(">x</a>", html);
    }

    [Fact]
    public void Safe_links_open_in_a_new_tab_without_referrer()
    {
        var html = Clean("<a href=\"https://example.test/a\">x</a>");
        Assert.Contains("href=\"https://example.test/a\"", html);
        Assert.Contains("rel=\"noopener noreferrer nofollow\"", html);
    }

    [Fact]
    public void Images_resolve_cid_and_never_load_remote_sources()
    {
        Assert.Contains("src=\"/api/files/1/content\"", Clean("<img src=\"cid:logo\">"));
        Assert.DoesNotContain("src=", Clean("<img src=\"cid:unknown\">"));
        Assert.DoesNotContain("src=", Clean("<img src=\"https://tracker.test/p.gif\">"));
        Assert.Contains("src=\"data:image/png;base64,AAAA\"", Clean("<img src=\"data:image/png;base64,AAAA\">"));
        Assert.DoesNotContain("src=", Clean("<img src=\"data:image/svg+xml;base64,AAAA\">"));
    }

    [Theory]
    [InlineData("background:url(https://tracker.test/x)")]
    [InlineData("width:expression(alert(1))")]
    [InlineData("position:fixed;top:0")]
    [InlineData("behavior:url(x.htc)")]
    public void Dangerous_styles_are_dropped(string style) =>
        Assert.DoesNotContain("style", Clean($"<p style=\"{style}\">a</p>"));

    [Fact]
    public void Text_is_reencoded_and_unclosed_tags_are_closed()
    {
        Assert.Equal("&lt;script&gt;", Clean("&lt;script&gt;"));
        Assert.Equal("<b><i>x</i></b>", Clean("<b><i>x"));
    }
}

public sealed class MailNamesTests
{
    [Theory]
    [InlineData("a/b\\c.pdf", "a-b-c.pdf")]
    [InlineData("..", "allegato-0")]
    [InlineData("  ", "allegato-0")]
    [InlineData("name.\u0001txt", "name. txt")]
    [InlineData("trailing. . ", "trailing")]
    public void Foreign_names_become_safe_segments(string raw, string expected) =>
        Assert.Equal(expected, MailNames.AttachmentFileName(raw, null, "0"));

    [Fact]
    public void Falls_back_to_the_display_name()
    {
        Assert.Equal("Documento.docx", MailNames.AttachmentFileName(null, "Documento.docx", "1"));
    }

    [Fact]
    public void Long_names_keep_their_extension_and_never_split_a_surrogate_pair()
    {
        var name = new string('a', 250) + "😀😀😀😀.pdf";
        var safe = MailNames.Segment(name, "x");
        Assert.True(safe.Length <= 255);
        Assert.EndsWith(".pdf", safe);
        Assert.False(char.IsHighSurrogate(safe[^5]));
    }
}
