using System.Text;
using NubArca.Api.Ai.Documents;
using Xunit;

namespace NubArca.Api.Tests.Ai.Documents;

// The decoder, and the refusals around it.
//
// This is the one place in the private pipeline that touches bytes a user
// supplied, so almost every test here is about saying NO: no binary, no
// mis-declared type, no lenient decoding, no unbounded read. The single
// affirmative case — UTF-8 text comes back as text — is the smallest part of
// the file on purpose.
public sealed class NativeTextExtractionTests
{
    private static readonly DocumentExtractionOptions Options = new();

    [Fact]
    public void Utf8PlainText_IsExtracted()
    {
        var result = Extract("text/plain", "Il manuale della caldaia.\nIl filtro va pulito ogni sei mesi.\n");

        Assert.True(result.Ok);
        Assert.Contains("ogni sei mesi", result.Text!, StringComparison.Ordinal);
    }

    [Fact]
    public void Markdown_IsExtracted()
    {
        var result = Extract("text/markdown", "# Manuale\n\n## Pulizia filtro\n\nOgni sei mesi.\n");

        Assert.True(result.Ok);
        Assert.Contains("Pulizia filtro", result.Text!, StringComparison.Ordinal);
    }

    [Fact]
    public void Accented_And_NonLatin_Text_Survives_Byte_For_Byte()
    {
        // The interface is Italian and a library is whatever its owner writes
        // in. A decoder that mangles this silently produces a corpus that
        // retrieves badly and looks fine.
        const string text = "Manutenzione periodica: però è necessario. Ελληνικά. 日本語. Ćao.";
        var result = Extract("text/plain", text);

        Assert.True(result.Ok);
        Assert.Equal(text, result.Text);
    }

    // ---- refusals -----------------------------------------------------------

    [Fact]
    public void Binary_IsRejected()
    {
        // Declared as text, and it is not. The bytes decide.
        var bytes = new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x00, 0x00, 0x08, 0x00 };
        var result = NativeTextExtractor.Extract("text/plain", bytes, Options);

        Assert.False(result.Ok);
        Assert.Equal(DocumentExtractionReasons.Binary, result.Reason);
    }

    [Fact]
    public void MalformedUtf8_IsRejected()
    {
        // A truncated multi-byte sequence, or a Latin-1 file mislabelled as
        // UTF-8. Lenient decoding turns both into a document full of U+FFFD that
        // indexes, embeds and retrieves as gibberish — so it is refused instead.
        var bytes = new byte[] { (byte)'c', (byte)'i', (byte)'a', (byte)'o', 0xC3 };
        var result = NativeTextExtractor.Extract("text/plain", bytes, Options);

        Assert.False(result.Ok);
        Assert.Equal(DocumentExtractionReasons.MalformedEncoding, result.Reason);
    }

    [Fact]
    public void Latin1_MislabelledAsUtf8_IsRejected()
    {
        // The realistic version of the test above: an old file saved as
        // ISO-8859-1 whose MIME type says UTF-8.
        var bytes = Encoding.Latin1.GetBytes(
            "Il filtro è sporco e va pulito periodicamente secondo il manuale.");
        var result = NativeTextExtractor.Extract("text/plain; charset=utf-8", bytes, Options);

        Assert.False(result.Ok);
        Assert.Equal(DocumentExtractionReasons.MalformedEncoding, result.Reason);
    }

    [Fact]
    public void UnsupportedMime_IsSkipped()
    {
        // A photo library is mostly photos. Not reading them is normal, and it
        // is decided from the DECLARED type — the one direction where trusting
        // the declaration fails closed, because a mislabelled text file simply
        // does not get indexed.
        foreach (var mime in new[] { "image/jpeg", "application/pdf", "video/mp4", "" })
        {
            var result = Extract(mime, "Questo è testo perfettamente leggibile ma non richiesto.");
            Assert.False(result.Ok);
            Assert.Equal(DocumentExtractionReasons.UnsupportedContentType, result.Reason);
        }
    }

    [Fact]
    public void OversizedContent_IsRejected()
    {
        var options = new DocumentExtractionOptions { MaxSourceBytes = 64 };
        var bytes = Encoding.UTF8.GetBytes(new string('a', 200));

        var result = NativeTextExtractor.Extract("text/plain", bytes, options);

        Assert.False(result.Ok);
        Assert.Equal(DocumentExtractionReasons.TooLarge, result.Reason);
    }

    [Fact]
    public void EmptyAndNearEmptyDocuments_AreRejected()
    {
        Assert.Equal(
            DocumentExtractionReasons.Empty,
            NativeTextExtractor.Extract("text/plain", Array.Empty<byte>(), Options).Reason);

        // Three words is not knowledge. Indexing it adds a near-empty vector
        // that competes with real content.
        Assert.Equal(DocumentExtractionReasons.Empty, Extract("text/plain", "ciao\n").Reason);
    }

    // ---- normalization ------------------------------------------------------

    [Fact]
    public void Extraction_IsIdempotent_ForTheSameBytes()
    {
        var bytes = Encoding.UTF8.GetBytes("# Note\n\nUn documento con del contenuto sufficiente.\n");

        var first = NativeTextExtractor.Extract("text/markdown", bytes, Options);
        var second = NativeTextExtractor.Extract("text/markdown", bytes, Options);

        Assert.Equal(first.Text, second.Text);
    }

    [Fact]
    public void CrlfAndLf_ProduceIdenticalText()
    {
        // The same document saved on Windows and on Linux must extract, hash and
        // chunk identically — a line-ending difference is not a content change,
        // and treating it as one would re-embed a file for being opened.
        var lf = Extract("text/plain", "Prima riga del documento.\nSeconda riga del documento.\n");
        var crlf = Extract("text/plain", "Prima riga del documento.\r\nSeconda riga del documento.\r\n");

        Assert.True(lf.Ok);
        Assert.Equal(lf.Text, crlf.Text);
    }

    [Fact]
    public void A_Utf8_Bom_Is_Not_Part_Of_The_Document()
    {
        // Left in place the BOM becomes the first character of the first chunk
        // and of the text hash, so the same file saved by two editors would not
        // deduplicate.
        var withBom = new byte[] { 0xEF, 0xBB, 0xBF }
            .Concat(Encoding.UTF8.GetBytes("Manuale della caldaia con istruzioni."))
            .ToArray();

        var result = NativeTextExtractor.Extract("text/markdown", withBom, Options);

        Assert.True(result.Ok);
        Assert.StartsWith("Manuale", result.Text!, StringComparison.Ordinal);
        Assert.Equal(Extract("text/markdown", "Manuale della caldaia con istruzioni.").Text, result.Text);
    }

    [Fact]
    public void ContentTypeParameters_DoNotChangeTheAnswer()
    {
        Assert.True(Extract("text/plain; charset=utf-8", Body).Ok);
        Assert.True(Extract("  text/markdown  ", Body).Ok);
        Assert.True(Extract("TEXT/PLAIN", Body).Ok);
    }

    [Fact]
    public void ExtractedCharacters_AreBounded()
    {
        var options = new DocumentExtractionOptions { MaxCharacters = 50 };
        var result = NativeTextExtractor.Extract(
            "text/plain", Encoding.UTF8.GetBytes(new string('a', 5000)), options);

        Assert.True(result.Ok);
        Assert.Equal(50, result.Text!.Length);
    }

    [Fact]
    public void No_Bound_Can_Be_Configured_Away()
    {
        // Configuration may make a bound tighter and cannot remove one. Zero and
        // negative are clamped up, absurd values clamped down.
        var zeroed = new DocumentExtractionOptions
        {
            MaxSourceBytes = 0,
            MaxCharacters = -1,
            MaxChunks = 0,
            MaxChunkCharacters = 0,
            MinimumCharacters = 0,
        };
        Assert.True(zeroed.EffectiveMaxSourceBytes >= 1);
        Assert.True(zeroed.EffectiveMaxCharacters >= 1);
        Assert.True(zeroed.EffectiveMaxChunks >= 1);
        Assert.True(zeroed.EffectiveMaxChunkCharacters >= 200);
        Assert.True(zeroed.EffectiveMinimumCharacters >= 1);

        var absurd = new DocumentExtractionOptions
        {
            MaxSourceBytes = int.MaxValue,
            MaxCharacters = int.MaxValue,
            MaxChunks = int.MaxValue,
            MaxChunkCharacters = int.MaxValue,
        };
        Assert.True(absurd.EffectiveMaxSourceBytes <= 32 * 1024 * 1024);
        Assert.True(absurd.EffectiveMaxCharacters <= 8_000_000);
        Assert.True(absurd.EffectiveMaxChunks <= 50_000);
        Assert.True(absurd.EffectiveMaxChunkCharacters <= 8_000);
    }

    [Fact]
    public void The_Eligibility_Allowlist_And_The_Extractor_Agree()
    {
        // Two lists that must match are two lists that will not, unless
        // something compares them. The query-side list decides which files are
        // even considered; the extractor decides what it will read. A type in
        // one and not the other is either a file fetched and thrown away, or a
        // supported format nothing ever offers.
        Assert.All(
            OwnerDocumentEligibility.DeclaredContentTypes,
            mime => Assert.True(
                NativeTextExtractor.IsSupportedContentType(mime),
                $"'{mime}' is queried for but the extractor refuses it."));
    }

    private const string Body = "Un documento con abbastanza contenuto da essere indicizzato.";

    private static DocumentExtractionResult Extract(string mime, string text)
        => NativeTextExtractor.Extract(mime, Encoding.UTF8.GetBytes(text), Options);
}
