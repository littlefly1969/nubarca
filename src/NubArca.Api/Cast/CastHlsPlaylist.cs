using System.Text;
using NubArca.Api.Storage;

namespace NubArca.Api.Cast;

// Rewrites an HLS playlist so every reference inside it points at a
// grant-scoped Cast route carrying the same bearer secret.
//
// Why a rewrite is unavoidable. HLS resolves a relative URI against the
// PLAYLIST's own URL, and URI resolution discards the query string — so a
// variant playlist fetched as `.../hls/high/stream.m3u8?token=X` would resolve
// its own `seg-0.m4s` to `.../hls/high/seg-0.m4s` with NO token, and the
// receiver would be answered 404 on the first segment. Both levels of the
// ladder therefore have to be rewritten, not just the master.
//
// Why this is not string replacement. Every URI is validated against
// HlsDerivativeStorage.IsServableRelativePath — the same whitelist the read
// path enforces — before anything is emitted, and a playlist containing even
// one URI that does not validate is rejected WHOLE (null → 404). Traversal,
// percent-encoded traversal, an absolute foreign URL, an unexpected file name
// and a rendition directory that does not exist all fail that check, so none of
// them can be turned into a signed URL. The output is always origin-relative:
// nothing here reads a Host header, so a spoofed one cannot redirect a
// television at somebody else's server.
public static class CastHlsPlaylist
{
    // A master's rendition URIs are storage-relative already ("high/stream.m3u8").
    public static string? RewriteMaster(string playlist, string mediaBasePath, string token)
        => Rewrite(playlist, mediaBasePath, renditionPrefix: null, token);

    // A variant's URIs are relative to its OWN directory ("seg-0.m4s"), so the
    // rendition it was served from is what makes them storage-relative again.
    public static string? RewriteVariant(
        string playlist, string mediaBasePath, string rendition, string token)
        => Rewrite(playlist, mediaBasePath, rendition, token);

    private static string? Rewrite(
        string playlist, string mediaBasePath, string? renditionPrefix, string token)
    {
        var query = "?token=" + Uri.EscapeDataString(token);
        var lines = playlist.Split('\n');
        var output = new StringBuilder(playlist.Length + (lines.Length * 64));

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');

            if (line.Length == 0)
            {
                output.Append('\n');
                continue;
            }

            if (line.StartsWith('#'))
            {
                // Tag lines are copied verbatim EXCEPT for a URI attribute —
                // #EXT-X-MAP:URI="init_0.mp4" is how an fMP4 ladder names its
                // initialisation segment, and a receiver that cannot fetch it
                // plays nothing at all.
                var rewrittenTag = RewriteUriAttribute(line, mediaBasePath, renditionPrefix, query);
                if (rewrittenTag is null)
                {
                    return null;
                }
                output.Append(rewrittenTag);
                output.Append('\n');
                continue;
            }

            var target = BuildTarget(line, mediaBasePath, renditionPrefix, query);
            if (target is null)
            {
                return null;
            }
            output.Append(target);
            output.Append('\n');
        }

        // Split on '\n' yields a trailing empty element for a playlist that ends
        // with a newline; the loop already emitted its own separator, so drop
        // the duplicate rather than growing the body on every rewrite.
        if (output.Length > 0 && output[^1] == '\n')
        {
            output.Length -= 1;
        }
        return output.ToString();
    }

    private static string? RewriteUriAttribute(
        string tagLine, string mediaBasePath, string? renditionPrefix, string query)
    {
        const string marker = "URI=\"";
        var start = tagLine.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
        {
            return tagLine;
        }

        var valueStart = start + marker.Length;
        var valueEnd = tagLine.IndexOf('"', valueStart);
        if (valueEnd < 0)
        {
            // A malformed tag is not something to guess at.
            return null;
        }

        var value = tagLine[valueStart..valueEnd];
        var target = BuildTarget(value, mediaBasePath, renditionPrefix, query);
        if (target is null)
        {
            return null;
        }

        return string.Concat(tagLine.AsSpan(0, valueStart), target, tagLine.AsSpan(valueEnd));
    }

    // The whole safety argument in one place: a URI is only ever emitted when
    // the storage-relative path it denotes is one this installation would
    // actually serve.
    private static string? BuildTarget(
        string uri, string mediaBasePath, string? renditionPrefix, string query)
    {
        var relative = renditionPrefix is null ? uri : renditionPrefix + "/" + uri;
        if (!HlsDerivativeStorage.IsServableRelativePath(relative))
        {
            return null;
        }

        // A master listing itself would be a resolution loop, and a variant is
        // never reached through the master's own name. `master.m3u8` passes the
        // storage whitelist, so it is excluded explicitly here.
        if (relative == "master.m3u8")
        {
            return null;
        }

        return $"{mediaBasePath}/hls/{relative}{query}";
    }
}
