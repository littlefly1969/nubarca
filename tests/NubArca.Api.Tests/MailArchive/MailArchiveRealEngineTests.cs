using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Files;
using NubArca.Api.Jobs;
using NubArca.Api.MailArchive;
using NubArca.Api.Storage;
using NubArca.Api.Tests.Endpoints;
using Xunit;

namespace NubArca.Api.Tests.MailArchive;

// The .NET side against the REAL Rust engine (tools/nubarca-pst-importer) and
// its committed fixture PST. Skipped when the binary has not been built: CI's
// pst-importer job builds it and points NUBARCA_PST_IMPORTER_BIN at it;
// locally `cargo build --release` in tools/nubarca-pst-importer is enough.
public sealed class MailArchiveRealEngineTests : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "NubArca.sln")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new InvalidOperationException("could not locate the repository root");
    }

    private static string Fixture => Path.Combine(RepositoryRoot(), "tools", "nubarca-pst-importer", "tests", "fixtures", "basic.pst");

    private static string? Binary()
    {
        var configured = Environment.GetEnvironmentVariable("NUBARCA_PST_IMPORTER_BIN");
        var candidate = !string.IsNullOrWhiteSpace(configured)
            ? configured
            : Path.Combine(RepositoryRoot(), "tools", "nubarca-pst-importer", "target", "release", "nubarca-pst-importer");
        return File.Exists(candidate) ? candidate : null;
    }

    private static string RequireBinary()
    {
        var binary = Binary();
        Skip.If(binary is null, "nubarca-pst-importer is not built; run `cargo build --release` in tools/nubarca-pst-importer.");
        return binary!;
    }

    private string TempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nubarca-mail-real-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        _disposables.Add(new DeleteOnDispose(path));
        return path;
    }

    [SkippableFact]
    public async Task Process_engine_speaks_the_protocol_with_the_real_binary()
    {
        var binary = RequireBinary();
        var engine = new ProcessPstEngine(
            Options.Create(new MailArchiveOptions { ImporterPath = binary }), NullLogger<ProcessPstEngine>.Instance);
        Assert.True(engine.IsAvailable);

        var probe = await engine.ProbeAsync(Fixture, CancellationToken.None);
        Assert.Equal(PstProtocol.Version, probe.Protocol);
        Assert.Equal("unicode", probe.Format);

        var events = new List<PstListEvent>();
        await foreach (var e in engine.ListAsync(Fixture, CancellationToken.None))
        {
            events.Add(e);
        }
        Assert.Equal(8, events.Count(e => e.Type == "folder"));
        var messages = events.Where(e => e.Type == "message").Select(e => e.MessageId!).ToList();
        Assert.Equal(5, messages.Count);
        Assert.Equal("end", events[^1].Type);

        var work = TempDirectory();
        await using var session = await engine.StartSessionAsync(Fixture, work, CancellationToken.None);
        var response = await session.ExtractAsync("req-0001", messages[1], CancellationToken.None);
        Assert.Equal("extracted", response.Type);
        Assert.Equal(2, response.Attachments!.Count);
        foreach (var file in response.Attachments)
        {
            var path = MailWorkspace.ResolveInside(work, "req-0001", file.File);
            Assert.Equal(file.Sha256, Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(path))));
        }
        var missing = await session.ExtractAsync("req-0002", "m:7fffffff", CancellationToken.None);
        Assert.Equal("error", missing.Type);
    }

    [SkippableFact]
    public async Task Full_import_of_the_fixture_through_the_api_with_the_real_binary()
    {
        var binary = RequireBinary();
        var fixtureBytes = await File.ReadAllBytesAsync(Fixture);
        var factory = new SqliteWebApplicationFactory(
            new Dictionary<string, string?> { ["MailArchive:ImporterPath"] = binary }, poolHost: false);
        _disposables.Add(factory);
        factory.EnsureDatabaseCreated();
        var (owner, client) = await factory.CreateAuthenticatedClientAsync();

        var status = await client.GetFromJsonAsync<MailStatusDto>("/api/mail/status", Json);
        Assert.True(status!.Available);

        var part = new ByteArrayContent(fixtureBytes);
        part.Headers.ContentType = new MediaTypeHeaderValue("application/vnd.ms-outlook");
        var upload = await client.PostAsync("/api/files", new MultipartFormDataContent { { part, "file", "basic.pst" } });
        upload.EnsureSuccessStatusCode();
        var pst = (await upload.Content.ReadFromJsonAsync<FileSummary>())!;
        var start = await client.PostAsJsonAsync("/api/mail/archives", new { fileItemId = pst.Id });
        Assert.Equal(HttpStatusCode.Accepted, start.StatusCode);
        var started = (await start.Content.ReadFromJsonAsync<MailStartImportResponse>(Json))!;

        for (var i = 0; i < 200; i++)
        {
            using var scope = factory.Services.CreateScope();
            if (await scope.ServiceProvider.GetRequiredService<JobProcessor>().ProcessAvailableAsync(10) == 0)
            {
                break;
            }
        }

        var archive = (await client.GetFromJsonAsync<MailArchiveDto>($"/api/mail/archives/{started.Archive.Id}", Json))!;
        Assert.Equal("succeeded", archive.LatestRun!.Status);
        Assert.Equal(5, archive.MessageCount);
        Assert.Equal(8, archive.FolderCount);
        Assert.Equal("basic", archive.DisplayName); // named after the owner's file, not the PST's internal store name

        var page = (await client.GetFromJsonAsync<MailMessagePage>($"/api/mail/messages?archiveId={archive.Id}&q=münchen", Json))!;
        var international = Assert.Single(page.Items);
        var detail = (await client.GetFromJsonAsync<MailMessageDetailDto>($"/api/mail/messages/{international.Id}", Json))!;
        Assert.Equal("résumé-Übersicht-Ωμέγα-日本語-🎉.txt", detail.Attachments.Single().Name);

        var html = (await client.GetFromJsonAsync<MailMessagePage>($"/api/mail/messages?archiveId={archive.Id}&subject=inline", Json))!;
        var inline = (await client.GetFromJsonAsync<MailMessageDetailDto>($"/api/mail/messages/{html.Items.Single().Id}", Json))!;
        var logo = inline.Attachments.Single(a => a.Name == "logo.png");
        Assert.Contains($"/api/files/{logo.FileItemId}/content", inline.Body.Html);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var files = await db.FileItems.AsNoTracking()
                .Where(f => f.OwnerUserId == owner && f.DeletedAt == null && f.Name != "basic.pst")
                .Select(f => f.Name).ToListAsync();
            // logo.png, report.pdf, notes.txt twice, empty.txt, data.bin and the international one.
            Assert.Equal(7, files.Count);
            Assert.Contains("notes (2).txt", files);
        }

        // The engine opened its copy read-only: the uploaded original is byte-identical.
        var original = await client.GetByteArrayAsync($"/api/files/{pst.Id}/content");
        Assert.Equal(fixtureBytes, original);

        using (var scope = factory.Services.CreateScope())
        {
            var audit = await scope.ServiceProvider.GetRequiredService<BlobReferenceAuditService>().AuditAsync();
            Assert.Equal(audit.TotalBlobs, audit.MatchedReferenceCount);
        }
    }

    private sealed class DeleteOnDispose(string path) : IDisposable
    {
        public void Dispose()
        {
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
