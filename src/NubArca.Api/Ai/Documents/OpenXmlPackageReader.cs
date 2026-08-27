using System.IO.Compression;
using System.Xml;

namespace NubArca.Api.Ai.Documents;

/// The bounded doorway every Office parser goes through.
///
/// The Open XML SDK is a parser, not a security boundary — and Microsoft
/// documents that ZIP package handling on modern .NET can hold a large working
/// set. So the package is measured before the SDK is allowed near it, and this
/// is the one place that happens: three parsers each writing their own preflight
/// is three chances to write two of the bounds.
///
/// XML resolution is the other half. Every part these parsers read is untrusted
/// markup from an untrusted document, and the two ways that becomes a problem —
/// entity expansion turning a kilobyte into a gigabyte, and an external entity
/// turning a document into a file read or an HTTP request — are both switched
/// off here rather than remembered per call site.
public static class OpenXmlPackageReader
{
    /// Opens the package after checking it, or returns the sanitized reason it
    /// was refused. The caller owns the returned archive.
    public static (ZipArchive? Archive, string? Reason) Open(
        ReadOnlyMemory<byte> bytes, DocumentExtractionOptions options)
    {
        var stream = new MemoryStream(bytes.ToArray(), writable: false);

        ZipArchive archive;
        try
        {
            archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        }
        catch (InvalidDataException)
        {
            stream.Dispose();
            return (null, DocumentExtractionReasons.OfficePackageInvalid);
        }

        var refusal = DocumentFormatProbe.Preflight(archive, options);
        if (refusal is not null)
        {
            archive.Dispose();
            return (null, refusal);
        }

        return (archive, null);
    }

    /// Reader settings that cannot be talked into doing anything.
    ///
    /// `DtdProcessing.Prohibit` stops the billion-laughs class of attack at the
    /// declaration rather than at the expansion, and a null resolver means an
    /// external entity pointing at `file:///etc/passwd`, a UNC path or an HTTPS
    /// URL resolves to nothing at all. Both matter because a document is a thing
    /// a stranger can send: "read this file for me" is not a request a parser
    /// should be able to honour.
    public static XmlReaderSettings SafeReaderSettings(long maxCharacters) => new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersFromEntities = 0,
        MaxCharactersInDocument = maxCharacters,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        IgnoreWhitespace = false,
        CloseInput = true,
    };

    /// One part's bytes, read under a hard cap that does not trust the archive
    /// directory's claim about its size.
    public static (byte[]? Bytes, string? Reason) ReadPart(
        ZipArchive archive, string partName, DocumentExtractionOptions options)
    {
        var entry = archive.GetEntry(partName);
        if (entry is null) return (null, DocumentExtractionReasons.OfficePackageInvalid);

        var max = options.EffectiveMaxOfficePartBytes;
        try
        {
            using var source = entry.Open();
            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            int read;
            while ((read = source.Read(chunk, 0, chunk.Length)) > 0)
            {
                if (buffer.Length + read > max)
                {
                    return (null, DocumentExtractionReasons.OfficeEntryTooLarge);
                }
                buffer.Write(chunk, 0, read);
            }
            return (buffer.ToArray(), null);
        }
        catch (InvalidDataException)
        {
            return (null, DocumentExtractionReasons.OfficePackageInvalid);
        }
    }
}
