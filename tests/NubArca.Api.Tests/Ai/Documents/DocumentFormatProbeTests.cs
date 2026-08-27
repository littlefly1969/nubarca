using System.IO.Compression;
using System.Text;
using NubArca.Api.Ai.Documents;
using Xunit;

namespace NubArca.Api.Tests.Ai.Documents;

// WHAT IS THIS FILE — asked of the bytes, never of the name.
//
// Every test here is the same shape: something claims to be a document, and the
// claim is either backed by structure or it is not. The claims are what an
// attacker controls and what ordinary uploaders get wrong by accident, so the
// interesting cases are all disagreements — a ZIP wearing a `.docx`, a DOCX
// wearing an `.xlsx`, a `.pdf` full of arbitrary bytes.
//
// The consequence of getting this wrong is not cosmetic. Routing a document to
// the wrong parser is untrusted input arriving somewhere written for a different
// structure, which is exactly where parser bugs stop being theoretical.
public sealed class DocumentFormatProbeTests
{
    private readonly DocumentExtractionOptions _options = new();

    // ---- candidate gate -----------------------------------------------------

    [Fact]
    public void Declared_Rich_Types_Are_Worth_Opening()
    {
        Assert.True(DocumentFormatProbe.IsCandidate("application/pdf", "manuale.pdf"));
        Assert.True(DocumentFormatProbe.IsCandidate(DocumentFormatProbe.WordMimeType, "contratto.docx"));
        Assert.True(DocumentFormatProbe.IsCandidate(DocumentFormatProbe.SpreadsheetMimeType, "budget.xlsx"));
        Assert.True(DocumentFormatProbe.IsCandidate(DocumentFormatProbe.PresentationMimeType, "piano.pptx"));
    }

    [Fact]
    public void An_Uninformative_Type_Needs_A_Rich_Extension_To_Be_Worth_Opening()
    {
        // The uploader that sends everything as octet-stream is extremely
        // common, so refusing it outright would make rich ingestion depend on
        // which client somebody used. The extension buys a bounded look and
        // nothing else.
        Assert.True(DocumentFormatProbe.IsCandidate("application/octet-stream", "contratto.docx"));
        Assert.True(DocumentFormatProbe.IsCandidate("application/zip", "budget.xlsx"));
        Assert.False(DocumentFormatProbe.IsCandidate("application/octet-stream", "vacanza.jpg"));
        Assert.False(DocumentFormatProbe.IsCandidate("application/octet-stream", null));
    }

    [Fact]
    public void A_Photo_Is_Never_Worth_Opening()
    {
        // A library is mostly photos. Reading each one to discover it is a JPEG
        // would be the expensive way to learn nothing.
        Assert.False(DocumentFormatProbe.IsCandidate("image/jpeg", "vacanza.jpg"));
        Assert.False(DocumentFormatProbe.IsCandidate("video/mp4", "clip.mp4"));
    }

    // ---- PDF ----------------------------------------------------------------

    [Fact]
    public void A_Real_Pdf_Signature_Is_Accepted()
    {
        var result = DocumentFormatProbe.Probe(MinimalPdf(), "manuale.pdf", _options);

        Assert.True(result.Ok);
        Assert.Equal(DocumentFormatKind.Pdf, result.Format);
        Assert.Equal(DocumentFormatProbe.PdfMimeType, result.CanonicalMimeType);
    }

    [Fact]
    public void A_Pdf_Named_File_Full_Of_Arbitrary_Bytes_Is_Refused()
    {
        // Naming a file optimistically is not evidence about its contents.
        var bytes = Encoding.UTF8.GetBytes("questo non è affatto un PDF, è solo testo");

        var result = DocumentFormatProbe.Probe(bytes, "manuale.pdf", _options);

        Assert.False(result.Ok);
        Assert.Equal(DocumentExtractionReasons.UnsupportedDocumentFormat, result.Reason);
    }

    // ---- OOXML --------------------------------------------------------------

    [Theory]
    [InlineData(DocumentFormatKind.WordOpenXml, "contratto.docx")]
    [InlineData(DocumentFormatKind.SpreadsheetOpenXml, "budget.xlsx")]
    [InlineData(DocumentFormatKind.PresentationOpenXml, "piano.pptx")]
    public void A_Well_Formed_Package_Is_Recognised_By_Its_Own_Declaration(
        DocumentFormatKind expected, string fileName)
    {
        var result = DocumentFormatProbe.Probe(Package(expected), fileName, _options);

        Assert.True(result.Ok, result.Reason);
        Assert.Equal(expected, result.Format);
    }

    [Fact]
    public void An_Arbitrary_Zip_Renamed_Docx_Is_Refused()
    {
        // A ZIP with no OPC structure is an archive somebody renamed. It has no
        // content types part, so it never gets to claim anything.
        var zip = Zip(("readme.txt", "solo un archivio qualunque"));

        var result = DocumentFormatProbe.Probe(zip, "contratto.docx", _options);

        Assert.False(result.Ok);
        Assert.Equal(DocumentExtractionReasons.OfficePackageInvalid, result.Reason);
    }

    [Fact]
    public void A_Package_Missing_Its_Relationships_Is_Refused()
    {
        var zip = Zip(
            ("[Content_Types].xml", ContentTypes(WordMainPart)),
            ("word/document.xml", "<document/>"));

        var result = DocumentFormatProbe.Probe(zip, "contratto.docx", _options);

        Assert.False(result.Ok);
        Assert.Equal(DocumentExtractionReasons.OfficePackageInvalid, result.Reason);
    }

    [Fact]
    public void A_Docx_Renamed_Xlsx_Never_Reaches_The_Spreadsheet_Parser()
    {
        // THE ROUTING TEST. The package says Word and the name says Excel, and
        // the answer is neither — refused, rather than resolved in favour of
        // whichever opinion the code happened to consult first. Trusting the
        // name hands a Word document to the spreadsheet parser; silently
        // trusting the package means a person's file is read by something they
        // had no reason to expect.
        var result = DocumentFormatProbe.Probe(
            Package(DocumentFormatKind.WordOpenXml), "in-realta-word.xlsx", _options);

        Assert.False(result.Ok);
        Assert.Equal(DocumentExtractionReasons.OfficePackageInvalid, result.Reason);
    }

    [Fact]
    public void A_Package_Uploaded_Without_A_Rich_Extension_Is_Judged_On_Its_Own_Evidence()
    {
        // No contradiction to resolve: the name says nothing about format, so
        // the package's own declaration stands.
        var result = DocumentFormatProbe.Probe(
            Package(DocumentFormatKind.SpreadsheetOpenXml), "allegato", _options);

        Assert.True(result.Ok, result.Reason);
        Assert.Equal(DocumentFormatKind.SpreadsheetOpenXml, result.Format);
    }

    [Theory]
    [InlineData("application/vnd.ms-word.document.macroEnabled.main+xml", "documento.docm")]
    [InlineData("application/vnd.ms-excel.sheet.macroEnabled.main+xml", "foglio.xlsm")]
    [InlineData("application/vnd.ms-powerpoint.presentation.macroEnabled.main+xml", "slide.pptm")]
    public void Macro_Enabled_Packages_Are_Refused_By_Name(string mainPart, string fileName)
    {
        // Refused as a FORMAT rather than parsed with macros ignored. What the
        // safe subset of a macro-enabled document is happens to be a judgement
        // this slice does not make, and a reason code an operator can read beats
        // a document that silently half-works.
        var zip = Zip(
            ("[Content_Types].xml", ContentTypes(mainPart)),
            ("_rels/.rels", "<Relationships/>"));

        var result = DocumentFormatProbe.Probe(zip, fileName, _options);

        Assert.False(result.Ok);
        Assert.Equal(DocumentExtractionReasons.MacroEnabledOffice, result.Reason);
    }

    // ---- legacy and encrypted ----------------------------------------------

    [Fact]
    public void Legacy_Binary_Office_Is_Refused_With_Its_Own_Reason()
    {
        var result = DocumentFormatProbe.Probe(CompoundFile(encrypted: false), "vecchio.doc", _options);

        Assert.False(result.Ok);
        Assert.Equal(DocumentExtractionReasons.LegacyOfficeFormat, result.Reason);
    }

    [Fact]
    public void An_Encrypted_Package_Is_Refused_As_Encrypted_Not_As_Legacy()
    {
        // Both are compound files and they share their first eight bytes, so
        // without looking further every password-protected document would be
        // reported as a 1997 Word file — sending an operator to fix the wrong
        // thing.
        var result = DocumentFormatProbe.Probe(CompoundFile(encrypted: true), "protetto.docx", _options);

        Assert.False(result.Ok);
        Assert.Equal(DocumentExtractionReasons.EncryptedDocument, result.Reason);
    }

    // ---- archive resource bounds -------------------------------------------

    [Fact]
    public void Too_Many_Entries_Is_Refused()
    {
        var options = new DocumentExtractionOptions { MaxOfficeEntries = 4 };
        var entries = Enumerable.Range(0, 12).Select(i => ($"part{i}.xml", "<x/>")).ToArray();

        var result = DocumentFormatProbe.Probe(Zip(entries), "contratto.docx", options);

        Assert.False(result.Ok);
        Assert.Equal(DocumentExtractionReasons.OfficeTooManyEntries, result.Reason);
    }

    [Fact]
    public void A_Single_Oversized_Part_Is_Refused()
    {
        var options = new DocumentExtractionOptions { MaxOfficePartBytes = 1024 };
        var zip = Zip(
            ("[Content_Types].xml", ContentTypes(WordMainPart)),
            ("_rels/.rels", "<Relationships/>"),
            ("word/document.xml", new string('a', 8192)));

        var result = DocumentFormatProbe.Probe(zip, "contratto.docx", options);

        Assert.False(result.Ok);
        Assert.Equal(DocumentExtractionReasons.OfficeEntryTooLarge, result.Reason);
    }

    [Fact]
    public void A_Compression_Bomb_Is_Refused_Before_Anything_Is_Expanded()
    {
        // Ten megabytes of zeroes compress to almost nothing, which is the whole
        // trick. The refusal comes from the archive DIRECTORY — the entry
        // lengths the package declares about itself — so the decision is made
        // without expanding a byte. Reading first and measuring afterwards is
        // how a bomb wins.
        var options = new DocumentExtractionOptions
        {
            MaxOfficeUncompressedBytes = 1024 * 1024,
            MaxOfficePartBytes = 64 * 1024 * 1024,
        };
        var zip = Zip(
            ("[Content_Types].xml", ContentTypes(WordMainPart)),
            ("_rels/.rels", "<Relationships/>"),
            ("word/document.xml", new string('\0', 10 * 1024 * 1024)));

        // The compressed package really is tiny — proof the bound is not just
        // the source-size gate wearing a different name.
        Assert.True(zip.Length < 128 * 1024, $"compressed to {zip.Length} bytes");

        var result = DocumentFormatProbe.Probe(zip, "contratto.docx", options);

        Assert.False(result.Ok);
        Assert.Equal(DocumentExtractionReasons.OfficePackageTooLarge, result.Reason);
    }

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("/etc/passwd")]
    [InlineData("word/../../../secret.xml")]
    [InlineData("C:\\Windows\\system32\\config")]
    public void Traversal_Shaped_Entries_Are_Refused(string entryName)
    {
        // Nothing here writes to disk, so this is not preventing a write today.
        // It is refusing a package that is malformed by OPC's own rules —
        // evidence about the whole document — and making sure a later change
        // that DOES materialize a part is not the first place anyone notices.
        var zip = Zip(
            ("[Content_Types].xml", ContentTypes(WordMainPart)),
            ("_rels/.rels", "<Relationships/>"),
            (entryName, "payload"));

        var result = DocumentFormatProbe.Probe(zip, "contratto.docx", _options);

        Assert.False(result.Ok);
        Assert.Equal(DocumentExtractionReasons.OfficePackageInvalid, result.Reason);
    }

    [Fact]
    public void A_Truncated_Package_Is_Sanitized_Not_Thrown()
    {
        // Half a ZIP produces an InvalidDataException deep inside the framework.
        // It reaches the caller as a reason token, because the alternative is an
        // exception message carrying a file path into a diagnostic.
        var whole = Package(DocumentFormatKind.WordOpenXml);
        var truncated = whole[..(whole.Length / 2)];

        var result = DocumentFormatProbe.Probe(truncated, "contratto.docx", _options);

        Assert.False(result.Ok);
        Assert.Equal(DocumentExtractionReasons.OfficePackageInvalid, result.Reason);
    }

    [Fact]
    public void An_Empty_File_Is_Empty_Not_Unsupported()
    {
        var result = DocumentFormatProbe.Probe(Array.Empty<byte>(), "vuoto.docx", _options);

        Assert.False(result.Ok);
        Assert.Equal(DocumentExtractionReasons.Empty, result.Reason);
    }

    [Fact]
    public void No_Reason_Code_Carries_A_Filename_Or_A_Path()
    {
        // These tokens travel into DocumentText.ErrorCode and from there into
        // operator diagnostics, so they must be vocabulary rather than prose.
        var reasons = new[]
        {
            DocumentFormatProbe.Probe(Zip(("x.txt", "y")), "riservato-2027.docx", _options).Reason,
            DocumentFormatProbe.Probe(CompoundFile(encrypted: true), "stipendi.xlsx", _options).Reason,
            DocumentFormatProbe.Probe(
                Encoding.UTF8.GetBytes("no"), "diagnosi-medica.pdf", _options).Reason,
        };

        Assert.All(reasons, r =>
        {
            Assert.NotNull(r);
            Assert.DoesNotContain("riservato", r!, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("stipendi", r, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("diagnosi", r, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("/", r, StringComparison.Ordinal);
            Assert.Matches("^[a-z-]+$", r);
        });
    }

    // ---- fixture ------------------------------------------------------------

    private const string WordMainPart =
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml";
    private const string SpreadsheetMainPart =
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml";
    private const string PresentationMainPart =
        "application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml";

    private static string ContentTypes(string mainPart) =>
        $"""
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
          <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
          <Override PartName="/main" ContentType="{mainPart}"/>
        </Types>
        """;

    /// A structurally valid package of the given family. Not a real document —
    /// the probe reads structure, and a real one would only make the fixture
    /// opaque.
    private static byte[] Package(DocumentFormatKind kind)
    {
        var (mainPart, path) = kind switch
        {
            DocumentFormatKind.WordOpenXml => (WordMainPart, "word/document.xml"),
            DocumentFormatKind.SpreadsheetOpenXml => (SpreadsheetMainPart, "xl/workbook.xml"),
            DocumentFormatKind.PresentationOpenXml => (PresentationMainPart, "ppt/presentation.xml"),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        return Zip(
            ("[Content_Types].xml", ContentTypes(mainPart)),
            ("_rels/.rels", """<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"/>"""),
            (path, "<root/>"));
    }

    private static byte[] Zip(params (string Name, string Content)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
                using var stream = entry.Open();
                var bytes = Encoding.UTF8.GetBytes(content);
                stream.Write(bytes, 0, bytes.Length);
            }
        }
        return buffer.ToArray();
    }

    private static byte[] MinimalPdf()
        => Encoding.ASCII.GetBytes("%PDF-1.7\n1 0 obj\n<< /Type /Catalog >>\nendobj\n%%EOF\n");

    /// An OLE2 compound file header, optionally carrying the UTF-16LE
    /// `EncryptedPackage` stream name that distinguishes a password-protected
    /// OOXML document from a genuine legacy binary one.
    private static byte[] CompoundFile(bool encrypted)
    {
        var header = new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 };
        var body = encrypted
            ? Encoding.Unicode.GetBytes("EncryptedPackage")
            : Encoding.Unicode.GetBytes("WordDocument");
        return header.Concat(new byte[512]).Concat(body).ToArray();
    }
}
