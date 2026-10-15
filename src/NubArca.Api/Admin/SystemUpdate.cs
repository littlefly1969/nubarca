using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace NubArca.Api.Admin;

public sealed class SystemUpdateOptions
{
    public const string SectionName = "SystemUpdate";

    /// <summary>
    /// The mailbox shared with the host updater (deploy/updater/), mounted from
    /// the host's NUBARCA_UPDATER_DIR. The api writes one request here; it never
    /// runs anything itself.
    /// </summary>
    public string Directory { get; set; } = "/var/lib/nubarca/updater";

    /// <summary>A status older than this means the updater is not running.</summary>
    public int HeartbeatStaleSeconds { get; set; } = 120;

    /// <summary>An install is confirmed only against a check this recent.</summary>
    public int ReportMaxAgeHours { get; set; } = 24;
}

public sealed record SystemUpdateRunDto(string? Action, string? Outcome, string? Message, string? Sha,
    bool Automatic, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt);

public sealed record SystemUpdateCommitDto(string Sha, string Subject);

public sealed record SystemUpdateComponentsDto(bool Backend, bool Frontend, bool PrintAgent,
    string? PrintAgentVersion, bool Tv, bool TvEnabled);

/// <summary>The last check, as the updater reported it: what the newest release would change here.</summary>
public sealed record SystemUpdateReportDto(
    string Outcome, string? Reason, DateTimeOffset? CheckedAt,
    string? CandidateSha, string? CandidateVersion, bool Signed,
    /// <summary>The exact release the check found; an install confirms this, not just the commit.</summary>
    string? ReleaseId,
    string? CurrentVersion, string? ApiVariant,
    SystemUpdateComponentsDto Components,
    IReadOnlyList<string> Migrations, bool ConfirmMigrations,
    IReadOnlyList<SystemUpdateCommitDto> Commits);

public sealed record SystemUpdateDto(
    /// <summary>The version and source commit this api runs.</summary>
    string? RunningVersion, string? RunningSha,
    /// <summary>The host updater is installed and alive.</summary>
    bool UpdaterConnected, string? State, DateTimeOffset? HeartbeatAt,
    bool RequestPending,
    SystemUpdateRunDto? Current, SystemUpdateRunDto? Last,
    DateTimeOffset? LastCheckAt, double? AutoCheckHours,
    SystemUpdateReportDto? Report);

public sealed record SystemUpdateApplyRequest(string? Sha, string? ReleaseId, bool ConfirmMigrations);

/// <summary>One validated request for the host updater, before it is handed over.</summary>
public sealed record SystemUpdateRequest(Guid Id, string Action, string? Sha, string? ReleaseId, bool ConfirmMigrations);

/// <summary>A request written beside the mailbox, not yet delivered (see <see cref="SystemUpdateService.Stage"/>).</summary>
public sealed record StagedSystemUpdateRequest(SystemUpdateRequest Request, string Path);

public enum SystemUpdateRequestError
{
    None,
    UpdaterUnavailable,
    Busy,
    CheckAgain,
    NotDeployable,
}

/// <summary>
/// The Updates page's view of the host updater, and the only thing it can do
/// to it: drop one validated request in the mailbox. Everything read back is
/// parsed into fixed fields — no raw report, path or log passes through.
/// </summary>
public sealed partial class SystemUpdateService
{
    private const long MaxFileBytes = 256 * 1024;
    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex ReleaseId();
    [GeneratedRegex("^[0-9a-f]{40}$", RegexOptions.CultureInvariant)]
    private static partial Regex Commit();
    [GeneratedRegex("^[0-9a-f]{7,40}$", RegexOptions.CultureInvariant)]
    private static partial Regex ShortCommit();
    [GeneratedRegex(@"^\d{1,4}\.\d{1,4}\.\d{1,4}$", RegexOptions.CultureInvariant)]
    private static partial Regex Version();
    [GeneratedRegex("^[0-9]{14}_[A-Za-z0-9_]{1,150}$", RegexOptions.CultureInvariant)]
    private static partial Regex MigrationId();
    [GeneratedRegex("^[a-z-]{1,40}$", RegexOptions.CultureInvariant)]
    private static partial Regex Word();

    private readonly SystemUpdateOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<SystemUpdateService> _logger;

    public SystemUpdateService(IOptions<SystemUpdateOptions> options, TimeProvider time,
        ILogger<SystemUpdateService> logger)
    {
        _options = options.Value;
        _time = time;
        _logger = logger;
    }

    private string StatusPath => Path.Combine(_options.Directory, "status.json");
    private string ReportPath => Path.Combine(_options.Directory, "report.json");
    private string RequestPath => Path.Combine(_options.Directory, "request.json");

    public static string? RunningVersion =>
        typeof(SystemUpdateService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion.Split('+')[0] is { } value && Version().IsMatch(value) ? value : null;

    public static string? RunningSha =>
        Environment.GetEnvironmentVariable("NUBARCA_GIT_SHA") is { } sha && Commit().IsMatch(sha) ? sha : null;

    public SystemUpdateDto Describe()
    {
        var status = Read(StatusPath);
        var heartbeat = Time(status, "heartbeatAt");
        var connected = heartbeat is not null
            && _time.GetUtcNow() - heartbeat.Value < TimeSpan.FromSeconds(_options.HeartbeatStaleSeconds);
        var state = Text(status, "state") is { } value && Word().IsMatch(value) ? value : null;
        return new SystemUpdateDto(
            RunningVersion, RunningSha,
            connected, connected ? state : null, heartbeat,
            File.Exists(RequestPath),
            Run(status, "current"), Run(status, "last"),
            Time(status, "lastCheckAt"),
            status is { } s && s.TryGetProperty("autoCheckHours", out var hours) && hours.TryGetDouble(out var h) ? h : null,
            Report(Read(ReportPath)));
    }

    /// <summary>
    /// A check, ready to hand over: <paramref name="request"/> is set only when
    /// nothing refuses it. The caller stages it (<see cref="Stage"/>), records
    /// it in the audit trail, and only then delivers it (<see cref="Deliver"/>):
    /// no request reaches the updater without its record.
    /// </summary>
    public SystemUpdateRequestError PrepareCheck(out SystemUpdateRequest? request)
    {
        request = null;
        var error = CanRequest(Describe());
        if (error != SystemUpdateRequestError.None) return error;
        request = new SystemUpdateRequest(Guid.NewGuid(), "check", null, null, false);
        return SystemUpdateRequestError.None;
    }

    /// <summary>
    /// An install is confirmed against the last check: the same commit, the
    /// same exact release (its manifest and TV bundles), the same migration
    /// answer, from a check recent enough to still describe it. Anything else
    /// asks for a new check — the updater would refuse it too.
    /// </summary>
    public SystemUpdateRequestError PrepareApply(SystemUpdateApplyRequest body, out SystemUpdateRequest? request)
    {
        request = null;
        var described = Describe();
        var error = CanRequest(described);
        if (error != SystemUpdateRequestError.None) return error;
        var report = described.Report;
        if (report is null || body.Sha is null || !Commit().IsMatch(body.Sha)
            || report.CandidateSha != body.Sha || report.ConfirmMigrations != body.ConfirmMigrations
            || report.ReleaseId is null || body.ReleaseId != report.ReleaseId
            || report.CheckedAt is null
            || _time.GetUtcNow() - report.CheckedAt.Value > TimeSpan.FromHours(_options.ReportMaxAgeHours))
            return SystemUpdateRequestError.CheckAgain;
        if (report.Outcome != "ready") return SystemUpdateRequestError.NotDeployable;
        request = new SystemUpdateRequest(Guid.NewGuid(), "apply", body.Sha, body.ReleaseId, body.ConfirmMigrations);
        return SystemUpdateRequestError.None;
    }

    /// <summary>
    /// Writes the request beside the mailbox under a name the updater ignores:
    /// everything that can fail on the way to the mailbox fails here, before
    /// the audit record is written.
    /// </summary>
    public SystemUpdateRequestError Stage(SystemUpdateRequest request, out StagedSystemUpdateRequest? staged)
    {
        staged = null;
        if (File.Exists(RequestPath)) return SystemUpdateRequestError.Busy;
        var path = Path.Combine(_options.Directory, $".request-{request.Id:N}.tmp");
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(request.Action == "apply"
                ? new
                {
                    id = request.Id.ToString("D"), action = "apply", sha = request.Sha, release = request.ReleaseId,
                    confirmMigrations = request.ConfirmMigrations,
                }
                : (object)new { id = request.Id.ToString("D"), action = "check" }));
            staged = new StagedSystemUpdateRequest(request, path);
            return SystemUpdateRequestError.None;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError("The updater mailbox could not be written ({ExceptionType}).", ex.GetType().Name);
            Discard(path);
            return SystemUpdateRequestError.UpdaterUnavailable;
        }
    }

    /// <summary>
    /// Delivers a staged request with one rename in the same directory, never
    /// over a pending request: the first one wins, the second is told to wait.
    /// </summary>
    public SystemUpdateRequestError Deliver(StagedSystemUpdateRequest staged)
    {
        try
        {
            File.Move(staged.Path, RequestPath, overwrite: false);
            return SystemUpdateRequestError.None;
        }
        catch (IOException) when (File.Exists(RequestPath))
        {
            return SystemUpdateRequestError.Busy;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError("The updater mailbox could not be written ({ExceptionType}).", ex.GetType().Name);
            return SystemUpdateRequestError.UpdaterUnavailable;
        }
        finally
        {
            Discard(staged.Path);
        }
    }

    /// <summary>A staged request that will not be delivered.</summary>
    public void Discard(StagedSystemUpdateRequest staged) => Discard(staged.Path);

    private static void Discard(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static SystemUpdateRequestError CanRequest(SystemUpdateDto described)
    {
        if (!described.UpdaterConnected) return SystemUpdateRequestError.UpdaterUnavailable;
        if (described.RequestPending || described.State is "checking" or "applying") return SystemUpdateRequestError.Busy;
        return SystemUpdateRequestError.None;
    }

    private JsonElement? Read(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxFileBytes) return null;
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            return document.RootElement.ValueKind == JsonValueKind.Object ? document.RootElement.Clone() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonElement? element, string name) =>
        element is { } e && e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool Flag(JsonElement? element, string name) =>
        element is { } e && e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.True;

    private static JsonElement? Child(JsonElement? element, string name) =>
        element is { } e && e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Object ? value : null;

    private static DateTimeOffset? Time(JsonElement? element, string name) =>
        DateTimeOffset.TryParse(Text(element, name), System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : null;

    private static string? Bounded(string? value, int max) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Length <= max ? value : value[..max];

    private static string? Matching(string? value, Regex pattern) => value is not null && pattern.IsMatch(value) ? value : null;

    private static SystemUpdateRunDto? Run(JsonElement? status, string name)
    {
        var run = Child(status, name);
        if (run is null) return null;
        return new SystemUpdateRunDto(
            Matching(Text(run, "action"), Word()),
            Matching(Text(run, "outcome"), Word()),
            Bounded(Text(run, "message"), 300),
            Matching(Text(run, "sha"), Commit()),
            Flag(run, "automatic"),
            Time(run, "startedAt"), Time(run, "finishedAt"));
    }

    private static SystemUpdateReportDto? Report(JsonElement? report)
    {
        var outcome = Matching(Text(report, "outcome"), Word());
        if (report is not { } r || outcome is null) return null;
        var candidate = Child(r, "candidate");
        var current = Child(r, "current");
        var components = Child(r, "components");
        var printAgent = Child(components, "printAgent");
        var tv = Child(components, "tv");
        var migrations = r.TryGetProperty("migrations", out var m) && m.ValueKind == JsonValueKind.Array
            ? m.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!)
                .Where(x => MigrationId().IsMatch(x)).Take(100).ToArray()
            : [];
        var commits = r.TryGetProperty("commits", out var c) && c.ValueKind == JsonValueKind.Array
            ? c.EnumerateArray().Select(x => (Sha: Matching(Text(x, "sha"), ShortCommit()), Subject: Bounded(Text(x, "subject"), 200)))
                .Where(x => x.Sha is not null && x.Subject is not null)
                .Select(x => new SystemUpdateCommitDto(x.Sha!, x.Subject!)).Take(50).ToArray()
            : [];
        return new SystemUpdateReportDto(
            outcome, Matching(Text(r, "reason"), Word()), Time(r, "finishedAt"),
            Matching(Text(candidate, "sha"), Commit()), Matching(Text(candidate, "version"), Version()),
            Flag(candidate, "signed"), Matching(Text(candidate, "releaseId"), ReleaseId()),
            Matching(Text(current, "version"), Version()), Matching(Text(current, "apiVariant"), Word()),
            new SystemUpdateComponentsDto(
                Flag(Child(components, "backend"), "changed"), Flag(Child(components, "frontend"), "changed"),
                Flag(printAgent, "changed"), Matching(Text(printAgent, "version"), Version()),
                Flag(tv, "changed"), Flag(tv, "enabled")),
            migrations, Flag(r, "confirmMigrations"), commits);
    }
}
