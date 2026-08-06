using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace NubArca.Api.Storage;

// Video-hls slice 1: physical store for HLS playback derivatives. An HLS
// ladder is inherently multi-file (master playlist + per-rendition playlists,
// init segments and media segments), which does not fit the one-derivative =
// one-BlobObject model used by thumbnails/posters — per-segment BlobObject
// rows would mean thousands of tiny refcounted rows for zero benefit. This
// store is the single, narrowly-scoped exception: a directory tree under the
// DERIVED root, sharded exactly like the object store and keyed by the SOURCE
// blob's sha256, so duplicate FileItems (and re-uploads of the same content)
// share one ladder and never retranscode:
//
//   {derivedRoot}/hls/{first2}/{next2}/{sha256}/master.m3u8
//                                             /high/stream.m3u8 + init*.mp4 + seg-N.m4s
//                                             /low/...
//
// Everything under hls/ is regenerable cache. Writes stage into hls/tmp and
// publish with an atomic Directory.Move; a lost publish race is success.
// Every external input (sha, served relative path) is regex-validated and the
// resolved path is root-contained before any filesystem call — same
// path-traversal defence as LocalFileSystemBlobStorage. No storage keys or
// physical paths are ever logged by callers of this type.
public sealed partial class HlsDerivativeStorage
{
    private static readonly Regex ShaPattern = ShaRegex();
    private static readonly Regex ServableFilePattern = ServableFileRegex();

    private readonly string _hlsRoot;
    private readonly string _tempRoot;

    public HlsDerivativeStorage(IOptions<BlobStorageOptions> options)
        : this(options.Value.EffectiveDerivedRootPath)
    {
    }

    // Explicit-root ctor for tests.
    public HlsDerivativeStorage(string derivedRootPath)
    {
        if (string.IsNullOrWhiteSpace(derivedRootPath))
        {
            throw new InvalidOperationException(
                "Storage:RootPath is not configured. Set it via appsettings or the environment variable Storage__RootPath.");
        }

        _hlsRoot = Path.Combine(Path.GetFullPath(derivedRootPath), "hls");
        _tempRoot = Path.Combine(_hlsRoot, "tmp");
    }

    // A fresh, unique staging directory for one transcode run. Lives under the
    // hls root so the publish Directory.Move is a same-volume rename.
    public string CreateStagingDirectory()
    {
        var dir = Path.Combine(_tempRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    // True when a published ladder exists for this source blob (a ready DB row
    // with wiped bytes must regenerate — derived artifacts are cache).
    public bool Exists(string sha256)
        => File.Exists(Path.Combine(ResolveDirectory(sha256), "master.m3u8"));

    // Atomically publish a fully-written staging directory as THE ladder for
    // this blob. If a concurrent run already published, the staging copy is
    // discarded — losing the race is success.
    public void Publish(string sha256, string stagingDirectory)
    {
        var final = ResolveDirectory(sha256);
        Directory.CreateDirectory(Path.GetDirectoryName(final)!);

        if (Directory.Exists(final))
        {
            DeleteDirectoryQuietly(stagingDirectory);
            return;
        }

        try
        {
            Directory.Move(stagingDirectory, final);
        }
        catch (IOException) when (Directory.Exists(final))
        {
            DeleteDirectoryQuietly(stagingDirectory);
        }
    }

    // Remove the published ladder for this blob. Idempotent. Used by --force
    // regeneration and by the blob janitor when the source blob is purged.
    public void Delete(string sha256)
    {
        var final = ResolveDirectory(sha256);
        try
        {
            if (Directory.Exists(final))
            {
                Directory.Delete(final, recursive: true);
            }
        }
        catch (DirectoryNotFoundException)
        {
            // already gone — desired post-condition
        }
    }

    // Open one servable ladder file. `relativePath` is untrusted client input
    // (slice 2 routes it from the URL): it must match the exact whitelist of
    // file shapes ffmpeg produces, and the resolved path must stay inside this
    // blob's directory. Anything else — traversal, unknown names, missing
    // files — collapses to null (no-leak: indistinguishable from absent).
    public Stream? OpenServableFile(string sha256, string relativePath)
    {
        if (string.IsNullOrEmpty(relativePath) || !ServableFilePattern.IsMatch(relativePath))
        {
            return null;
        }

        var dir = ResolveDirectory(sha256);
        var fullPath = Path.GetFullPath(
            Path.Combine(dir, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!fullPath.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            return new FileStream(
                fullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                81920, FileOptions.Asynchronous);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    // Best-effort cleanup of a staging (or any partial) directory.
    public void DeleteDirectoryQuietly(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch
        {
            // Best-effort: a leftover temp dir is disk waste only; the next
            // operator wipe of the derived cache reclaims it.
        }
    }

    private string ResolveDirectory(string sha256)
    {
        if (string.IsNullOrEmpty(sha256) || !ShaPattern.IsMatch(sha256))
        {
            throw new ArgumentException("Malformed source blob hash.", nameof(sha256));
        }
        return Path.Combine(_hlsRoot, sha256[..2], sha256[2..4], sha256);
    }

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex ShaRegex();

    // master.m3u8 at the root; per-rendition playlist, fMP4 init segment
    // (ffmpeg suffixes the variant index: init_0.mp4 / init_1.mp4) and media
    // segments inside high/ or low/.
    [GeneratedRegex(@"^(master\.m3u8|(high|low)/(stream\.m3u8|init(_\d+)?\.mp4|seg-\d+\.m4s))$")]
    private static partial Regex ServableFileRegex();
}
