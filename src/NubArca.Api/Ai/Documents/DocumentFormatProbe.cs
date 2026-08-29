using System.IO.Compression;
using System.Text;

namespace NubArca.Api.Ai.Documents;

/// The families of document NubArca can compile into owner-private text.
public enum DocumentFormatKind
{
    NativeText,
    Pdf,
    WordOpenXml,
    SpreadsheetOpenXml,
    PresentationOpenXml,
}

/// What the bytes turned out to be, or the sanitized reason they are not
/// something this installation reads.
public sealed record DocumentProbeResult(
    DocumentFormatKind? Format,
    string? CanonicalMimeType,
    string? Reason)
{
    public static DocumentProbeResult Refused(string reason) => new(null, null, reason);

    public static DocumentProbeResult Accepted(DocumentFormatKind format, string mimeType)
        => new(format, mimeType, null);

    public bool Ok => Format is not null;
}

/// WHAT IS THIS FILE, decided from its bytes.
///
/// The declared MIME type and the extension are used for exactly one thing:
/// deciding whether opening the file is worth doing at all. They never decide
/// what it IS. That direction matters because both are attacker-controlled and
/// both are routinely wrong by accident — clients upload OOXML packages as
/// `application/octet-stream` all the time, and a `.pdf` full of arbitrary bytes
/// is a file somebody named optimistically.
///
/// The consequence is worth stating plainly: a DOCX renamed `.xlsx` must not
/// reach the spreadsheet parser. Handing a document to the wrong parser is not a
/// cosmetic mistake — it is untrusted input arriving somewhere that was written
/// assuming a different structure, which is where parser bugs become
/// interesting. So acceptance requires the package to say what it is, in its own
/// content types part, and to agree.
///
/// The probe reads structure only. It extracts nothing.
public static class DocumentFormatProbe
{
    // ---- signatures ---------------------------------------------------------

    private static readonly byte[] PdfSignature = "%PDF-"u8.ToArray();

    /// PKZip local file header. Every OOXML package is a ZIP.
    private static readonly byte[] ZipSignature = { 0x50, 0x4B, 0x03, 0x04 };

    /// OLE2 / Compound File Binary — legacy binary Office, and also the wrapper
    /// an ENCRYPTED OOXML document is stored in. Two very different refusals
    /// that share a first eight bytes.
    private static readonly byte[] CompoundFileSignature =
        { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 };

    // ---- OOXML part names and content types ---------------------------------

    private const string ContentTypesPart = "[Content_Types].xml";
    private const string RelationshipsPart = "_rels/.rels";

    public const string WordMimeType =
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
    public const string SpreadsheetMimeType =
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    public const string PresentationMimeType =
        "application/vnd.openxmlformats-officedocument.presentationml.presentation";
    public const string PdfMimeType = "application/pdf";

    /// The MAIN PART content type each package family declares. This is the
    /// package saying what it is, which is the only statement about format worth
    /// trusting.
    private const string WordMainPart =
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml";
    private const string SpreadsheetMainPart =
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml";
    private const string PresentationMainPart =
        "application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml";

    /// The macro-enabled counterparts. Recognised precisely so they can be
    /// refused by name rather than falling through to "not a document".
    private const string WordMacroMainPart =
        "application/vnd.ms-word.document.macroEnabled.main+xml";
    private const string SpreadsheetMacroMainPart =
        "application/vnd.ms-excel.sheet.macroEnabled.main+xml";
    private const string PresentationMacroMainPart =
        "application/vnd.ms-powerpoint.presentation.macroEnabled.main+xml";

    /// Declared types worth opening the file for.
    ///
    /// Not a claim about what the file is — a gate on whether a bounded probe is
    /// justified. A photo library is mostly photos, and reading every one of
    /// them to discover it is a JPEG would be the expensive way to learn
    /// nothing.
    public static readonly IReadOnlyList<string> CandidateMimeTypes = new[]
    {
        PdfMimeType,
        WordMimeType,
        SpreadsheetMimeType,
        PresentationMimeType,
    };

    /// Types that say nothing useful, and are therefore accepted as candidates
    /// ONLY when the filename carries a supported rich extension.
    ///
    /// Plenty of clients upload an OOXML package as `application/octet-stream`
    /// or `application/zip`, so refusing those outright would make rich
    /// ingestion depend on which uploader somebody happened to use. The
    /// extension is not evidence and never becomes evidence: it buys a bounded
    /// look at the bytes, and the bytes decide.
    public static readonly IReadOnlyList<string> GenericMimeTypes = new[]
    {
        "application/octet-stream",
        "application/zip",
        "application/x-zip-compressed",
    };

    public static readonly IReadOnlyList<string> RichExtensions = new[]
    {
        ".pdf", ".docx", ".xlsx", ".pptx",
    };

    /// Is this file worth opening? Declared type first, then — for the
    /// uninformative types — the extension.
    public static bool IsCandidate(string? mimeType, string? fileName)
    {
        var mime = Canonical(mimeType);
        if (CandidateMimeTypes.Contains(mime, StringComparer.Ordinal)) return true;
        if (!GenericMimeTypes.Contains(mime, StringComparer.Ordinal)) return false;

        return fileName is not null
               && RichExtensions.Any(e => fileName.EndsWith(e, StringComparison.OrdinalIgnoreCase));
    }

    /// How many SOURCE BYTES it is worth reading before the bytes are probed.
    ///
    /// A RESOURCE BUDGET, and deliberately nothing else. It confers no format
    /// authority: the bytes still decide what the file is, and the real
    /// per-format ceiling is re-checked afterwards against
    /// `DocumentExtractionOptions.SourceBytesFor(actualFormat)`.
    ///
    /// It exists because the read has to be bounded BEFORE anything is known
    /// about the content, and the only bound available at that moment used to be
    /// the native-text ceiling. Deriving the budget from the candidate makes the
    /// PDF and Office ceilings reachable at all: with one 4 MiB budget for
    /// everything, a 10 MiB PDF was refused before it was ever opened, and the
    /// 64 MiB limits the operator can configure could never be exercised.
    ///
    /// A wrong guess is safe in both directions. Too generous, and the bytes
    /// turn out to be something with a smaller ceiling — the post-probe check
    /// refuses it. Too small, and the read stops short — which the caller must
    /// treat as a refusal rather than parse, because a truncated buffer is
    /// exactly the partial document this whole gate exists to prevent.
    public static int CandidateSourceBudget(
        string? mimeType, string? fileName, DocumentExtractionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var mime = Canonical(mimeType);

        if (string.Equals(mime, PdfMimeType, StringComparison.Ordinal))
        {
            return options.EffectiveMaxPdfSourceBytes;
        }

        if (string.Equals(mime, WordMimeType, StringComparison.Ordinal)
            || string.Equals(mime, SpreadsheetMimeType, StringComparison.Ordinal)
            || string.Equals(mime, PresentationMimeType, StringComparison.Ordinal))
        {
            return options.EffectiveMaxOfficeSourceBytes;
        }

        // An uninformative declared type buys a look at the bytes only when the
        // NAME suggests a rich document — the same rule IsCandidate applies, and
        // for the same reason: plenty of clients upload an OOXML package as
        // `application/octet-stream`, and the extension buys the read without
        // ever becoming evidence of what the file is.
        if (GenericMimeTypes.Contains(mime, StringComparer.Ordinal) && fileName is not null)
        {
            if (fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                return options.EffectiveMaxPdfSourceBytes;
            }

            if (fileName.EndsWith(".docx", StringComparison.OrdinalIgnoreCase)
                || fileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase)
                || fileName.EndsWith(".pptx", StringComparison.OrdinalIgnoreCase))
            {
                return options.EffectiveMaxOfficeSourceBytes;
            }
        }

        // Everything else, native text included, keeps the text ceiling.
        return options.EffectiveMaxSourceBytes;
    }

    /// What the bytes are.
    ///
    /// `fileName` is passed for one narrow purpose — telling the three OOXML
    /// families apart is done from the package, but a package whose main part
    /// disagrees with the extension is refused rather than trusted in either
    /// direction. It is never the thing that decides.
    public static DocumentProbeResult Probe(
        ReadOnlySpan<byte> bytes, string? fileName, DocumentExtractionOptions options)
    {
        if (bytes.Length == 0) return DocumentProbeResult.Refused(DocumentExtractionReasons.Empty);

        // LEGACY AND ENCRYPTED FIRST. Both are compound files, and both would
        // otherwise fall through to "unsupported" — which is true but useless to
        // an operator wondering why one document out of forty is missing.
        if (StartsWith(bytes, CompoundFileSignature))
        {
            return DocumentProbeResult.Refused(
                LooksEncryptedCompoundFile(bytes)
                    ? DocumentExtractionReasons.EncryptedDocument
                    : DocumentExtractionReasons.LegacyOfficeFormat);
        }

        if (StartsWith(bytes, PdfSignature))
        {
            return DocumentProbeResult.Accepted(DocumentFormatKind.Pdf, PdfMimeType);
        }

        if (StartsWith(bytes, ZipSignature))
        {
            return ProbeOpenXml(bytes, fileName, options);
        }

        return DocumentProbeResult.Refused(DocumentExtractionReasons.UnsupportedDocumentFormat);
    }

    // ---- OOXML --------------------------------------------------------------

    private static DocumentProbeResult ProbeOpenXml(
        ReadOnlySpan<byte> bytes, string? fileName, DocumentExtractionOptions options)
    {
        // The copy is deliberate: ZipArchive needs a seekable stream, and the
        // caller's buffer is already bounded by the source-size gate, so this
        // does not introduce an allocation the caller has not already paid for.
        using var stream = new MemoryStream(bytes.ToArray(), writable: false);

        ZipArchive archive;
        try
        {
            archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        }
        catch (InvalidDataException)
        {
            // A ZIP signature and no readable directory. Corrupt, truncated, or
            // something that merely starts with PK.
            return DocumentProbeResult.Refused(DocumentExtractionReasons.OfficePackageInvalid);
        }

        using (archive)
        {
            // PREFLIGHT BEFORE ANY ENTRY IS OPENED. The bounds are decided from
            // the archive DIRECTORY, which records each entry's uncompressed
            // length — so a compression bomb is refused on the strength of what
            // it claims about itself, before a byte of it is expanded. Reading
            // first and measuring afterwards is how a bomb wins.
            var preflight = Preflight(archive, options);
            if (preflight is not null) return DocumentProbeResult.Refused(preflight);

            var contentTypes = archive.GetEntry(ContentTypesPart);
            var relationships = archive.GetEntry(RelationshipsPart);
            if (contentTypes is null || relationships is null)
            {
                // A ZIP with no OPC structure is an archive somebody renamed.
                return DocumentProbeResult.Refused(DocumentExtractionReasons.OfficePackageInvalid);
            }

            string declared;
            try
            {
                declared = ReadBounded(contentTypes, options.EffectiveMaxOfficePartBytes);
            }
            catch (InvalidDataException)
            {
                return DocumentProbeResult.Refused(DocumentExtractionReasons.OfficePackageInvalid);
            }

            // MACROS ARE REFUSED BY NAME. Recognised before the supported
            // families so a `.docm` gets its own reason rather than the generic
            // "not a document", which would send an operator looking for a
            // corrupt file.
            if (Declares(declared, WordMacroMainPart)
                || Declares(declared, SpreadsheetMacroMainPart)
                || Declares(declared, PresentationMacroMainPart))
            {
                return DocumentProbeResult.Refused(DocumentExtractionReasons.MacroEnabledOffice);
            }

            var format = Declares(declared, WordMainPart) ? DocumentFormatKind.WordOpenXml
                : Declares(declared, SpreadsheetMainPart) ? DocumentFormatKind.SpreadsheetOpenXml
                : Declares(declared, PresentationMainPart) ? DocumentFormatKind.PresentationOpenXml
                : (DocumentFormatKind?)null;

            if (format is not { } kind)
            {
                return DocumentProbeResult.Refused(DocumentExtractionReasons.OfficePackageInvalid);
            }

            // THE NAME MUST NOT CONTRADICT THE PACKAGE. A DOCX renamed `.xlsx`
            // is refused outright rather than routed by either opinion: trusting
            // the extension sends a Word document to the spreadsheet parser, and
            // silently trusting the package means a person's file is read by
            // something they have no reason to expect. Refusing says so.
            if (!ExtensionAgrees(fileName, kind))
            {
                return DocumentProbeResult.Refused(DocumentExtractionReasons.OfficePackageInvalid);
            }

            return DocumentProbeResult.Accepted(kind, MimeTypeFor(kind));
        }
    }

    /// The archive-level resource bounds, read from the directory.
    ///
    /// Returns a sanitized reason, or null when the package is within every
    /// bound. The Open XML SDK is a parser, not a security boundary — and it
    /// documents that ZIP handling on modern .NET can hold a large working set —
    /// so this runs before the SDK is ever handed the package.
    public static string? Preflight(ZipArchive archive, DocumentExtractionOptions options)
    {
        if (archive.Entries.Count > options.EffectiveMaxOfficeEntries)
        {
            return DocumentExtractionReasons.OfficeTooManyEntries;
        }

        long total = 0;
        foreach (var entry in archive.Entries)
        {
            if (entry.Length > options.EffectiveMaxOfficePartBytes)
            {
                return DocumentExtractionReasons.OfficeEntryTooLarge;
            }

            total += entry.Length;
            if (total > options.EffectiveMaxOfficeUncompressedBytes)
            {
                return DocumentExtractionReasons.OfficePackageTooLarge;
            }

            // TRAVERSAL-SHAPED ENTRIES ARE REFUSED even though nothing here ever
            // writes to disk. Two reasons: the package is malformed by OPC's own
            // rules, which is evidence about the whole document; and a later
            // change that does materialize a part must not be the first place
            // this is noticed.
            if (LooksLikeTraversal(entry.FullName))
            {
                return DocumentExtractionReasons.OfficePackageInvalid;
            }
        }

        return null;
    }

    // ---- helpers ------------------------------------------------------------

    private static bool StartsWith(ReadOnlySpan<byte> bytes, ReadOnlySpan<byte> signature)
        => bytes.Length >= signature.Length && bytes[..signature.Length].SequenceEqual(signature);

    /// An encrypted OOXML document is a compound file holding an
    /// `EncryptedPackage` stream. The name appears UTF-16LE in the directory, so
    /// a bounded scan of the header distinguishes it from a genuine legacy
    /// binary document without implementing a compound-file reader.
    private static bool LooksEncryptedCompoundFile(ReadOnlySpan<byte> bytes)
    {
        var window = bytes[..Math.Min(bytes.Length, 16 * 1024)];
        ReadOnlySpan<byte> marker = "E\0n\0c\0r\0y\0p\0t\0e\0d\0P\0a\0c\0k\0a\0g\0e\0"u8;
        return window.IndexOf(marker) >= 0;
    }

    /// A single part, read under a hard cap.
    ///
    /// `entry.Length` is the directory's CLAIM about the uncompressed size, and
    /// a hostile package may lie. So the cap is enforced on what is actually
    /// read, one byte past the limit, rather than trusting the number that has
    /// already been checked in the preflight.
    private static string ReadBounded(ZipArchiveEntry entry, int maxBytes)
    {
        using var source = entry.Open();
        var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = source.Read(chunk, 0, chunk.Length)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                throw new InvalidDataException("part exceeds the configured bound");
            }
            buffer.Write(chunk, 0, read);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// Substring rather than XML parsing, and deliberately so at this stage. The
    /// content types part is untrusted XML from an untrusted package, and the
    /// probe's job is to decide whether it is worth handing to a real parser at
    /// all — resolving entities to answer that question would be doing the
    /// dangerous thing first. The main-part content types are long, specific
    /// strings that do not occur by accident.
    private static bool Declares(string contentTypesXml, string contentType)
        => contentTypesXml.Contains(contentType, StringComparison.Ordinal);

    private static bool ExtensionAgrees(string? fileName, DocumentFormatKind kind)
    {
        if (fileName is null) return true;

        var expected = kind switch
        {
            DocumentFormatKind.WordOpenXml => ".docx",
            DocumentFormatKind.SpreadsheetOpenXml => ".xlsx",
            DocumentFormatKind.PresentationOpenXml => ".pptx",
            _ => null,
        };
        if (expected is null) return true;

        // A package with no rich extension at all is accepted on its own
        // evidence — that is the `application/octet-stream` upload the candidate
        // gate exists for. Only a name that names a DIFFERENT supported format
        // is a contradiction.
        var named = RichExtensions.FirstOrDefault(
            e => fileName.EndsWith(e, StringComparison.OrdinalIgnoreCase));
        return named is null || string.Equals(named, expected, StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeTraversal(string entryName)
        => entryName.Contains("..", StringComparison.Ordinal)
           || entryName.StartsWith('/')
           || entryName.StartsWith('\\')
           || entryName.Contains(':', StringComparison.Ordinal);

    private static string MimeTypeFor(DocumentFormatKind kind) => kind switch
    {
        DocumentFormatKind.WordOpenXml => WordMimeType,
        DocumentFormatKind.SpreadsheetOpenXml => SpreadsheetMimeType,
        DocumentFormatKind.PresentationOpenXml => PresentationMimeType,
        DocumentFormatKind.Pdf => PdfMimeType,
        _ => "text/plain",
    };

    private static string Canonical(string? mimeType)
    {
        if (string.IsNullOrWhiteSpace(mimeType)) return string.Empty;
        var semicolon = mimeType.IndexOf(';');
        var value = semicolon >= 0 ? mimeType[..semicolon] : mimeType;
        return value.Trim().ToLowerInvariant();
    }
}
