using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NubArca.Api.Data;
using NubArca.Api.Domain.Print;

namespace NubArca.Api.Print;

/// <summary>
/// Which Print Agents this server still speaks to. An older agent that is
/// compatible keeps working — updating is never forced; only one older than
/// <see cref="MinimumVersion"/> is refused, and said to be.
/// </summary>
public static class PrintAgentCompatibility
{
    /// <summary>
    /// The oldest agent the server's protocol still serves. Raised only when a
    /// change in the protocol leaves older agents behind — never to push an
    /// update.
    /// </summary>
    public static readonly Version MinimumVersion = new(0, 2, 2);

    public const string Current = "current";
    public const string UpdateAvailable = "update-available";
    public const string Incompatible = "incompatible";
    public const string Unknown = "unknown";

    /// <summary>"0.6.0", or the four-part version an assembly reports; null for anything else.</summary>
    public static Version? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Version.TryParse(value.Trim(), out var parsed)) return null;
        return new Version(parsed.Major, parsed.Minor, Math.Max(0, parsed.Build));
    }

    public static string Display(Version version) => $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";

    /// <summary>An agent whose version cannot be read is not refused: nothing says it is too old.</summary>
    public static bool IsCompatible(string? reported) => Parse(reported) is not { } version || version >= MinimumVersion;

    /// <summary>
    /// The first agent that prints a joined 20x15 from its two 10x15 files
    /// (<c>PrintArtifactParts</c>). An older one would wait for one sheet, so
    /// those jobs wait for it to update; an unreadable version is served, as above.
    /// </summary>
    public static readonly Version JoinedPartsVersion = new(0, 11, 0);

    public static bool PrintsJoinedParts(string? reported) =>
        Parse(reported) is not { } version || version >= JoinedPartsVersion;

    /// <summary>
    /// How an agent stands against this server and the bundle it hands out:
    /// its compatibility, and whether it claims the published version without
    /// being that build — a development build, or one not from this project's CI.
    /// </summary>
    public static PrintAgentStatusDto Describe(string? reportedVersion, string? reportedBuild, PrintAgentBundle? bundle)
    {
        var version = Parse(reportedVersion);
        var build = string.IsNullOrWhiteSpace(reportedBuild) ? null : reportedBuild;
        if (version is null) return new(null, Short(build), Unknown, false, bundle?.Version);
        var latest = bundle is null ? null : Parse(bundle.Version);
        var compatibility = version < MinimumVersion ? Incompatible
            : latest is not null && version < latest ? UpdateAvailable
            : Current;
        var unofficial = latest is not null && version == latest
            && !string.Equals(build, bundle!.SourceCommit, StringComparison.OrdinalIgnoreCase);
        return new(Display(version), Short(build), compatibility, unofficial, bundle?.Version);
    }

    private static string? Short(string? build) => build is null ? null : build[..Math.Min(12, build.Length)];
}

public sealed class PrintAgentBundleOptions
{
    public const string SectionName = "PrintAgentBundle";

    /// <summary>
    /// Where the published bundles are, read-only inside the container: the
    /// zip and, last, its <c>current-&lt;runtime&gt;.json</c> pointer, written by
    /// <c>deploy/pull-publish-print-agent-image.sh</c>.
    /// </summary>
    public string Directory { get; set; } = "/var/lib/nubarca/print-agent";
}

/// <summary>One published bundle, as its pointer names it and its bytes prove.</summary>
public sealed record PrintAgentBundle(
    string Version, string Runtime, string FileName, string Sha256, long SizeBytes, string SourceCommit,
    string FilePath);

/// <summary>
/// Reads the published bundle. A pointer that does not parse, names a file
/// that is not there or whose bytes do not hash to what it says, is no bundle
/// at all: nothing half-checked is ever offered for download. The hash is
/// taken once per published file, not per request.
/// </summary>
public sealed partial class PrintAgentBundleStore
{
    public const string LinuxX64 = "linux-x64";
    private const long MaxBundleBytes = 512L * 1024 * 1024;

    [GeneratedRegex(@"^\d{1,4}\.\d{1,4}\.\d{1,4}$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();
    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Pattern();
    [GeneratedRegex("^[0-9a-f]{40}$", RegexOptions.CultureInvariant)]
    private static partial Regex CommitPattern();

    private readonly string _directory;
    private readonly ILogger<PrintAgentBundleStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private (string Stamp, PrintAgentBundle? Bundle)? _cached;

    public PrintAgentBundleStore(IOptions<PrintAgentBundleOptions> options, ILogger<PrintAgentBundleStore> logger)
    {
        _directory = options.Value.Directory;
        _logger = logger;
    }

    public static string FileNameFor(string version, string runtime) => $"nubarca-print-agent-{version}-{runtime}.zip";

    /// <summary>The bundle published for Linux (the Print Box), or null when there is none to trust.</summary>
    public async Task<PrintAgentBundle?> CurrentAsync(CancellationToken cancellationToken)
    {
        var pointer = Path.Combine(_directory, $"current-{LinuxX64}.json");
        if (!File.Exists(pointer)) return null;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var stamp = Stamp(pointer);
            if (_cached is { } cached && cached.Stamp == stamp) return cached.Bundle;
            var bundle = await ReadAsync(pointer, cancellationToken);
            // Stamped again after the read: a bundle published meanwhile is read next time.
            _cached = (Stamp(pointer), bundle);
            return bundle;
        }
        finally { _gate.Release(); }
    }

    private string Stamp(string pointer)
    {
        var info = new FileInfo(pointer);
        var parts = new List<string> { info.Exists ? $"{info.Length}:{info.LastWriteTimeUtc.Ticks}" : "-" };
        var zips = System.IO.Directory.Exists(_directory)
            ? System.IO.Directory.GetFiles(_directory, "*.zip")
            : [];
        foreach (var zip in zips.Order(StringComparer.Ordinal))
        {
            var file = new FileInfo(zip);
            parts.Add($"{file.Name}:{file.Length}:{file.LastWriteTimeUtc.Ticks}");
        }
        return string.Join('|', parts);
    }

    private async Task<PrintAgentBundle?> ReadAsync(string pointer, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = File.OpenRead(pointer);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = document.RootElement;
            string Text(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty : string.Empty;
            var version = Text("version");
            var runtime = Text("runtime");
            var fileName = Text("fileName");
            var sha256 = Text("sha256");
            var commit = Text("sourceCommit");
            var size = root.TryGetProperty("sizeBytes", out var sizeValue) && sizeValue.TryGetInt64(out var parsed) ? parsed : -1;
            if (!VersionPattern().IsMatch(version) || runtime != LinuxX64
                || fileName != FileNameFor(version, runtime) || !Sha256Pattern().IsMatch(sha256)
                || !CommitPattern().IsMatch(commit) || size is <= 0 or > MaxBundleBytes)
            {
                _logger.LogError("Print Agent bundle pointer is not valid; no bundle is offered.");
                return null;
            }
            var path = Path.Combine(_directory, fileName);
            var file = new FileInfo(path);
            if (!file.Exists || file.Length != size)
            {
                _logger.LogError("Print Agent bundle {FileName} is missing or not the size its pointer says.", fileName);
                return null;
            }
            await using var bytes = File.OpenRead(path);
            var actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(bytes, cancellationToken));
            if (actual != sha256)
            {
                _logger.LogError("Print Agent bundle {FileName} does not hash to its pointer; no bundle is offered.", fileName);
                return null;
            }
            return new PrintAgentBundle(version, runtime, fileName, sha256, size, commit, path);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.LogError("Print Agent bundle could not be read ({ExceptionType}); no bundle is offered.", ex.GetType().Name);
            return null;
        }
    }
}

/// <summary>
/// The bundle as the printers page shows it: what is published, whether it is
/// handed out now, and — for an administrator — the switch.
/// </summary>
public sealed record PrintAgentBundleDto(
    bool Available, bool Sharing, bool CanManage, string MinimumVersion,
    string? Version, string? Runtime, string? FileName, string? Sha256, long? SizeBytes, string? SourceCommit,
    string? DownloadPath);

public sealed record SetPrintAgentSharingRequest(bool Enabled);

/// <summary>How a station's agent stands: its version, build, compatibility and whether it is an official build.</summary>
public sealed record PrintAgentStatusDto(
    string? Version, string? Build, string Compatibility, bool Unofficial, string? LatestVersion,
    /// <summary>The agent updates itself on request (a Print Box with the update service).</summary>
    bool SelfUpdate = false,
    /// <summary>The version it was asked to update to and has not reached yet.</summary>
    string? UpdateTarget = null,
    DateTime? UpdateRequestedAt = null,
    /// <summary><c>ok</c> or <c>failed</c>: how the last requested update ended.</summary>
    string? UpdateResult = null,
    string? UpdateResultVersion = null,
    DateTime? UpdateResultAt = null);

public sealed class PrintAgentDistributionService
{
    public const string DownloadRoute = "/api/print-agent/download";

    private readonly AppDbContext _db;
    private readonly PrintAgentBundleStore _store;
    private readonly TimeProvider _time;

    public PrintAgentDistributionService(AppDbContext db, PrintAgentBundleStore store, TimeProvider time)
    {
        _db = db;
        _store = store;
        _time = time;
    }

    public async Task<bool> IsSharingAsync(CancellationToken cancellationToken) =>
        await _db.PrintAgentDistributions.AsNoTracking()
            .Where(x => x.Id == PrintAgentDistribution.SingletonId)
            .Select(x => (bool?)x.SharingEnabled)
            .SingleOrDefaultAsync(cancellationToken) ?? false;

    public async Task<PrintAgentBundleDto> DescribeAsync(bool canManage, CancellationToken cancellationToken)
    {
        var bundle = await _store.CurrentAsync(cancellationToken);
        var sharing = await IsSharingAsync(cancellationToken);
        var minimum = PrintAgentCompatibility.Display(PrintAgentCompatibility.MinimumVersion);
        return bundle is null
            ? new(false, sharing, canManage, minimum, null, null, null, null, null, null, null)
            : new(true, sharing, canManage, minimum, bundle.Version, bundle.Runtime, bundle.FileName, bundle.Sha256,
                bundle.SizeBytes, bundle.SourceCommit,
                sharing ? $"{DownloadRoute}/{bundle.FileName}" : null);
    }

    public async Task SetSharingAsync(Guid userId, bool enabled, CancellationToken cancellationToken)
    {
        var row = await _db.PrintAgentDistributions
            .SingleOrDefaultAsync(x => x.Id == PrintAgentDistribution.SingletonId, cancellationToken);
        if (row is null)
        {
            row = new PrintAgentDistribution();
            _db.PrintAgentDistributions.Add(row);
        }
        row.SharingEnabled = enabled;
        row.UpdatedAt = _time.GetUtcNow().UtcDateTime;
        row.UpdatedByUserId = userId;
        await _db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>The file to send for <paramref name="fileName"/>: only the current bundle, and only while it is shared.</summary>
    public async Task<PrintAgentBundle?> DownloadableAsync(string fileName, CancellationToken cancellationToken)
    {
        if (!await IsSharingAsync(cancellationToken)) return null;
        var bundle = await _store.CurrentAsync(cancellationToken);
        return bundle is not null && string.Equals(bundle.FileName, fileName, StringComparison.Ordinal) ? bundle : null;
    }

    public Task<PrintAgentBundle?> CurrentBundleAsync(CancellationToken cancellationToken) =>
        _store.CurrentAsync(cancellationToken);
}
