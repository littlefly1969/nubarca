using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Audit;
using NubArca.Api.Data;
using NubArca.Api.Tests.Endpoints;

namespace NubArca.Api.Tests.Admin;

/// <summary>
/// The Updates page talks to the host updater only through its mailbox: it
/// reads a status it parses into fixed fields, and drops one validated request.
/// An install is confirmed against the last check — the same release and the
/// same migration answer — and only while the updater is alive and idle.
/// </summary>
public sealed class SystemUpdateTests : IDisposable
{
    private const string Candidate = "0123456789abcdef0123456789abcdef01234567";
    private readonly string _mailbox = Path.Combine(Path.GetTempPath(), $"nubarca-updater-{Guid.NewGuid():N}");
    private readonly SqliteWebApplicationFactory _factory;

    public SystemUpdateTests()
    {
        Directory.CreateDirectory(_mailbox);
        _factory = new SqliteWebApplicationFactory(new Dictionary<string, string?>
        {
            ["SystemUpdate:Directory"] = _mailbox,
        });
        _factory.EnsureDatabaseCreated();
    }

    public void Dispose()
    {
        _factory.Dispose();
        if (Directory.Exists(_mailbox)) Directory.Delete(_mailbox, recursive: true);
    }

    private static string Now(TimeSpan offset = default) =>
        DateTimeOffset.UtcNow.Add(offset).ToString("yyyy-MM-ddTHH:mm:ssK");

    private void Status(string state = "idle", TimeSpan? heartbeatAge = null, object? last = null) =>
        File.WriteAllText(Path.Combine(_mailbox, "status.json"), JsonSerializer.Serialize(new
        {
            schemaVersion = 1, state, heartbeatAt = Now(-(heartbeatAge ?? TimeSpan.Zero)),
            current = (object?)null, last, lastCheckAt = Now(), autoCheckHours = 6,
        }));

    private void Report(string outcome = "ready", string[]? migrations = null, TimeSpan? age = null) =>
        File.WriteAllText(Path.Combine(_mailbox, "report.json"), JsonSerializer.Serialize(new
        {
            schemaVersion = 1, mode = "check", outcome, reason = (string?)null,
            finishedAt = Now(-(age ?? TimeSpan.Zero)),
            candidate = new { sha = Candidate, version = "0.4.0", signed = true },
            current = new { apiSha = new string('1', 40), version = "0.3.0", apiVariant = "openvino",
                // Anything the report carries beyond the fixed fields never reaches the page.
                checkoutPath = "/srv/secret/checkout" },
            components = new
            {
                backend = new { changed = true }, frontend = new { changed = false },
                printAgent = new { changed = true, version = "0.9.0", served = true },
                tv = new { changed = false, enabled = false, ready = false },
            },
            migrations = migrations ?? [],
            confirmMigrations = (migrations ?? []).Length > 0,
            commits = new[] { new { sha = "0123456", subject = "feat: something" } },
        }));

    private async Task<HttpClient> AdminAsync()
    {
        var (id, client) = await _factory.CreateAuthenticatedClientAsync("admin@example.com");
        await _factory.PromoteToAdminAsync(id);
        return client;
    }

    private string? Request() =>
        File.Exists(Path.Combine(_mailbox, "request.json")) ? File.ReadAllText(Path.Combine(_mailbox, "request.json")) : null;

    [Fact]
    public async Task Without_A_Running_Updater_The_Page_Says_So_And_Nothing_Is_Requested()
    {
        var admin = await AdminAsync();
        var described = await admin.GetFromJsonAsync<JsonElement>("/api/admin/system-update");
        Assert.False(described.GetProperty("updaterConnected").GetBoolean());
        Assert.Equal(JsonValueKind.Null, described.GetProperty("report").ValueKind);

        var check = await admin.PostAsync("/api/admin/system-update/check", null);
        Assert.Equal(HttpStatusCode.Conflict, check.StatusCode);
        Assert.Equal("updater_unavailable", (await check.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());

        // A heartbeat older than the stale limit is the same as none.
        Status(heartbeatAge: TimeSpan.FromMinutes(10));
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync("/api/admin/system-update/check", null)).StatusCode);
        Assert.Null(Request());
    }

    [Fact]
    public async Task A_Check_Drops_One_Request_And_A_Second_Waits()
    {
        Status();
        var admin = await AdminAsync();
        Assert.Equal(HttpStatusCode.Accepted, (await admin.PostAsync("/api/admin/system-update/check", null)).StatusCode);
        var request = JsonDocument.Parse(Request()!).RootElement;
        Assert.Equal("check", request.GetProperty("action").GetString());
        Assert.True(Guid.TryParse(request.GetProperty("id").GetString(), out _));

        var second = await admin.PostAsync("/api/admin/system-update/check", null);
        Assert.Equal("update_busy", (await second.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await db.AuditLogs.CountAsync(a => a.Action == AuditActions.SystemUpdateCheck));
    }

    [Fact]
    public async Task The_Report_Reaches_The_Page_As_Fixed_Fields_Only()
    {
        Status(last: new { action = "check", outcome = "ready", message = "fine", startedAt = Now(), finishedAt = Now() });
        Report(migrations: ["20261015101023_AddPrintAgentSelfUpdate"]);
        var admin = await AdminAsync();
        var raw = await admin.GetStringAsync("/api/admin/system-update");
        Assert.DoesNotContain("/srv/secret", raw);
        var described = JsonDocument.Parse(raw).RootElement;
        Assert.True(described.GetProperty("updaterConnected").GetBoolean());
        var report = described.GetProperty("report");
        Assert.Equal(Candidate, report.GetProperty("candidateSha").GetString());
        Assert.Equal("0.4.0", report.GetProperty("candidateVersion").GetString());
        Assert.True(report.GetProperty("signed").GetBoolean());
        Assert.True(report.GetProperty("components").GetProperty("printAgent").GetBoolean());
        Assert.Equal("0.9.0", report.GetProperty("components").GetProperty("printAgentVersion").GetString());
        Assert.True(report.GetProperty("confirmMigrations").GetBoolean());
        Assert.Single(report.GetProperty("commits").EnumerateArray());
        Assert.Equal("ready", described.GetProperty("last").GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task An_Install_Is_Confirmed_Against_The_Last_Check_Only()
    {
        Status();
        Report(migrations: ["20261015101023_AddPrintAgentSelfUpdate"]);
        var admin = await AdminAsync();

        async Task<string?> Refusal(object body)
        {
            var response = await admin.PostAsJsonAsync("/api/admin/system-update/apply", body);
            return response.StatusCode == HttpStatusCode.Accepted ? null
                : (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString();
        }

        Assert.Equal("check_again", await Refusal(new { sha = new string('f', 40), confirmMigrations = true }));
        // The migration answer must be the check's: an install never slips a migration past it.
        Assert.Equal("check_again", await Refusal(new { sha = Candidate, confirmMigrations = false }));
        Assert.Equal("check_again", await Refusal(new { sha = "main", confirmMigrations = true }));
        Assert.Null(Request());

        Assert.Null(await Refusal(new { sha = Candidate, confirmMigrations = true }));
        var request = JsonDocument.Parse(Request()!).RootElement;
        Assert.Equal("apply", request.GetProperty("action").GetString());
        Assert.Equal(Candidate, request.GetProperty("sha").GetString());
        Assert.True(request.GetProperty("confirmMigrations").GetBoolean());
    }

    [Fact]
    public async Task A_Stale_Or_Not_Deployable_Check_Installs_Nothing()
    {
        Status();
        var admin = await AdminAsync();
        Report(age: TimeSpan.FromDays(2));
        var stale = await admin.PostAsJsonAsync("/api/admin/system-update/apply", new { sha = Candidate, confirmMigrations = false });
        Assert.Equal("check_again", (await stale.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        Report(outcome: "not-deployable");
        var refused = await admin.PostAsJsonAsync("/api/admin/system-update/apply", new { sha = Candidate, confirmMigrations = false });
        Assert.Equal("not_deployable", (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        Assert.Null(Request());
    }

    [Fact]
    public async Task Nothing_Is_Requested_While_The_Updater_Is_Busy()
    {
        Status(state: "applying");
        Report();
        var admin = await AdminAsync();
        var busy = await admin.PostAsJsonAsync("/api/admin/system-update/apply", new { sha = Candidate, confirmMigrations = false });
        Assert.Equal("update_busy", (await busy.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Only_An_Administrator_Sees_Or_Asks()
    {
        Status();
        var (_, member) = await _factory.CreateAuthenticatedClientAsync("member@example.com");
        Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync("/api/admin/system-update")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PostAsync("/api/admin/system-update/check", null)).StatusCode);
        Assert.Null(Request());
    }
}
