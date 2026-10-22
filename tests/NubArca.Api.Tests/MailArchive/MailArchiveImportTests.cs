using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Domain.Mail;
using NubArca.Api.Files;
using NubArca.Api.Jobs;
using NubArca.Api.MailArchive;
using NubArca.Api.Storage;
using NubArca.Api.Tests.Endpoints;
using Xunit;
using Xunit.Abstractions;

namespace NubArca.Api.Tests.MailArchive;

// MAILARCHIVE-01 backend acceptance: import, two views' data, structured
// search, deletion with tombstones, re-import idempotency, crash recovery,
// cancellation and resume, immutability, owner isolation and blob accounting.
// Runs the real HTTP stack, FileItem/blob services and job processor; the
// Rust engine is replaced by a contract-faithful fake (FakePstEngine).
public sealed class MailArchiveImportTests : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly List<SqliteWebApplicationFactory> _factories = [];
    private readonly ITestOutputHelper _output;

    public MailArchiveImportTests(ITestOutputHelper output) => _output = output;

    public void Dispose()
    {
        foreach (var factory in _factories)
        {
            factory.Dispose();
        }
    }

    private SqliteWebApplicationFactory NewFactory(FakePstEngine engine, Action<IServiceCollection>? extra = null,
        Dictionary<string, string?>? settings = null)
    {
        // Never pooled: each test swaps in its own engine, and a pooled host
        // would keep the previous test's registrations.
        var factory = new SqliteWebApplicationFactory(settings ?? new Dictionary<string, string?>(), poolHost: false);
        factory.ConfigureExtraServices = services =>
        {
            services.RemoveAll<IPstEngine>();
            services.AddSingleton<IPstEngine>(engine);
            extra?.Invoke(services);
        };
        factory.EnsureDatabaseCreated();
        _factories.Add(factory);
        return factory;
    }

    private static async Task<Guid> UploadPstAsync(HttpClient client, string name = "archivio.pst", string body = "payload")
    {
        var bytes = FakePstEngine.Bytes("!BDN" + body);
        var part = new ByteArrayContent(bytes);
        part.Headers.ContentType = new MediaTypeHeaderValue("application/vnd.ms-outlook");
        var multipart = new MultipartFormDataContent { { part, "file", name } };
        var response = await client.PostAsync("/api/files", multipart);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<FileSummary>())!.Id;
    }

    private static async Task RunJobsAsync(SqliteWebApplicationFactory factory, int maxRounds = 200)
    {
        for (var i = 0; i < maxRounds; i++)
        {
            using var scope = factory.Services.CreateScope();
            var processor = scope.ServiceProvider.GetRequiredService<JobProcessor>();
            if (await processor.ProcessAvailableAsync(10) == 0)
            {
                break;
            }
        }
    }

    private static async Task<MailStartImportResponse> StartAsync(HttpClient client, Guid fileItemId)
    {
        var response = await client.PostAsJsonAsync("/api/mail/archives", new { fileItemId });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<MailStartImportResponse>(Json))!;
    }

    private static async Task<T> GetAsync<T>(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<T>(Json))!;
    }

    private static async Task<T> InDbAsync<T>(SqliteWebApplicationFactory factory, Func<AppDbContext, Task<T>> work)
    {
        using var scope = factory.Services.CreateScope();
        return await work(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    private static async Task<BlobReferenceAuditReport> AuditAsync(SqliteWebApplicationFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<BlobReferenceAuditService>().AuditAsync();
    }

    private static async Task<(SqliteWebApplicationFactory Factory, HttpClient Client, Guid Owner, MailArchiveDto Archive)> ImportedAsync(
        MailArchiveImportTests tests, FakePstEngine? engine = null)
    {
        var factory = tests.NewFactory(engine ?? FakePstEngine.Sample());
        var (owner, client) = await factory.CreateAuthenticatedClientAsync();
        var pst = await UploadPstAsync(client);
        var started = await StartAsync(client, pst);
        await RunJobsAsync(factory);
        var archive = await GetAsync<MailArchiveDto>(client, $"/api/mail/archives/{started.Archive.Id}");
        return (factory, client, owner, archive);
    }

    private static async Task<List<T>> ItemsAsync<T>(HttpClient client, string url)
    {
        var doc = await GetAsync<JsonElement>(client, url);
        return doc.GetProperty("items").Deserialize<List<T>>(Json)!;
    }

    // ---- import, the mail view and the document view ------------------------------

    [Fact]
    public async Task Import_creates_folders_messages_and_attachment_files()
    {
        var (factory, client, owner, archive) = await ImportedAsync(this);

        Assert.Equal("succeeded", archive.LatestRun!.Status);
        Assert.Equal(4, archive.LatestRun.TotalMessages);
        Assert.Equal(4, archive.LatestRun.ImportedMessages);
        Assert.Equal(0, archive.LatestRun.FailedMessages);
        Assert.Equal(4, archive.MessageCount);
        Assert.Equal("unicode", archive.Format);
        Assert.True(archive.SourceAvailable);

        var folders = await ItemsAsync<MailFolderDto>(client, $"/api/mail/archives/{archive.Id}/folders");
        Assert.Equal(4, folders.Count);
        var inbox = folders.Single(f => f.Name == "Posta in arrivo");
        var projects = folders.Single(f => f.Name == "Progetti");
        Assert.Equal(inbox.Id, projects.ParentId);
        Assert.Equal(2, inbox.MessageCount);

        // Newest first, keyset pages.
        var first = await GetAsync<MailMessagePage>(client, $"/api/mail/messages?archiveId={archive.Id}&limit=2");
        Assert.Equal(2, first.Items.Count);
        Assert.NotNull(first.NextCursor);
        var second = await GetAsync<MailMessagePage>(client,
            $"/api/mail/messages?archiveId={archive.Id}&limit=2&cursor={Uri.EscapeDataString(first.NextCursor!)}");
        Assert.Equal(2, second.Items.Count);
        Assert.Null(second.NextCursor);
        var all = first.Items.Concat(second.Items).ToList();
        Assert.Equal(4, all.Select(m => m.Id).Distinct().Count());
        Assert.True(all.Zip(all.Skip(1)).All(p => p.First.Date >= p.Second.Date));

        // The original files are NubArca files under Posta/<archive>/<folder path>.
        var files = await InDbAsync(factory, db => db.FileItems.AsNoTracking()
            .Where(f => f.OwnerUserId == owner && f.DeletedAt == null && !f.Name.EndsWith(".pst"))
            .ToListAsync());
        Assert.Equal(5, files.Count); // report.pdf, logo.png, report (00000101-2).pdf, dentro.txt, foto.jpg
        Assert.Contains(files, f => f.Name == "report (00000101-2).pdf");
        var folderNames = await InDbAsync(factory, db => db.Folders.AsNoTracking()
            .Where(f => f.OwnerUserId == owner).Select(f => f.Name).ToListAsync());
        Assert.Contains("Posta", folderNames);
        Assert.Contains("archivio", folderNames);
        Assert.Contains("Progetti", folderNames);

        // Same bytes in two messages: one physical blob, two FileItems.
        var report = files.Single(f => f.Name == "report.pdf");
        var photo = files.Single(f => f.Name == "foto.jpg");
        Assert.Equal(report.BlobObjectId, photo.BlobObjectId);

        var audit = await AuditAsync(factory);
        Assert.Equal(audit.TotalBlobs, audit.MatchedReferenceCount);
    }

    [Fact]
    public async Task Message_detail_has_headers_sanitized_html_and_attachment_states()
    {
        var (_, client, _, archive) = await ImportedAsync(this);
        var page = await GetAsync<MailMessagePage>(client, $"/api/mail/messages?archiveId={archive.Id}&subject=trimestrale");
        var original = page.Items.Single(m => m.Subject == "Relazione trimestrale");
        var detail = await GetAsync<MailMessageDetailDto>(client, $"/api/mail/messages/{original.Id}");

        Assert.Equal("Ada Example", detail.From!.Name);
        Assert.Equal("bob@example.test", detail.To.Single().Address);
        Assert.Equal("carla@example.test", detail.Cc.Single().Address);
        Assert.Equal("dario@example.test", detail.Bcc.Single().Address);
        Assert.Equal(["Top of Outlook data file", "Posta in arrivo"], detail.FolderPath);
        Assert.Equal("archivio", detail.ArchiveName);

        Assert.Equal("html", detail.Body.Kind);
        var html = detail.Body.Html!;
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("alert", html);
        Assert.DoesNotContain("onclick", html);
        Assert.DoesNotContain("color:red", html);
        Assert.DoesNotContain("tracker.example.test", html);
        Assert.DoesNotContain("javascript:", html);
        Assert.Contains("<b>Bob</b>", html);

        Assert.Equal(3, detail.Attachments.Count);
        Assert.All(detail.Attachments, a => Assert.Equal(MailAttachmentStates.Available, a.State));
        var logo = detail.Attachments.Single(a => a.Name == "logo.png");
        Assert.True(logo.Inline);
        Assert.Contains($"src=\"/api/files/{logo.FileItemId}/content\"", html);
        Assert.Equal(["report.pdf", "logo.png", "report.pdf"], detail.Attachments.Select(a => a.Name));
        Assert.Equal("report (00000101-2).pdf", detail.Attachments[2].CurrentFileName);

        // Attachments open through the ordinary file endpoint.
        var content = await client.GetAsync($"/api/files/{detail.Attachments[0].FileItemId}/content");
        Assert.Equal(HttpStatusCode.OK, content.StatusCode);
        Assert.Equal("pdf-one", await content.Content.ReadAsStringAsync());

        // Embedded message and unsupported attachment.
        var forwarded = (await GetAsync<MailMessagePage>(client, $"/api/mail/messages?archiveId={archive.Id}&subject=incorporato"))
            .Items.Single();
        var fwd = await GetAsync<MailMessageDetailDto>(client, $"/api/mail/messages/{forwarded.Id}");
        Assert.Equal("Originale", fwd.EmbeddedMessages.Single().Subject);
        var inner = fwd.Attachments.Single(a => a.Name == "dentro.txt");
        Assert.Equal("0", inner.EmbeddedPath);
        Assert.Equal(MailAttachmentStates.Available, inner.State);
        var ole = fwd.Attachments.Single(a => a.Name == "contratto.docx");
        Assert.Equal(MailAttachmentStates.NotExtracted, ole.State);
        Assert.Equal("unsupported_method", ole.SkipReason);
        Assert.Null(ole.FileItemId);
    }

    [Fact]
    public async Task Structured_search_by_sender_recipient_subject_and_dates()
    {
        var (_, client, _, archive) = await ImportedAsync(this);
        async Task<List<string?>> Subjects(string query) =>
            (await GetAsync<MailMessagePage>(client, $"/api/mail/messages?archiveId={archive.Id}&{query}"))
                .Items.Select(m => m.Subject).ToList();

        Assert.Equal(2, (await Subjects("from=ada%40example")).Count);
        Assert.Single(await Subjects("from=carla"));
        Assert.Equal(2, (await Subjects("to=ada%40example.test")).Count); // bob's message and carla's
        Assert.Single(await Subjects("to=dario")); // Bcc counts as a recipient
        Assert.Equal(2, (await Subjects("subject=RELAZIONE")).Count);
        Assert.Single(await Subjects("q=日本語"));
        // The body is searched too: its text, and embedded messages' text.
        Assert.Equal(["Senza allegati — àèìòù 日本語"], await Subjects("q=solo%20TESTO"));
        Assert.Equal(["Inoltro con messaggio incorporato"], await Subjects("q=testo%20originale"));
        Assert.Empty(await Subjects("q=testo%20inesistente"));
        Assert.Equal(2, (await Subjects("after=2024-03-01T00:00:00Z&before=2024-03-03T00:00:00Z")).Count);
        Assert.Single(await Subjects("hasAttachments=false"));

        var bad = await client.GetAsync("/api/mail/messages?cursor=not-a-cursor");
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var limit = await client.GetAsync("/api/mail/messages?limit=1000");
        Assert.Equal(HttpStatusCode.BadRequest, limit.StatusCode);
    }

    [Fact]
    public async Task Attachment_file_leads_back_to_its_message_even_after_rename_and_move()
    {
        var (factory, client, owner, archive) = await ImportedAsync(this);
        var file = await InDbAsync(factory, db => db.FileItems.AsNoTracking()
            .FirstAsync(f => f.OwnerUserId == owner && f.Name == "logo.png"));

        var origins = await ItemsAsync<MailFileOriginDto>(client, $"/api/mail/file-origins/{file.Id}");
        var origin = Assert.Single(origins);
        Assert.Equal("Relazione trimestrale", origin.Subject);
        Assert.Equal("archivio", origin.ArchiveName);
        Assert.Equal("logo.png", origin.AttachmentName);

        // Rename and move through the ordinary file routes: still linked.
        Assert.Equal(HttpStatusCode.OK,
            (await client.PatchAsJsonAsync($"/api/files/{file.Id}/rename", new { name = "marchio.png" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await client.PatchAsJsonAsync($"/api/files/{file.Id}/move", new { parentFolderId = (Guid?)null })).StatusCode);
        Assert.Single(await ItemsAsync<MailFileOriginDto>(client, $"/api/mail/file-origins/{file.Id}"));
        var detail = await GetAsync<MailMessageDetailDto>(client, $"/api/mail/messages/{origin.MessageId}");
        var logo = detail.Attachments.Single(a => a.FileItemId == file.Id);
        Assert.Equal("marchio.png", logo.CurrentFileName);
        Assert.Equal("logo.png", logo.Name);

        // Deleted from NubArca: the message says so and never resurrects it.
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/files/{file.Id}")).StatusCode);
        detail = await GetAsync<MailMessageDetailDto>(client, $"/api/mail/messages/{origin.MessageId}");
        logo = detail.Attachments.Single(a => a.Name == "logo.png");
        Assert.Equal(MailAttachmentStates.FileDeleted, logo.State);
        Assert.Null(logo.FileItemId);
        Assert.DoesNotContain("/api/files/", detail.Body.Html!);
        Assert.Empty(await ItemsAsync<MailFileOriginDto>(client, $"/api/mail/file-origins/{file.Id}"));
    }

    // ---- deletion, tombstones, re-import ---------------------------------------------

    [Fact]
    public async Task Deleting_a_message_keeps_its_attachments_and_reimport_does_not_restore_it()
    {
        var engine = FakePstEngine.Sample();
        var (factory, client, owner, archive) = await ImportedAsync(this, engine);
        var message = (await GetAsync<MailMessagePage>(client, $"/api/mail/messages?archiveId={archive.Id}&subject=trimestrale"))
            .Items.Single(m => m.Subject == "Relazione trimestrale");
        var detail = await GetAsync<MailMessageDetailDto>(client, $"/api/mail/messages/{message.Id}");
        var attachmentFiles = detail.Attachments.Select(a => a.FileItemId!.Value).ToList();
        var documentBlob = await InDbAsync(factory, db => db.MailMessages.Where(m => m.Id == message.Id)
            .Select(m => m.BlobObjectId!.Value).FirstAsync());
        var refsBefore = await InDbAsync(factory, db => db.BlobObjects.AsNoTracking()
            .Where(b => db.FileItems.Where(f => attachmentFiles.Contains(f.Id)).Select(f => f.BlobObjectId).Contains(b.Id))
            .ToDictionaryAsync(b => b.Id, b => b.ReferenceCount));

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/mail/messages/{message.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/mail/messages/{message.Id}")).StatusCode);
        // Its searchable text leaves with it.
        Assert.False(await InDbAsync(factory, db => db.MailMessageTexts.AnyAsync(t => t.MessageId == message.Id)));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/mail/messages/{message.Id}")).StatusCode);
        Assert.DoesNotContain((await GetAsync<MailMessagePage>(client, $"/api/mail/messages?archiveId={archive.Id}")).Items,
            m => m.Id == message.Id);

        // Attachments: same files, same content, same blob references.
        foreach (var fileId in attachmentFiles)
        {
            var content = await client.GetAsync($"/api/files/{fileId}/content");
            Assert.Equal(HttpStatusCode.OK, content.StatusCode);
            Assert.Empty(await ItemsAsync<MailFileOriginDto>(client, $"/api/mail/file-origins/{fileId}"));
        }
        var refsAfter = await InDbAsync(factory, db => db.BlobObjects.AsNoTracking()
            .Where(b => refsBefore.Keys.Contains(b.Id)).ToDictionaryAsync(b => b.Id, b => b.ReferenceCount));
        Assert.Equal(refsBefore, refsAfter);

        // The document's reference was released (and nothing else was).
        var documentRefs = await InDbAsync(factory, db => db.BlobObjects.Where(b => b.Id == documentBlob).Select(b => b.ReferenceCount).FirstAsync());
        Assert.Equal(0, documentRefs);
        var audit = await AuditAsync(factory);
        Assert.Equal(audit.TotalBlobs, audit.MatchedReferenceCount);

        var tombstone = await InDbAsync(factory, db => db.MailDeletionTombstones.AsNoTracking().SingleAsync());
        Assert.Equal(owner, tombstone.OwnerUserId);

        // Re-import the very same PST: nothing re-created, the deleted one stays deleted.
        var reimport = await client.PostAsync($"/api/mail/archives/{archive.Id}/import", null);
        Assert.Equal(HttpStatusCode.Accepted, reimport.StatusCode);
        var extractionsBefore = engine.Extractions;
        await RunJobsAsync(factory);
        var after = await GetAsync<MailArchiveDto>(client, $"/api/mail/archives/{archive.Id}");
        Assert.Equal("succeeded", after.LatestRun!.Status);
        Assert.Equal(0, after.LatestRun.ImportedMessages);
        Assert.Equal(3, after.LatestRun.AlreadyPresentMessages);
        Assert.Equal(1, after.LatestRun.SkippedDeletedMessages);
        Assert.Equal(3, after.MessageCount);
        Assert.Equal(extractionsBefore, engine.Extractions); // nothing extracted again
        Assert.Equal(1, engine.Lists); // the manifest was not rebuilt
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/mail/messages/{message.Id}")).StatusCode);
        var fileCount = await InDbAsync(factory, db => db.FileItems.CountAsync(f => f.OwnerUserId == owner && f.DeletedAt == null));
        Assert.Equal(6, fileCount); // the PST + 5 attachments, never duplicated
    }

    [Fact]
    public async Task A_partial_scan_is_not_final_and_the_next_run_scans_again_after_the_source_is_fixed()
    {
        // Run 1: the "Progetti" folder cannot be enumerated, so its message is
        // missing from the manifest.
        var engine = FakePstEngine.Sample();
        engine.UnreadableFolders.Add("f:00000003");
        var (factory, client, _, archive) = await ImportedAsync(this, engine);

        var first = archive.LatestRun!;
        Assert.Equal("partial", first.Status);
        Assert.Equal("scan_errors", first.ErrorCode);
        Assert.Equal(1, first.ScanErrors);
        var issue = Assert.Single(first.ScanIssues);
        Assert.Equal("folder", issue.Scope);
        Assert.Equal("corrupt", issue.Code);
        Assert.Equal(["Top of Outlook data file", "Posta in arrivo", "Progetti"], issue.FolderPath);
        Assert.Equal(3, first.TotalMessages);
        Assert.Equal(3, first.ImportedMessages);
        Assert.Equal(0, first.FailedMessages);
        Assert.Equal(3, archive.MessageCount);
        Assert.Null(await InDbAsync(factory, db => db.MailArchives.Where(a => a.Id == archive.Id)
            .Select(a => a.ScanCompletedAt).FirstAsync()));

        // The source (here: the parser) is fixed; the re-import scans again
        // instead of trusting the partial manifest.
        engine.UnreadableFolders.Clear();
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsync($"/api/mail/archives/{archive.Id}/import", null)).StatusCode);
        await RunJobsAsync(factory);
        var fixedArchive = await GetAsync<MailArchiveDto>(client, $"/api/mail/archives/{archive.Id}");
        var second = fixedArchive.LatestRun!;
        Assert.Equal(2, engine.Lists);
        Assert.Equal("succeeded", second.Status);
        Assert.Null(second.ErrorCode);
        Assert.Equal(0, second.ScanErrors);
        Assert.Empty(second.ScanIssues);
        Assert.Equal(4, second.TotalMessages);
        Assert.Equal(1, second.ImportedMessages);
        Assert.Equal(3, second.AlreadyPresentMessages);
        Assert.Equal(4, fixedArchive.MessageCount);
        Assert.Single((await GetAsync<MailMessagePage>(client, $"/api/mail/messages?archiveId={archive.Id}&subject=incorporato")).Items);

        // The first run keeps its own record of what it could not read.
        var firstAgain = await GetAsync<MailRunDto>(client, $"/api/mail/runs/{first.Id}");
        Assert.Equal(1, firstAgain.ScanErrors);
        Assert.Single(firstAgain.ScanIssues);

        // Now the scan is exhaustive: a third run does not list again.
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsync($"/api/mail/archives/{archive.Id}/import", null)).StatusCode);
        await RunJobsAsync(factory);
        Assert.Equal(2, engine.Lists);
        var third = (await GetAsync<MailArchiveDto>(client, $"/api/mail/archives/{archive.Id}")).LatestRun!;
        Assert.Equal("succeeded", third.Status);
        Assert.Equal(4, third.AlreadyPresentMessages);
        var audit = await AuditAsync(factory);
        Assert.Equal(audit.TotalBlobs, audit.MatchedReferenceCount);
    }

    [SkippableFact]
    public async Task A_compromised_engine_cannot_make_the_worker_import_a_file_outside_its_staging()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "Engine output is guarded with Linux openat2/O_NOFOLLOW.");
        var outside = Directory.CreateTempSubdirectory("nubarca-mail-outside-").FullName;
        try
        {
            var secret = Path.Combine(outside, "secret.bin");
            await File.WriteAllTextAsync(secret, "readable only by the worker");
            File.SetAttributes(secret, FileAttributes.ReadOnly);
            var secretSha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(secret)));
            var decoy = Directory.CreateDirectory(Path.Combine(outside, "decoy")).FullName;

            var engine = FakePstEngine.Sample();
            engine.TamperOutput = (key, dir) =>
            {
                switch (key)
                {
                    case "m:00000101": // an attachment replaced by a link to the secret
                        File.Delete(Path.Combine(dir, "att-0.bin"));
                        File.CreateSymbolicLink(Path.Combine(dir, "att-0.bin"), secret);
                        break;
                    case "m:00000102": // the document itself a link
                        File.Delete(Path.Combine(dir, "message.json"));
                        File.CreateSymbolicLink(Path.Combine(dir, "message.json"), secret);
                        break;
                    case "m:00000103": // the whole request directory a link elsewhere
                        foreach (var f in Directory.EnumerateFiles(dir)) File.Copy(f, Path.Combine(decoy, Path.GetFileName(f)), overwrite: true);
                        Directory.Delete(dir, recursive: true);
                        Directory.CreateSymbolicLink(dir, decoy);
                        break;
                }
            };
            var (factory, client, owner, archive) = await ImportedAsync(this, engine);

            var run = archive.LatestRun!;
            Assert.Equal("partial", run.Status);
            Assert.Equal(3, run.FailedMessages);
            Assert.Equal(1, run.ImportedMessages); // m:00000104 is untouched
            var failures = await InDbAsync(factory, db => db.MailImportItems.AsNoTracking()
                .Where(i => i.Status == MailImportItemStatuses.Failed).Select(i => i.FailureCode).ToListAsync());
            Assert.All(failures, code => Assert.Equal(EngineFiles.UnsafeOutput, code));

            // The secret's bytes never became a blob, a file or a message.
            Assert.False(await InDbAsync(factory, db => db.BlobObjects.AnyAsync(b => b.Sha256 == secretSha)));
            Assert.Equal(1, archive.MessageCount);
            // Cleaning the staging removed the links, never what they pointed at.
            Assert.Equal("readable only by the worker", await File.ReadAllTextAsync(secret));
            Assert.True(File.GetAttributes(secret).HasFlag(FileAttributes.ReadOnly));
            Assert.True(File.Exists(Path.Combine(decoy, "message.json")));
            var audit = await AuditAsync(factory);
            Assert.Equal(audit.TotalBlobs, audit.MatchedReferenceCount);
        }
        finally
        {
            foreach (var f in Directory.EnumerateFiles(outside, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public async Task Many_same_named_attachments_in_one_folder_get_deterministic_names_without_a_prefix_scan()
    {
        const int count = 300;
        var engine = new FakePstEngine();
        engine.Folders.Add(new FakeFolder("f:00000001", null, "Posta in arrivo", ["Posta in arrivo"], 0));
        for (var i = 0; i < count; i++)
        {
            var id = $"m:{0x10000 + i * 0x20:x8}";
            engine.Messages.Add(new FakeMessage(id, "f:00000001",
                FakePstEngine.Doc(id, "f:00000001", $"Firma {i}", "ada@example.test", "Ada",
                    to: [("bob@example.test", "Bob")], received: "2024-03-01T10:00:00Z", text: "x",
                    attachments: [FakePstEngine.Att("0", "image001.png", "image/png")]),
                new() { ["0"] = FakePstEngine.Bytes($"signature-{i}") }));
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var (factory, _, owner, archive) = await ImportedAsync(this, engine);
        stopwatch.Stop();
        Assert.Equal("succeeded", archive.LatestRun!.Status);
        Assert.Equal(count, archive.LatestRun.ImportedMessages);

        var names = await InDbAsync(factory, db => db.FileItems.AsNoTracking()
            .Where(f => f.OwnerUserId == owner && f.DeletedAt == null && f.Name.StartsWith("image001"))
            .Select(f => new { f.Name, f.ParentFolderId }).ToListAsync());
        Assert.Equal(count, names.Count);
        Assert.Single(names.Select(n => n.ParentFolderId).Distinct());
        Assert.Equal(count, names.Select(n => n.Name).Distinct(StringComparer.Ordinal).Count());
        Assert.Single(names, n => n.Name == "image001.png");
        // Every other name is the source-tagged one, never the "(N)" fallback.
        Assert.All(names.Where(n => n.Name != "image001.png"),
            n => Assert.Matches(@"^image001 \([0-9a-f]{8}-0\)\.png$", n.Name));
        // And the link keeps the original name.
        Assert.Equal(count, await InDbAsync(factory, db => db.MailAttachments.CountAsync(a => a.OriginalName == "image001.png")));

        _output.WriteLine(
            $"{count} same-named attachments in one folder: {stopwatch.ElapsedMilliseconds} ms end to end "
            + $"({stopwatch.Elapsed.TotalMilliseconds / count:F1} ms/message, SQLite test host)");
    }

    [Fact]
    public async Task Same_pst_uploaded_twice_is_one_archive_and_a_different_pst_is_independent()
    {
        var (factory, client, _, archive) = await ImportedAsync(this);
        var copy = await UploadPstAsync(client, "copia.pst");
        var again = await client.PostAsJsonAsync("/api/mail/archives", new { fileItemId = copy });
        Assert.Equal(HttpStatusCode.Accepted, again.StatusCode);
        var started = (await again.Content.ReadFromJsonAsync<MailStartImportResponse>(Json))!;
        Assert.Equal(archive.Id, started.Archive.Id);
        await RunJobsAsync(factory);

        var other = await UploadPstAsync(client, "altro.pst", body: "different-bytes");
        var otherStart = await StartAsync(client, other);
        Assert.NotEqual(archive.Id, otherStart.Archive.Id);
        await RunJobsAsync(factory);
        var archives = await ItemsAsync<MailArchiveDto>(client, "/api/mail/archives");
        Assert.Equal(2, archives.Count);
        Assert.All(archives, a => Assert.Equal(4, a.MessageCount));
        var sources = await ItemsAsync<MailSourceCandidateDto>(client, "/api/mail/sources");
        Assert.Equal(3, sources.Count);
        Assert.Equal(archive.Id, sources.Single(s => s.Name == "copia.pst").AlreadyImportedArchiveId);
    }

    // ---- crash recovery, failures, cancellation ----------------------------------------

    [Fact]
    public async Task Crash_after_attachments_before_completion_reuses_the_created_files()
    {
        var engine = FakePstEngine.Sample();
        var crashes = 0;
        var factory = NewFactory(engine, services =>
        {
            services.RemoveAll<IBlobService>();
            services.AddScoped<IBlobService>(sp => new CrashOnceOnDocumentBlobService(
                ActivatorUtilities.CreateInstance<BlobService>(sp), () => crashes++ == 0));
        });
        var (owner, client) = await factory.CreateAuthenticatedClientAsync();
        var started = await StartAsync(client, await UploadPstAsync(client));

        await RunJobsAsync(factory); // first attempt dies on the first document
        Assert.True(crashes > 0);
        // The job processor schedules the retry at least a second later; bring
        // it forward instead of sleeping.
        await InDbAsync(factory, db => db.BackgroundJobs
            .Where(j => j.Status == JobStatuses.Queued)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.AvailableAt, DateTime.UtcNow.AddMinutes(-1))));
        await RunJobsAsync(factory); // the retry reuses the files and finishes

        var archive = await GetAsync<MailArchiveDto>(client, $"/api/mail/archives/{started.Archive.Id}");
        Assert.Equal("succeeded", archive.LatestRun!.Status);
        Assert.Equal(4, archive.MessageCount);
        var names = await InDbAsync(factory, db => db.FileItems.AsNoTracking()
            .Where(f => f.OwnerUserId == owner && f.DeletedAt == null && !f.Name.EndsWith(".pst"))
            .Select(f => f.Name).OrderBy(n => n).ToListAsync());
        Assert.Equal(["dentro.txt", "foto.jpg", "logo.png", "report (00000101-2).pdf", "report.pdf"], names);
        var audit = await AuditAsync(factory);
        Assert.Equal(audit.TotalBlobs, audit.MatchedReferenceCount);
    }

    private static FakePstEngine PlainMessages(int count)
    {
        var engine = new FakePstEngine();
        engine.Folders.Add(new FakeFolder("f:00000001", null, "Posta in arrivo", ["Posta in arrivo"], 0));
        for (var i = 0; i < count; i++)
        {
            var id = $"m:{0x10000 + i * 0x20:x8}";
            engine.Messages.Add(new FakeMessage(id, "f:00000001",
                FakePstEngine.Doc(id, "f:00000001", $"Messaggio {i}", "ada@example.test", "Ada",
                    to: [("bob@example.test", "Bob")], received: "2024-03-01T10:00:00Z", text: $"testo {i}"),
                new()));
        }
        return engine;
    }

    [Fact]
    public async Task A_crash_while_completing_a_group_commits_none_of_it_and_the_retry_imports_each_message_once()
    {
        // 60 attachment-free messages: groups of 25 are completed in one
        // transaction each. The 30th document blob dies: the first group is
        // committed, the second is not, and its checkpoint never moved.
        var engine = PlainMessages(60);
        var stored = 0;
        var factory = NewFactory(engine, services =>
        {
            services.RemoveAll<IBlobService>();
            services.AddScoped<IBlobService>(sp => new CrashOnceOnDocumentBlobService(
                ActivatorUtilities.CreateInstance<BlobService>(sp), () => ++stored == 30));
        });
        var (_, client) = await factory.CreateAuthenticatedClientAsync();
        var started = await StartAsync(client, await UploadPstAsync(client));

        await RunJobsAsync(factory);
        var midway = await InDbAsync(factory, db => db.MailMessages.CountAsync(m => m.ArchiveId == started.Archive.Id));
        Assert.Equal(25, midway);
        Assert.Equal(25, await InDbAsync(factory, db => db.MailImportItems
            .CountAsync(i => i.ArchiveId == started.Archive.Id && i.Status == MailImportItemStatuses.Imported)));

        await InDbAsync(factory, db => db.BackgroundJobs
            .Where(j => j.Status == JobStatuses.Queued)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.AvailableAt, DateTime.UtcNow.AddMinutes(-1))));
        await RunJobsAsync(factory);

        var archive = await GetAsync<MailArchiveDto>(client, $"/api/mail/archives/{started.Archive.Id}");
        Assert.Equal("succeeded", archive.LatestRun!.Status);
        Assert.Equal(60, archive.MessageCount);
        var keys = await InDbAsync(factory, db => db.MailMessages.AsNoTracking()
            .Where(m => m.ArchiveId == started.Archive.Id)
            .Select(m => m.SourceMessageKey).ToListAsync());
        Assert.Equal(60, keys.Distinct(StringComparer.Ordinal).Count());
        var audit = await AuditAsync(factory);
        Assert.Equal(audit.TotalBlobs, audit.MatchedReferenceCount);
    }

    [Fact]
    public async Task Groups_never_run_past_the_slice_budget()
    {
        var engine = PlainMessages(10);
        var factory = NewFactory(engine, settings: new() { ["Jobs:MaintenanceSliceItemBudget"] = "3" });
        var (_, client) = await factory.CreateAuthenticatedClientAsync();
        var started = await StartAsync(client, await UploadPstAsync(client));

        // One slice at a time: each imports at most three messages.
        var imported = 0;
        for (var slice = 0; slice < 20 && imported < 10; slice++)
        {
            using (var scope = factory.Services.CreateScope())
            {
                await scope.ServiceProvider.GetRequiredService<JobProcessor>().ProcessAvailableAsync(1);
            }
            var now = await InDbAsync(factory, db => db.MailMessages.CountAsync(m => m.ArchiveId == started.Archive.Id));
            Assert.InRange(now - imported, 0, 3);
            imported = now;
        }

        await RunJobsAsync(factory);
        var archive = await GetAsync<MailArchiveDto>(client, $"/api/mail/archives/{started.Archive.Id}");
        Assert.Equal("succeeded", archive.LatestRun!.Status);
        Assert.Equal(10, archive.MessageCount);
        Assert.Equal(10, engine.Extractions);
    }

    [Fact]
    public async Task A_lost_link_is_adopted_from_the_completed_upload_claim()
    {
        var engine = FakePstEngine.Sample();
        engine.ErrorFor["m:00000101"] = "corrupt"; // first run: the message fails
        var (factory, client, owner, archive) = await ImportedAsync(this, engine);
        Assert.Equal("partial", archive.LatestRun!.Status);
        Assert.Equal(1, archive.LatestRun.FailedMessages);
        var failures = await GetAsync<MailFailurePage>(client, $"/api/mail/runs/{archive.LatestRun.Id}/failures");
        var failure = Assert.Single(failures.Items);
        Assert.Equal("corrupt", failure.Code);
        Assert.Equal(["Top of Outlook data file", "Posta in arrivo"], failure.FolderPath);

        // Simulate a crash that committed a file under the attachment's claim
        // but never recorded the link: create it exactly the way the runner does.
        engine.ErrorFor.Clear();
        var messageId = Guid.NewGuid();
        await InDbAsync(factory, async db =>
        {
            var folder = await db.MailFolders.FirstAsync(f => f.SourceFolderKey == "f:00000002");
            db.MailMessages.Add(new MailMessage
            {
                Id = messageId, OwnerUserId = owner, ArchiveId = archive.Id, FolderId = folder.Id,
                SourceMessageKey = "m:00000101", Status = MailMessageStatuses.Importing,
                SortDate = DateTime.UtcNow, CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
            return 0;
        });
        using (var scope = factory.Services.CreateScope())
        {
            var idem = scope.ServiceProvider.GetRequiredService<NubArca.Api.Uploads.IUploadIdempotencyService>();
            var files = scope.ServiceProvider.GetRequiredService<IFileItemService>();
            var claim = await idem.TryClaimAsync(owner, $"mailarchive:{messageId:N}:0", TimeSpan.FromMinutes(30));
            await using var bytes = new MemoryStream(FakePstEngine.Bytes("pdf-one"));
            await files.CreateAsync(owner, null, "orphan-report.pdf", "application/pdf", bytes,
                uploadOperationClaimToken: claim.Token);
        }

        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsync($"/api/mail/archives/{archive.Id}/import", null)).StatusCode);
        await RunJobsAsync(factory);
        var after = await GetAsync<MailArchiveDto>(client, $"/api/mail/archives/{archive.Id}");
        Assert.Equal("succeeded", after.LatestRun!.Status);
        Assert.Equal(1, after.LatestRun.ImportedMessages);
        Assert.Equal(1, after.LatestRun.AttachmentsReused);
        var detail = await GetAsync<MailMessageDetailDto>(client, $"/api/mail/messages/{messageId}");
        Assert.Equal("orphan-report.pdf", detail.Attachments[0].CurrentFileName); // attachment "0" adopted, not recreated
        var reports = await InDbAsync(factory, db => db.FileItems.CountAsync(f => f.OwnerUserId == owner && f.Name.Contains("report")));
        Assert.Equal(2, reports); // the adopted one + the second report, never a third
    }

    [Fact]
    public async Task Cancel_then_resume_finishes_without_duplicates()
    {
        var engine = FakePstEngine.Sample();
        var factory = NewFactory(engine, settings: new() { ["Jobs:MaintenanceSliceItemBudget"] = "1" });
        var (owner, client) = await factory.CreateAuthenticatedClientAsync();
        var started = await StartAsync(client, await UploadPstAsync(client));

        using (var scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<JobProcessor>().ProcessAvailableAsync(1); // one slice: one message
        }
        var mid = await GetAsync<MailRunDto>(client, $"/api/mail/runs/{started.Run.Id}");
        Assert.Equal("running", mid.Status);

        var cancel = await client.PostAsync($"/api/mail/runs/{started.Run.Id}/cancel", null);
        Assert.Equal(HttpStatusCode.Accepted, cancel.StatusCode);
        await RunJobsAsync(factory);
        var cancelled = await GetAsync<MailRunDto>(client, $"/api/mail/runs/{started.Run.Id}");
        Assert.Equal("cancelled", cancelled.Status);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync($"/api/mail/runs/{started.Run.Id}/cancel", null)).StatusCode);
        var visible = await GetAsync<MailMessagePage>(client, $"/api/mail/messages?archiveId={started.Archive.Id}");
        Assert.Single(visible.Items);

        var resume = await client.PostAsync($"/api/mail/archives/{started.Archive.Id}/import", null);
        Assert.Equal(HttpStatusCode.Accepted, resume.StatusCode);
        await RunJobsAsync(factory);
        var archive = await GetAsync<MailArchiveDto>(client, $"/api/mail/archives/{started.Archive.Id}");
        Assert.Equal("succeeded", archive.LatestRun!.Status);
        Assert.Equal(3, archive.LatestRun.ImportedMessages);
        Assert.Equal(1, archive.LatestRun.AlreadyPresentMessages);
        Assert.Equal(4, archive.MessageCount);
        var attachments = await InDbAsync(factory, db => db.FileItems.CountAsync(f => f.OwnerUserId == owner && !f.Name.EndsWith(".pst")));
        Assert.Equal(5, attachments);
        Assert.Equal(1, engine.Lists);
    }

    [Fact]
    public async Task Second_start_while_running_is_a_conflict_naming_the_run()
    {
        var factory = NewFactory(FakePstEngine.Sample());
        var (_, client) = await factory.CreateAuthenticatedClientAsync();
        var pst = await UploadPstAsync(client);
        var started = await StartAsync(client, pst);
        var again = await client.PostAsJsonAsync("/api/mail/archives", new { fileItemId = pst });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        var body = await again.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("import_running", body.GetProperty("error").GetString());
        Assert.Equal(started.Run.Id, body.GetProperty("runId").GetGuid());
    }

    [Fact]
    public async Task Not_a_pst_and_missing_source_are_refused()
    {
        var factory = NewFactory(FakePstEngine.Sample());
        var (_, client) = await factory.CreateAuthenticatedClientAsync();
        var part = new ByteArrayContent(FakePstEngine.Bytes("hello world"));
        var multipart = new MultipartFormDataContent { { part, "file", "finto.pst" } };
        var text = (await (await client.PostAsync("/api/files", multipart)).Content.ReadFromJsonAsync<FileSummary>())!.Id;
        var response = await client.PostAsJsonAsync("/api/mail/archives", new { fileItemId = text });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var pst = await UploadPstAsync(client);
        var started = await StartAsync(client, pst);
        await RunJobsAsync(factory);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/files/{pst}")).StatusCode);
        var reimport = await client.PostAsync($"/api/mail/archives/{started.Archive.Id}/import", null);
        Assert.Equal(HttpStatusCode.Conflict, reimport.StatusCode);
        // The archive and its messages outlive their source file.
        var archive = await GetAsync<MailArchiveDto>(client, $"/api/mail/archives/{started.Archive.Id}");
        Assert.False(archive.SourceAvailable);
        Assert.Equal(4, archive.MessageCount);
    }

    [Fact]
    public async Task Unavailable_engine_is_reported_and_refused()
    {
        var engine = FakePstEngine.Sample();
        engine.IsAvailable = false;
        var factory = NewFactory(engine);
        var (_, client) = await factory.CreateAuthenticatedClientAsync();
        var status = await GetAsync<MailStatusDto>(client, "/api/mail/status");
        Assert.False(status.Available);
        Assert.Equal("importer_missing", status.Reason);
        var response = await client.PostAsJsonAsync("/api/mail/archives", new { fileItemId = await UploadPstAsync(client) });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    // ---- immutability, isolation, no leaks -------------------------------------------------

    [Fact]
    public async Task Messages_cannot_be_changed_through_any_route()
    {
        var (factory, client, owner, archive) = await ImportedAsync(this);
        var message = (await GetAsync<MailMessagePage>(client, $"/api/mail/messages?archiveId={archive.Id}")).Items[0];

        foreach (var method in new[] { HttpMethod.Put, HttpMethod.Patch, HttpMethod.Post })
        {
            var response = await client.SendAsync(new HttpRequestMessage(method, $"/api/mail/messages/{message.Id}")
            {
                Content = JsonContent.Create(new { subject = "changed" }),
            });
            Assert.True(response.StatusCode is HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotFound, method.Method);
        }
        // The message id is not a file: generic file routes cannot reach it.
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.PatchAsJsonAsync($"/api/files/{message.Id}/rename", new { name = "x.eml" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.PatchAsJsonAsync($"/api/files/{message.Id}/move", new { parentFolderId = (Guid?)null })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/files/{message.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/files/{message.Id}/content")).StatusCode);

        // No FileItem owns a message document: nothing generic can rename or
        // move it, and the reference audit counts it through the message row.
        var documentBlobs = await InDbAsync(factory, db => db.MailMessages.Select(m => m.BlobObjectId!.Value).ToListAsync());
        Assert.False(await InDbAsync(factory, db => db.FileItems.IgnoreQueryFilters().AnyAsync(f => documentBlobs.Contains(f.BlobObjectId))));
        Assert.Equal(4, documentBlobs.Distinct().Count());

        var after = await GetAsync<MailMessageDetailDto>(client, $"/api/mail/messages/{message.Id}");
        Assert.Equal(message.Subject, after.Subject);
    }

    [Fact]
    public async Task Another_owner_sees_nothing_and_can_touch_nothing()
    {
        var (factory, client, _, archive) = await ImportedAsync(this);
        var message = (await GetAsync<MailMessagePage>(client, $"/api/mail/messages?archiveId={archive.Id}")).Items[0];
        var detail = await GetAsync<MailMessageDetailDto>(client, $"/api/mail/messages/{message.Id}");
        var (_, other) = await factory.CreateAuthenticatedClientAsync("other@example.com");

        Assert.Empty(await ItemsAsync<MailArchiveDto>(other, "/api/mail/archives"));
        Assert.Empty(await ItemsAsync<MailSourceCandidateDto>(other, "/api/mail/sources"));
        Assert.Empty((await GetAsync<MailMessagePage>(other, "/api/mail/messages")).Items);
        Assert.Empty((await GetAsync<MailMessagePage>(other, $"/api/mail/messages?archiveId={archive.Id}")).Items);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/mail/archives/{archive.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/mail/archives/{archive.Id}/folders")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/mail/runs/{archive.LatestRun!.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/mail/runs/{archive.LatestRun.Id}/failures")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsync($"/api/mail/runs/{archive.LatestRun.Id}/cancel", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsync($"/api/mail/archives/{archive.Id}/import", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/mail/messages/{message.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.DeleteAsync($"/api/mail/messages/{message.Id}")).StatusCode);
        var fileId = detail.Attachments.FirstOrDefault()?.FileItemId;
        if (fileId is not null)
        {
            Assert.Empty(await ItemsAsync<MailFileOriginDto>(other, $"/api/mail/file-origins/{fileId}"));
        }
        var pstId = await InDbAsync(factory, db => db.MailArchives.Select(a => a.SourceFileItemId!.Value).FirstAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsJsonAsync("/api/mail/archives", new { fileItemId = pstId })).StatusCode);

        // The owner's message is untouched.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/mail/messages/{message.Id}")).StatusCode);
        var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/mail/archives")).StatusCode);
    }

    [Fact]
    public async Task Responses_never_carry_internal_identifiers()
    {
        var (factory, client, _, archive) = await ImportedAsync(this);
        var message = (await GetAsync<MailMessagePage>(client, $"/api/mail/messages?archiveId={archive.Id}&subject=trimestrale")).Items[0];
        var fingerprint = await InDbAsync(factory, db => db.MailArchives.Select(a => a.SourceFingerprint).FirstAsync());
        var blobIds = await InDbAsync(factory, db => db.BlobObjects.Select(b => new { b.Id, b.Sha256, b.StorageKey }).ToListAsync());

        var bodies = new List<string>();
        foreach (var url in new[]
        {
            "/api/mail/archives", $"/api/mail/archives/{archive.Id}", $"/api/mail/archives/{archive.Id}/folders",
            $"/api/mail/runs/{archive.LatestRun!.Id}", $"/api/mail/messages?archiveId={archive.Id}",
            $"/api/mail/messages/{message.Id}", "/api/mail/sources",
        })
        {
            bodies.Add(await client.GetStringAsync(url));
        }
        var all = string.Join('\n', bodies);
        Assert.DoesNotContain(fingerprint, all);
        Assert.DoesNotContain("m:0000", all);
        Assert.DoesNotContain("f:0000", all);
        Assert.DoesNotContain("storageKey", all, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sha256", all, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("mail-work", all);
        foreach (var blob in blobIds)
        {
            Assert.DoesNotContain(blob.Id.ToString(), all);
            Assert.DoesNotContain(blob.Sha256, all);
            Assert.DoesNotContain(blob.StorageKey, all);
        }
    }

    // A blob service that dies once while storing a message document — after
    // the message's attachments were committed, before it completes.
    private sealed class CrashOnceOnDocumentBlobService(IBlobService inner, Func<bool> shouldCrash) : IBlobService
    {
        public async Task<BlobObject> StoreAsync(Stream content, CancellationToken cancellationToken = default)
        {
            var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, cancellationToken);
            buffer.Position = 0;
            if (buffer.Length > 0 && buffer.GetBuffer()[0] == (byte)'{' && shouldCrash())
            {
                throw new InvalidOperationException("simulated crash");
            }
            return await inner.StoreAsync(buffer, cancellationToken);
        }

        public Task<BlobStoreResult> StoreMeasuredAsync(Stream content, CancellationToken cancellationToken = default) => inner.StoreMeasuredAsync(content, cancellationToken);
        public Task<Stream> OpenContentAsync(Guid blobObjectId, CancellationToken cancellationToken = default) => inner.OpenContentAsync(blobObjectId, cancellationToken);
        public Task<BlobObject> StoreDerivedAsync(Stream content, CancellationToken cancellationToken = default) => inner.StoreDerivedAsync(content, cancellationToken);
        public Task<Stream?> OpenDerivedContentAsync(Guid blobObjectId, CancellationToken cancellationToken = default) => inner.OpenDerivedContentAsync(blobObjectId, cancellationToken);
        public Task ReleaseAsync(Guid blobObjectId, CancellationToken cancellationToken = default) => inner.ReleaseAsync(blobObjectId, cancellationToken);
        public Task MarkPurgeEligibleIfUnreferencedAsync(Guid blobObjectId, CancellationToken cancellationToken = default) => inner.MarkPurgeEligibleIfUnreferencedAsync(blobObjectId, cancellationToken);
        public Task<BlobObject> AcquireExistingAsync(Guid blobObjectId, CancellationToken cancellationToken = default) => inner.AcquireExistingAsync(blobObjectId, cancellationToken);
        public Task<bool> TryRestoreDerivedFromOriginalAsync(Guid blobObjectId, CancellationToken cancellationToken = default) => inner.TryRestoreDerivedFromOriginalAsync(blobObjectId, cancellationToken);
    }
}
