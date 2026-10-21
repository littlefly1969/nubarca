using System.Security.Cryptography;
using System.Text.Json;
using NubArca.Api.MailArchive;

namespace NubArca.Api.Tests.MailArchive;

// A scripted stand-in for the Rust engine that speaks the same contract
// (docs/mailarchive/contract.md §2): folders and messages are declared in
// memory, `serve` writes message.json and attachment files under the request
// directory exactly as the engine does. Failure hooks let tests interrupt an
// import at a precise point. The real binary is exercised separately.
public sealed class FakePstEngine : IPstEngine
{
    public List<FakeFolder> Folders { get; } = [];
    public List<FakeMessage> Messages { get; } = [];

    // Called before each extraction; return an exception to throw it.
    public Func<string, Exception?>? BeforeExtract { get; set; }
    // Per-message engine error responses (code), instead of an extraction.
    public Dictionary<string, string> ErrorFor { get; } = new();
    // Folders whose contents `list` cannot enumerate: the folder is reported,
    // then a folder-scoped error, and none of its messages.
    public HashSet<string> UnreadableFolders { get; } = new(StringComparer.Ordinal);

    public int Extractions { get; private set; }
    public int Lists { get; private set; }
    public bool IsAvailable { get; set; } = true;

    public static FakePstEngine Sample()
    {
        var engine = new FakePstEngine();
        engine.Folders.Add(new FakeFolder("f:00000001", null, "Top of Outlook data file", ["Top of Outlook data file"], 0));
        engine.Folders.Add(new FakeFolder("f:00000002", "f:00000001", "Posta in arrivo", ["Top of Outlook data file", "Posta in arrivo"], 1));
        engine.Folders.Add(new FakeFolder("f:00000003", "f:00000002", "Progetti", ["Top of Outlook data file", "Posta in arrivo", "Progetti"], 2));
        engine.Folders.Add(new FakeFolder("f:00000004", "f:00000001", "Posta inviata", ["Top of Outlook data file", "Posta inviata"], 1));

        engine.Messages.Add(new FakeMessage("m:00000101", "f:00000002",
            Doc("m:00000101", "f:00000002", "Relazione trimestrale", "ada@example.test", "Ada Example",
                to: [("bob@example.test", "Bob")], cc: [("carla@example.test", "Carla")], bcc: [("dario@example.test", null)],
                received: "2024-03-01T10:00:05Z",
                html: "<html><head><style>p{color:red}</style><script>alert(1)</script></head><body><p onclick=\"x()\">Ciao <b>Bob</b></p><img src=\"cid:logo@nubarca\"><img src=\"https://tracker.example.test/p.gif\"><a href=\"javascript:alert(1)\">x</a></body></html>",
                text: "Ciao Bob",
                attachments:
                [
                    Att("0", "report.pdf", "application/pdf"),
                    Att("1", "logo.png", "image/png", contentId: "<logo@nubarca>", inline: true),
                    Att("2", "report.pdf", "application/pdf"),
                ]),
            new() { ["0"] = Bytes("pdf-one"), ["1"] = Bytes("png-logo"), ["2"] = Bytes("pdf-two") }));

        engine.Messages.Add(new FakeMessage("m:00000102", "f:00000002",
            Doc("m:00000102", "f:00000002", "Senza allegati — àèìòù 日本語", "bob@example.test", "Bob",
                to: [("ada@example.test", "Ada")], received: "2024-03-02T09:00:00Z", text: "Solo testo."),
            new()));

        engine.Messages.Add(new FakeMessage("m:00000103", "f:00000003",
            Doc("m:00000103", "f:00000003", "Inoltro con messaggio incorporato", "carla@example.test", "Carla",
                to: [("ada@example.test", "Ada")], received: "2024-02-10T08:00:00Z", text: "Vedi sotto.",
                attachments:
                [
                    new
                    {
                        key = "0", method = "embedded_message", fileName = (string?)null, displayName = "Fwd: originale",
                        mimeType = (string?)null, contentId = (string?)null, inline = false, hidden = false, sizeBytes = 100,
                        extracted = false, skipReason = (string?)null,
                        embeddedMessage = new
                        {
                            subject = "Originale", from = new { name = "Eva", address = "eva@example.test", addressType = "SMTP" },
                            to = Array.Empty<object>(), cc = Array.Empty<object>(), bcc = Array.Empty<object>(),
                            sentAt = "2024-02-09T08:00:00Z", body = new { text = "Testo originale", html = (string?)null, rtf = (string?)null },
                            attachments = new object[] { Att("0.0", "dentro.txt", "text/plain") },
                        },
                    },
                    Att("1", "contratto.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                        extracted: false, skipReason: "unsupported_method", method: "ole"),
                ]),
            new() { ["0.0"] = Bytes("inner-text") }));

        engine.Messages.Add(new FakeMessage("m:00000104", "f:00000004",
            Doc("m:00000104", "f:00000004", "Re: Relazione trimestrale", "ada@example.test", "Ada Example",
                to: [("bob@example.test", "Bob")], received: null, sent: "2024-03-03T12:00:00Z", text: "Grazie!",
                attachments: [Att("0", "foto.jpg", "image/jpeg")]),
            new() { ["0"] = Bytes("pdf-one") }));
        return engine;
    }

    public static byte[] Bytes(string text) => System.Text.Encoding.UTF8.GetBytes(text);

    public static object Att(string key, string fileName, string mime, string? contentId = null, bool inline = false,
        bool extracted = true, string? skipReason = null, string method = "by_value") => new
    {
        key, method, fileName, displayName = fileName, mimeType = mime, contentId, inline, hidden = false,
        sizeBytes = 10, extracted, skipReason, embeddedMessage = (object?)null,
    };

    public static object Doc(string id, string folderId, string subject, string fromAddress, string? fromName,
        (string Address, string? Name)[] to, (string Address, string? Name)[]? cc = null,
        (string Address, string? Name)[]? bcc = null, string? received = null, string? sent = null,
        string? html = null, string? text = null, object[]? attachments = null) => new
    {
        format = "nubarca-mail-message",
        version = 1,
        source = new { messageId = id, folderId },
        messageClass = "IPM.Note",
        subject,
        from = new { name = fromName, address = fromAddress, addressType = "SMTP" },
        to = to.Select(r => new { name = r.Name, address = r.Address, addressType = "SMTP" }),
        cc = (cc ?? []).Select(r => new { name = r.Name, address = r.Address, addressType = "SMTP" }),
        bcc = (bcc ?? []).Select(r => new { name = r.Name, address = r.Address, addressType = "SMTP" }),
        sentAt = sent ?? received,
        receivedAt = received,
        internetMessageId = $"<{id.Replace("m:", "msg-")}@example.test>",
        hasAttachments = attachments is { Length: > 0 },
        body = new { text, html, rtf = (string?)null, truncated = new { text = false, html = false, rtf = false } },
        attachments = attachments ?? [],
        warnings = Array.Empty<object>(),
    };

    public Task<PstProbe> ProbeAsync(string pstPath, CancellationToken cancellationToken)
    {
        AssertReadable(pstPath);
        return Task.FromResult(new PstProbe("probe", PstProtocol.Version, "unicode", "none", "Cartelle personali", new FileInfo(pstPath).Length));
    }

    public async IAsyncEnumerable<PstListEvent> ListAsync(
        string pstPath, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        AssertReadable(pstPath);
        Lists++;
        var errors = 0;
        var listed = 0;
        foreach (var folder in Folders)
        {
            yield return new PstListEvent("folder", folder.Id, folder.ParentId, folder.Name, folder.Path, folder.Depth,
                "IPF.Note", Messages.Count(m => m.FolderId == folder.Id), null, null, null, null, null, null, null, null);
            if (UnreadableFolders.Contains(folder.Id))
            {
                errors++;
                yield return new PstListEvent("error", null, null, null, null, null, null, null, null,
                    "folder", folder.Id, "corrupt", "contents table unreadable", null, null, null);
                continue;
            }
            foreach (var message in Messages.Where(m => m.FolderId == folder.Id))
            {
                cancellationToken.ThrowIfCancellationRequested();
                listed++;
                yield return new PstListEvent("message", folder.Id, null, null, null, null, null, null, message.Id,
                    null, null, null, null, null, null, null);
            }
        }
        await Task.Yield();
        yield return new PstListEvent("end", null, null, null, null, null, null, null, null, null, null, null, null,
            Folders.Count, listed, errors);
    }

    public Task<IPstExtractionSession> StartSessionAsync(string pstPath, string workDir, CancellationToken cancellationToken)
    {
        AssertReadable(pstPath);
        return Task.FromResult<IPstExtractionSession>(new Session(this, workDir));
    }

    private static void AssertReadable(string pstPath)
    {
        using var stream = File.OpenRead(pstPath);
        var header = new byte[4];
        stream.ReadExactly(header);
        if (System.Text.Encoding.ASCII.GetString(header) != "!BDN")
        {
            throw new PstEngineException("not_pst", "not a PST");
        }
    }

    private sealed class Session(FakePstEngine engine, string workDir) : IPstExtractionSession
    {
        public async Task<PstExtractResponse> ExtractAsync(string requestId, string messageKey, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            engine.Extractions++;
            if (engine.BeforeExtract?.Invoke(messageKey) is { } failure)
            {
                throw failure;
            }
            if (engine.ErrorFor.TryGetValue(messageKey, out var code))
            {
                return new PstExtractResponse("error", requestId, messageKey, null, null, null, null, code, "scripted failure");
            }
            var message = engine.Messages.FirstOrDefault(m => m.Id == messageKey);
            if (message is null)
            {
                return new PstExtractResponse("error", requestId, messageKey, null, null, null, null, "not_found", "no such message");
            }
            var dir = Path.Combine(workDir, requestId);
            Directory.CreateDirectory(dir);
            var json = JsonSerializer.SerializeToUtf8Bytes(message.Document);
            await File.WriteAllBytesAsync(Path.Combine(dir, "message.json"), json, cancellationToken);
            var files = new List<PstExtractedAttachmentFile>();
            foreach (var (key, bytes) in message.Attachments)
            {
                var name = $"att-{key}.bin";
                await File.WriteAllBytesAsync(Path.Combine(dir, name), bytes, cancellationToken);
                files.Add(new PstExtractedAttachmentFile(key, $"{requestId}/{name}", bytes.Length,
                    Convert.ToHexStringLower(SHA256.HashData(bytes))));
            }
            return new PstExtractResponse("extracted", requestId, messageKey, $"{requestId}/message.json", json.Length,
                files, [], null, null);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

public sealed record FakeFolder(string Id, string? ParentId, string Name, string[] Path, int Depth);

public sealed record FakeMessage(string Id, string FolderId, object Document, Dictionary<string, byte[]> Attachments);
