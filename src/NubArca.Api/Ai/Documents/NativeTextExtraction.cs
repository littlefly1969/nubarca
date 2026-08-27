using System.Text;

namespace NubArca.Api.Ai.Documents;

/// Sanitized reasons a document's text could not be extracted.
///
/// Every value is a TOKEN. None of them carries a filename, a storage key, a
/// byte offset or an exception message, because these travel into
/// `DocumentText.ErrorCode` and from there into operator diagnostics.
public static class DocumentExtractionReasons
{
    /// The MIME type is not one NubArca reads as native text. Not a failure —
    /// a photo library is mostly photos.
    public const string UnsupportedContentType = "unsupported-content-type";

    /// The bytes are not text. A NUL in the first block is the same test
    /// `git diff` uses, and it is the right one.
    public const string Binary = "binary";

    /// Declared as text and not decodable as UTF-8. Refused rather than
    /// decoded with replacement characters: a document full of `�` is
    /// indexable nonsense, and nonsense in a corpus is worse than a gap.
    public const string MalformedEncoding = "malformed-encoding";

    /// Larger than the extraction ceiling. Checked against the RECORDED size
    /// before the bytes are read, so this is a bound and not a report.
    public const string TooLarge = "too-large";

    /// Decodes to nothing worth retrieving.
    public const string Empty = "empty";

    // ---- rich ingestion ------------------------------------------------------
    //
    // All permanent CONTENT verdicts: the same bytes under the same profile earn
    // the same answer next week, so recording them against the blob is honest
    // and saves reading them again. Environment failures — a missing OCR binary,
    // a renderer that will not load — are counted elsewhere and never written
    // here, because a temporarily absent dependency must not mark somebody's
    // document permanently unreadable.

    /// The bytes are not a format NubArca reads. Distinct from
    /// UnsupportedContentType, which is a verdict about the DECLARED type: this
    /// one survived the declaration and failed on evidence.
    public const string UnsupportedDocumentFormat = "unsupported-document-format";

    /// A `.docm`/`.xlsm`/`.pptm` package. Refused as a format rather than
    /// parsed-with-macros-ignored: the safe subset of a macro-enabled document
    /// is a judgement this slice does not make, and refusing is a sentence an
    /// operator can read.
    public const string MacroEnabledOffice = "macro-enabled-office";

    /// Legacy binary Office — `.doc`, `.xls`, `.ppt`. An OLE compound file, a
    /// completely different format from OOXML, and out of scope.
    public const string LegacyOfficeFormat = "legacy-office-format";

    /// Password-protected. An encrypted OOXML package is a compound file
    /// wrapping the real one, so it never looks like a readable document; there
    /// is nothing to extract without a credential NubArca does not have and
    /// must not ask for.
    public const string EncryptedDocument = "encrypted-document";

    /// Claims to be an Office package and is not one: no content types part, no
    /// relationships, or a main part that does not match what the extension
    /// promised.
    public const string OfficePackageInvalid = "office-package-invalid";

    /// The package's total UNCOMPRESSED size is past the bound. This is the
    /// compression-bomb refusal, and it is decided from the archive directory
    /// before a single entry is decompressed.
    public const string OfficePackageTooLarge = "office-package-too-large";

    /// One entry inside the package is past the per-part bound.
    public const string OfficeEntryTooLarge = "office-entry-too-large";

    /// More entries than the bound allows.
    public const string OfficeTooManyEntries = "office-too-many-entries";
}

/// Bounds on native text extraction.
///
/// Configuration may make a bound TIGHTER and cannot remove one — every
/// accessor is clamped. An unbounded extraction is not a tuning option: it is a
/// way for one file in a library to consume the host.
public sealed class DocumentExtractionOptions
{
    public const string SectionName = "Ai:DocumentExtraction";

    /// Ceiling on the SOURCE bytes, checked against `FileItem.SizeBytes` before
    /// the blob is opened.
    public int MaxSourceBytes { get; set; } = 4 * 1024 * 1024;

    /// Ceiling on the extracted characters. Separate from the byte ceiling
    /// because they are not the same limit: UTF-8 makes them differ by up to
    /// four, and the chunker and the embedder care about characters.
    public int MaxCharacters { get; set; } = 1_000_000;

    public int MaxChunks { get; set; } = 4_000;

    public int MaxChunkCharacters { get; set; } = 1_600;

    /// Below this, a document has nothing to retrieve. A three-word note is not
    /// knowledge, and indexing it only adds a near-empty vector.
    public int MinimumCharacters { get; set; } = 20;

    public int EffectiveMaxSourceBytes => Math.Clamp(MaxSourceBytes, 1, 32 * 1024 * 1024);
    public int EffectiveMaxCharacters => Math.Clamp(MaxCharacters, 1, 8_000_000);
    public int EffectiveMaxChunks => Math.Clamp(MaxChunks, 1, 50_000);
    public int EffectiveMaxChunkCharacters => Math.Clamp(MaxChunkCharacters, 200, 8_000);
    // ---- rich document source bounds ----------------------------------------
    //
    // Per format, because 4 MiB is the right ceiling for a text file and the
    // wrong one for an Office package: a `.docx` carries images that contribute
    // nothing to extraction and still count against the source size, so one
    // universal limit either refuses ordinary documents or lets a text file be
    // enormous. Every one of these is clamped; none of them has a value meaning
    // "unlimited".

    /// Ceiling on the SOURCE bytes of a PDF.
    public int MaxPdfSourceBytes { get; set; } = 64 * 1024 * 1024;

    /// Ceiling on the SOURCE bytes of an Office Open XML package.
    public int MaxOfficeSourceBytes { get; set; } = 64 * 1024 * 1024;

    /// How many entries an Office package may contain.
    public int MaxOfficeEntries { get; set; } = 2_000;

    /// Total UNCOMPRESSED bytes the package may expand to. The bomb bound: it is
    /// read from the archive directory, so it is decided before anything is
    /// decompressed.
    public int MaxOfficeUncompressedBytes { get; set; } = 256 * 1024 * 1024;

    /// Ceiling on any single part inside the package.
    public int MaxOfficePartBytes { get; set; } = 64 * 1024 * 1024;

    public int EffectiveMinimumCharacters => Math.Clamp(MinimumCharacters, 1, 10_000);

    /// The hard ceiling no configuration can exceed, for every source-byte
    /// bound. A limit an operator can raise without end is not a limit.
    public const int AbsoluteMaxSourceBytes = 256 * 1024 * 1024;

    public int EffectiveMaxPdfSourceBytes
        => Math.Clamp(MaxPdfSourceBytes, 1, AbsoluteMaxSourceBytes);

    public int EffectiveMaxOfficeSourceBytes
        => Math.Clamp(MaxOfficeSourceBytes, 1, AbsoluteMaxSourceBytes);

    public int EffectiveMaxOfficeEntries => Math.Clamp(MaxOfficeEntries, 1, 50_000);

    public int EffectiveMaxOfficeUncompressedBytes
        => Math.Clamp(MaxOfficeUncompressedBytes, 1, 1024 * 1024 * 1024);

    public int EffectiveMaxOfficePartBytes
        => Math.Clamp(MaxOfficePartBytes, 1, AbsoluteMaxSourceBytes);

    /// The source ceiling that applies to ONE format. Callers ask this rather
    /// than picking a field, so a new format cannot quietly inherit the text
    /// limit by being forgotten in a switch.
    public int SourceBytesFor(DocumentFormatKind format) => format switch
    {
        DocumentFormatKind.Pdf => EffectiveMaxPdfSourceBytes,
        DocumentFormatKind.WordOpenXml
            or DocumentFormatKind.SpreadsheetOpenXml
            or DocumentFormatKind.PresentationOpenXml => EffectiveMaxOfficeSourceBytes,
        _ => EffectiveMaxSourceBytes,
    };
}

/// Extracted text, or a sanitized reason there is none.
public sealed record DocumentExtractionResult(string? Text, string? Reason, string? Language)
{
    public static DocumentExtractionResult Rejected(string reason) => new(null, reason, null);

    public bool Ok => Text is not null;
}

/// Turns a document's BYTES into text, locally and under a bound.
///
/// Native text only. No PDF, no OCR, no Office parsing, nothing that shells out
/// and nothing that reaches the network — the failure modes of a document parser
/// are memory and code execution, and the way to not have them is to not have a
/// parser. What is here is a decoder and a set of refusals.
///
/// THE EXTENSION IS NOT TRUSTED. A MIME type says what a file claims to be, and
/// the bytes say what it is: a `.txt` full of NULs is binary whatever the
/// content type says, and it is refused on the strength of its content rather
/// than its name. The declared type is used only to decide whether to LOOK,
/// which is the direction that fails closed.
public static class NativeTextExtractor
{
    /// The content types read as native text.
    ///
    /// An allowlist, not a `text/*` prefix test. `text/*` would sweep in every
    /// future type somebody registers under it, and the point of this list is
    /// that adding a format is a decision.
    private static readonly HashSet<string> Supported = new(StringComparer.OrdinalIgnoreCase)
    {
        "text/plain",
        "text/markdown",
        "text/x-markdown",
        "text/csv",
        "text/tab-separated-values",
        "application/json",
        "application/xml",
        "text/xml",
        "application/yaml",
        "text/yaml",
    };

    public static bool IsSupportedContentType(string? mimeType)
        => !string.IsNullOrWhiteSpace(mimeType) && Supported.Contains(Normalize(mimeType));

    /// `text/plain; charset=utf-8` is `text/plain`. The parameter is a claim
    /// about encoding, and the decoder below does not take claims about
    /// encoding.
    private static string Normalize(string mimeType)
    {
        var semicolon = mimeType.IndexOf(';');
        return (semicolon < 0 ? mimeType : mimeType[..semicolon]).Trim();
    }

    /// A NUL byte anywhere in the first block. Every binary format puts one
    /// there and no text encoding NubArca stores puts one in the middle of a
    /// document.
    public static bool LooksBinary(ReadOnlySpan<byte> bytes)
    {
        var window = bytes.Length < 8000 ? bytes : bytes[..8000];
        foreach (var b in window)
        {
            if (b == 0) return true;
        }
        return false;
    }

    /// Decode, refuse, or bound. `bytes` has already passed the SIZE gate — the
    /// caller checks the recorded size before opening anything, so this method
    /// never sees a file it was going to reject for being large.
    public static DocumentExtractionResult Extract(
        string mimeType, ReadOnlySpan<byte> bytes, DocumentExtractionOptions options)
    {
        if (!IsSupportedContentType(mimeType))
        {
            return DocumentExtractionResult.Rejected(DocumentExtractionReasons.UnsupportedContentType);
        }
        if (bytes.Length > options.EffectiveMaxSourceBytes)
        {
            return DocumentExtractionResult.Rejected(DocumentExtractionReasons.TooLarge);
        }
        if (bytes.Length == 0)
        {
            return DocumentExtractionResult.Rejected(DocumentExtractionReasons.Empty);
        }
        if (LooksBinary(bytes))
        {
            return DocumentExtractionResult.Rejected(DocumentExtractionReasons.Binary);
        }

        // STRICT UTF-8. `Encoding.UTF8` is lenient by default and substitutes
        // U+FFFD for anything it cannot decode, which would turn a Latin-1 file
        // or a truncated multi-byte sequence into a document full of replacement
        // characters that indexes, embeds and retrieves as gibberish. Throwing
        // and reporting `malformed-encoding` is the honest answer.
        string text;
        try
        {
            var strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            text = strict.GetString(StripBom(bytes));
        }
        catch (DecoderFallbackException)
        {
            return DocumentExtractionResult.Rejected(DocumentExtractionReasons.MalformedEncoding);
        }

        // Normalize line endings so the same document stored from Windows and
        // from Linux hashes and chunks identically. A CRLF/LF difference is not
        // a content change.
        text = text.Replace("\r\n", "\n", StringComparison.Ordinal)
                   .Replace('\r', '\n');

        if (text.Length > options.EffectiveMaxCharacters)
        {
            text = text[..options.EffectiveMaxCharacters];
        }
        if (text.Trim().Length < options.EffectiveMinimumCharacters)
        {
            return DocumentExtractionResult.Rejected(DocumentExtractionReasons.Empty);
        }

        return new DocumentExtractionResult(text, null, null);
    }

    /// A UTF-8 BOM is a byte-order mark for an encoding that has no byte order.
    /// Left in place it becomes the first character of the first chunk and of
    /// the text hash, so the same document saved by two editors would not
    /// deduplicate.
    private static ReadOnlySpan<byte> StripBom(ReadOnlySpan<byte> bytes)
        => bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF
            ? bytes[3..]
            : bytes;
}
