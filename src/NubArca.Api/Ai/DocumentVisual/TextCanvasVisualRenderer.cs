using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using NubArca.Api.Ai.Documents;
using SkiaSharp;

namespace NubArca.Api.Ai.DocumentVisual;

/// NubArca's own page, drawn for a document that has none.
///
/// A Markdown note or a plain-text file has no pages, no layout and no visual
/// identity — so there is nothing to photograph, and the obvious conclusion is
/// that visual retrieval simply skips them. This renderer disagrees, for one
/// reason: a heading hierarchy, a table, an indented block and a fenced code
/// listing ARE visual structure, and they are structure a text embedding
/// flattens away. Drawing them and embedding the picture is what lets "the
/// document that was mostly a table of settings" be a findable thing.
///
/// DETERMINISTIC, AND THAT IS THE HARD PART. The same bytes must produce the
/// same pixels on the same installation, run after run, or `PixelHash` means
/// nothing and a rebuild silently re-embeds identical content. So: a font
/// resolved by exact family name and REFUSED if the host substituted something
/// else, fixed page dimensions, fixed margins, integer line advances, and
/// wrapping that measures rather than estimates.
///
/// NO BROWSER, and no font download. A headless browser would render beautifully
/// and would be a network-capable HTML engine executing owner content inside the
/// API — the precise thing the Office renderer is isolated into another
/// container to avoid.
public sealed class TextCanvasVisualRenderer : IDocumentVisualRenderer
{
    /// The body and code faces, BY FAMILY NAME, loaded from a font FILE.
    ///
    /// NubArca's Skia ships as `SkiaSharp.NativeAssets.Linux.NoDependencies`,
    /// which is built without fontconfig — so on Linux `SKFontManager.Default`
    /// has no system fonts at all and `MatchFamily` returns nothing for
    /// everything. That is a feature here rather than an obstacle: it means the
    /// only face this renderer can possibly draw with is one it opened
    /// deliberately, and there is no path by which a host's font configuration
    /// silently changes every pixel NubArca produces.
    private const string BodyFamily = "DejaVu Sans";
    private const string MonoFamily = "DejaVu Sans Mono";

    private const float Margin = 64f;
    private const float BodySize = 20f;
    private const float LineGap = 8f;

    /// WHERE THE FONT FILE MAY BE, as a closed list of the standard locations
    /// the open DejaVu package installs into on the distributions NubArca runs
    /// on. The API image installs `fonts-dejavu-core`, which is the first entry.
    ///
    /// Several paths and not one, because "the font lives here" is a packaging
    /// fact that differs between Debian, Fedora and Arch and is not something an
    /// operator should have to configure. None of these is an installation
    /// identity: they describe the product's dependency, not this deployment.
    ///
    /// `Ai:DocumentVisual:TextCanvasFontDir` overrides the search for a host
    /// that keeps its fonts elsewhere. It cannot change WHICH font is used — the
    /// family name is verified after loading — so the render profile key stays a
    /// true statement about the pixels no matter where the file came from.
    private static readonly string[] FontDirectories =
    {
        "/usr/share/fonts/truetype/dejavu",
        "/usr/share/fonts/dejavu",
        "/usr/share/fonts/TTF",
        "/usr/share/fonts/dejavu-sans-fonts",
        "/usr/local/share/fonts/dejavu",
    };

    private static readonly IReadOnlyDictionary<string, string> FontFiles =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [BodyFamily] = "DejaVuSans.ttf",
            [MonoFamily] = "DejaVuSansMono.ttf",
        };

    private readonly IOptions<DocumentVisualOptions> _options;

    public TextCanvasVisualRenderer(IOptions<DocumentVisualOptions> options)
    {
        _options = options;
    }

    public string RenderProfileKey => DocumentVisualRenderProfiles.TextCanvas;

    public IReadOnlyCollection<DocumentFormatKind> Formats { get; }
        = new[] { DocumentFormatKind.NativeText };

    public DocumentVisualRendererReadiness CheckReadiness()
    {
        using var body = ResolveTypeface(BodyFamily);
        using var mono = ResolveTypeface(MonoFamily);

        return body is null || mono is null
            ? DocumentVisualRendererReadiness.NotReady(DocumentVisualReasons.RendererUnavailable)
            : DocumentVisualRendererReadiness.Available;
    }

    /// EXACT FAMILY OR NOTHING.
    ///
    /// The file is opened by name from a known directory, and then the loaded
    /// face is asked what it actually IS. A `DejaVuSans.ttf` that some packaging
    /// decision replaced with a different design would otherwise change every
    /// rendered page and every vector, while `nubarca-text-canvas-v1` went on
    /// claiming to describe them.
    private SKTypeface? ResolveTypeface(string family)
    {
        if (!FontFiles.TryGetValue(family, out var fileName)) return null;

        foreach (var directory in Directories())
        {
            var path = Path.Combine(directory, fileName);
            if (!File.Exists(path)) continue;

            SKTypeface? typeface;
            try
            {
                typeface = SKTypeface.FromFile(path);
            }
            catch (Exception)
            {
                continue;
            }

            if (typeface is null) continue;
            if (string.Equals(typeface.FamilyName, family, StringComparison.Ordinal))
            {
                return typeface;
            }

            typeface.Dispose();
        }

        return null;
    }

    private IEnumerable<string> Directories()
    {
        var configured = (_options.Value.TextCanvasFontDir ?? string.Empty).Trim();
        if (configured.Length > 0) yield return configured;
        foreach (var directory in FontDirectories) yield return directory;
    }

    public Task<DocumentVisualRenderOutcome> RenderAsync(
        DocumentVisualRenderRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Format != DocumentFormatKind.NativeText)
        {
            return Task.FromResult(
                DocumentVisualRenderOutcome.Rejected(DocumentVisualReasons.FormatUnsupported));
        }

        var readiness = CheckReadiness();
        if (!readiness.Ready)
        {
            return Task.FromResult(DocumentVisualRenderOutcome.Unavailable(
                readiness.Reason ?? DocumentVisualReasons.RendererUnavailable));
        }

        string text;
        try
        {
            // Strict UTF-8, the same decision native extraction makes: a
            // document full of replacement characters is indexable nonsense, and
            // nonsense in a corpus is worse than a gap.
            text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(request.Bytes.Span);
        }
        catch (DecoderFallbackException)
        {
            return Task.FromResult(
                DocumentVisualRenderOutcome.Rejected(DocumentVisualReasons.InvalidSource));
        }

        return Task.FromResult(Draw(text, request.Options, cancellationToken));
    }

    private DocumentVisualRenderOutcome Draw(
        string text, DocumentVisualOptions options, CancellationToken cancellationToken)
    {
        using var body = ResolveTypeface(BodyFamily)!;
        using var mono = ResolveTypeface(MonoFamily)!;

        var width = options.EffectiveTextCanvasWidth;
        var height = options.EffectiveTextCanvasHeight;
        var contentWidth = width - (2 * Margin);
        if (contentWidth <= 0)
        {
            return DocumentVisualRenderOutcome.Rejected(DocumentVisualReasons.OutputTooLarge);
        }

        var lines = LayOut(text, body, mono, contentWidth);
        if (lines.Count == 0)
        {
            return DocumentVisualRenderOutcome.Rejected(DocumentVisualReasons.InvalidSource);
        }

        // Pagination is computed BEFORE anything is drawn, so a document past the
        // unit bound is refused whole rather than rendered up to the bound.
        var usable = height - (2 * Margin);
        var pages = new List<List<LaidOutLine>>();
        var current = new List<LaidOutLine>();
        var y = 0f;
        foreach (var line in lines)
        {
            if (y + line.Advance > usable && current.Count > 0)
            {
                pages.Add(current);
                current = new List<LaidOutLine>();
                y = 0f;
            }
            current.Add(line);
            y += line.Advance;
        }
        if (current.Count > 0) pages.Add(current);

        if (pages.Count > options.EffectiveMaxVisualUnitsPerDocument)
        {
            return DocumentVisualRenderOutcome.Rejected(DocumentVisualReasons.DocumentTooComplex);
        }

        var pixelsPerPage = (long)width * height;
        if (pixelsPerPage > options.EffectiveMaxVisualPixelsPerUnit
            || pixelsPerPage * pages.Count > options.EffectiveMaxVisualTotalPixelsPerDocument)
        {
            return DocumentVisualRenderOutcome.Rejected(DocumentVisualReasons.DocumentTooComplex);
        }

        var units = new List<DocumentVisualUnitArtifact>(pages.Count);
        for (var index = 0; index < pages.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var png = DrawPage(pages[index], width, height, body, mono);
            if (png.Length > options.EffectiveMaxVisualImageBytesPerUnit)
            {
                return DocumentVisualRenderOutcome.Rejected(DocumentVisualReasons.OutputTooLarge);
            }

            units.Add(new DocumentVisualUnitArtifact(
                Ordinal: index,
                RenderKind: DocumentVisualRenderKinds.TextCanvasSheet,
                Png: png,
                Width: width,
                Height: height,
                // NO LOCATOR, and that is the honest value. A Markdown file has
                // no sheets; "sheet 3 of 5" describes this renderer's margins,
                // not the author's document, and would be a citation about
                // NubArca rather than about their notes.
                SourceLocator: null,
                SourcePage: null));
        }

        return DocumentVisualRenderOutcome.Rendered(
            new DocumentVisualRenderArtifact(RenderProfileKey, units));
    }

    private static byte[] DrawPage(
        IReadOnlyList<LaidOutLine> lines, int width, int height, SKTypeface body, SKTypeface mono)
    {
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.White);

        using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true };

        var y = Margin;
        foreach (var line in lines)
        {
            using var font = new SKFont(line.Mono ? mono : body, line.Size)
            {
                Embolden = line.Bold,
            };
            y += line.Advance;
            if (line.Text.Length > 0)
            {
                canvas.DrawText(line.Text, Margin + line.Indent, y - LineGap, SKTextAlign.Left, font, paint);
            }
        }

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// One drawn line and everything that decides its geometry.
    private readonly record struct LaidOutLine(
        string Text, float Size, bool Bold, bool Mono, float Indent, float Advance);

    /// Text into lines, measured rather than estimated.
    ///
    /// The structure this recognises is Markdown's, because Markdown is what a
    /// person's notes are actually written in and because its structure is
    /// visible in the plain text — no parser, no HTML, no execution. A `.txt`
    /// with no markers simply lays out as body text, which is the correct
    /// picture of a file that has no structure.
    private static List<LaidOutLine> LayOut(
        string text, SKTypeface body, SKTypeface mono, float contentWidth)
    {
        var result = new List<LaidOutLine>();
        var inFence = false;

        foreach (var raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = raw.TrimEnd();

            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                inFence = !inFence;
                result.Add(Blank(BodySize * 0.4f));
                continue;
            }

            if (inFence)
            {
                // Code keeps its own leading whitespace: indentation IS the
                // structure of a listing, and stripping it would draw a picture
                // of a different program.
                Wrap(result, line, mono, BodySize * 0.85f, bold: false, isMono: true, contentWidth);
                continue;
            }

            if (line.Length == 0)
            {
                result.Add(Blank(BodySize * 0.6f));
                continue;
            }

            var level = HeadingLevel(line);
            if (level > 0)
            {
                // HEADINGS ARE VISIBLY DIFFERENT, in size and weight, because
                // that difference is exactly what the visual encoder is being
                // asked to see. A document whose headings render at body size is
                // a document with no visible hierarchy to find.
                var size = BodySize * (level switch
                {
                    1 => 1.9f,
                    2 => 1.55f,
                    3 => 1.3f,
                    _ => 1.12f,
                });
                result.Add(Blank(size * 0.5f));
                Wrap(result, line[(level + 1)..].Trim(), body, size, bold: true, isMono: false, contentWidth);
                continue;
            }

            var indent = line.StartsWith("  ", StringComparison.Ordinal) ? 24f : 0f;
            Wrap(result, line.TrimStart(), body, BodySize, bold: false, isMono: false, contentWidth,
                indent);
        }

        return result;
    }

    private static LaidOutLine Blank(float advance)
        => new(string.Empty, BodySize, false, false, 0f, MathF.Round(advance));

    private static int HeadingLevel(string line)
    {
        var level = 0;
        while (level < line.Length && line[level] == '#') level++;
        // `#foo` is not a heading in Markdown; the space is what makes it one.
        return level is > 0 and <= 6 && level < line.Length && line[level] == ' ' ? level : 0;
    }

    /// Greedy word wrap, MEASURED. Estimating from an average character width
    /// would put a different number of words on a line depending on the words,
    /// which is a different picture of the same document.
    private static void Wrap(
        List<LaidOutLine> into, string text, SKTypeface typeface, float size,
        bool bold, bool isMono, float contentWidth, float indent = 0f)
    {
        using var font = new SKFont(typeface, size) { Embolden = bold };
        // Integer advances: a fractional line height accumulates differently
        // depending on where a page break lands, which would make the same
        // paragraph hash differently on page 2 than on page 3.
        var advance = MathF.Round(size + LineGap);
        var available = contentWidth - indent;

        if (text.Length == 0)
        {
            into.Add(new LaidOutLine(string.Empty, size, bold, isMono, indent, advance));
            return;
        }

        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            into.Add(new LaidOutLine(string.Empty, size, bold, isMono, indent, advance));
            return;
        }

        var current = new StringBuilder();
        foreach (var word in words)
        {
            var candidate = current.Length == 0 ? word : $"{current} {word}";
            if (font.MeasureText(candidate) <= available || current.Length == 0)
            {
                current.Clear();
                current.Append(candidate);
                // A single word wider than the line is broken by character
                // rather than allowed to run off the page — a rendered document
                // whose long identifiers vanish past the margin is a picture
                // missing the thing somebody is searching for.
                while (font.MeasureText(current.ToString()) > available && current.Length > 1)
                {
                    var kept = current.ToString();
                    var cut = kept.Length - 1;
                    while (cut > 1 && font.MeasureText(kept[..cut]) > available) cut--;
                    into.Add(new LaidOutLine(kept[..cut], size, bold, isMono, indent, advance));
                    current.Clear();
                    current.Append(kept[cut..]);
                }
                continue;
            }

            into.Add(new LaidOutLine(current.ToString(), size, bold, isMono, indent, advance));
            current.Clear();
            current.Append(word);
        }

        if (current.Length > 0)
        {
            into.Add(new LaidOutLine(current.ToString(), size, bold, isMono, indent, advance));
        }
    }

    /// SHA-256 of the rendered bytes, hex. The determinism proof that survives
    /// discarding the image.
    public static string PixelHash(ReadOnlySpan<byte> png)
        => Convert.ToHexString(SHA256.HashData(png)).ToLowerInvariant();
}
