using System.Text.RegularExpressions;

namespace NubArca.Api.Files;

// Slice 76: parses + validates a client-supplied relative upload path (from
// the browser's `webkitRelativePath`, e.g. "Holiday/2024/IMG_001.jpg") into a
// safe sequence of logical directory segments plus a file name.
//
// Client paths are NEVER trusted: this rejects absolute paths, drive letters,
// path traversal (".", "..", empty segments), and over-long segments/depth.
// Separators are normalised to "/". The result maps onto NubArca's logical
// folder tree — it never touches the physical store (blobs stay content-
// addressed; no physical directories are created).
public static partial class RelativeUploadPath
{
    // Mirrors the FileItem / Folder name limit (255) so a segment that would be
    // rejected by FolderService.CreateAsync / FileItemService.CreateAsync is
    // rejected up-front with a clearer message.
    private const int MaxSegmentLength = 255;

    // Defensive caps so a malicious/huge path can't blow up folder creation.
    private const int MaxDepth = 64;          // directory segments
    private const int MaxTotalLength = 4096;  // whole relative path

    public readonly record struct Parsed(IReadOnlyList<string> Directories, string FileName);

    // `relativePath` is the client-supplied path (may be null/empty for a
    // normal single-file upload). `fallbackFileName` is the multipart file
    // name, used when no relative path is provided. Throws ArgumentException
    // (→ HTTP 400) on any unsafe input.
    public static Parsed Parse(string? relativePath, string fallbackFileName)
    {
        // No relative path → normal upload: no directories, name = the file's
        // own name (validated downstream by FileItemService).
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return new Parsed([], fallbackFileName);
        }

        if (relativePath.Length > MaxTotalLength)
        {
            throw new ArgumentException("Upload path is too long.", nameof(relativePath));
        }

        // Normalise backslashes (Windows / spoofed separators) to "/".
        var normalised = relativePath.Replace('\\', '/').Trim();

        // Absolute paths are rejected: leading "/" or a Windows drive letter
        // ("C:/...", "c:..."). After backslash normalisation a drive path looks
        // like "C:/Users/...".
        if (normalised.StartsWith('/'))
        {
            throw new ArgumentException("Upload path must be relative, not absolute.", nameof(relativePath));
        }
        if (DriveLetterRegex().IsMatch(normalised))
        {
            throw new ArgumentException("Upload path must be relative, not absolute.", nameof(relativePath));
        }

        var rawSegments = normalised.Split('/');
        var segments = new List<string>(rawSegments.Length);
        foreach (var raw in rawSegments)
        {
            var seg = raw.Trim();
            if (seg.Length == 0)
            {
                // Empty segment: leading/trailing/double slash ("a//b", "a/").
                throw new ArgumentException("Upload path must not contain empty segments.", nameof(relativePath));
            }
            if (seg is "." or "..")
            {
                throw new ArgumentException("Upload path must not contain '.' or '..' segments.", nameof(relativePath));
            }
            if (seg.Length > MaxSegmentLength)
            {
                throw new ArgumentException(
                    $"Each upload path segment must be {MaxSegmentLength} characters or fewer.",
                    nameof(relativePath));
            }
            segments.Add(seg);
        }

        // The last segment is the file name; everything before it is the
        // directory chain.
        var fileName = segments[^1];
        var directories = segments.GetRange(0, segments.Count - 1);

        if (directories.Count > MaxDepth)
        {
            throw new ArgumentException(
                $"Upload path is too deeply nested (max {MaxDepth} folders).",
                nameof(relativePath));
        }

        return new Parsed(directories, fileName);
    }

    [GeneratedRegex(@"^[A-Za-z]:", RegexOptions.CultureInvariant)]
    private static partial Regex DriveLetterRegex();
}
