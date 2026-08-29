using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NubArca.Api.Ai.DocumentVisual;
using NubArca.Api.Ai.Documents;
using NubArca.Api.Tests.Ai.Documents;
using Xunit;
using Xunit.Abstractions;

namespace NubArca.Api.Tests.Integration;

// THE REAL OFFICE RENDERER, against the real worker.
//
// `OfficeVisualRendererTests` proves the CLIENT's half against a fake socket:
// what the protocol can express, what it refuses to allocate, how answers are
// mapped. This is the other half, and it needs a container: does LibreOffice
// headless actually lay a DOCX out, does the timeout actually kill it, does the
// job directory actually get cleaned up.
//
// GATED ON `NUBARCA_RENDER_SOCKET`, with no fallback and no attempt to start a
// container from inside a test. A default path would be an installation-specific
// literal in tracked source. Unset means SKIPPED, and a completion report has to
// say so rather than claim a lane it did not run.
//
//   docker build -t nubarca-document-renderer:local scripts/document-render-worker
//   docker run -d --name nubarca-render-test --network none --read-only \
//     --cap-drop ALL --security-opt no-new-privileges:true \
//     --tmpfs /tmp:size=64m,mode=1777 \
//     --tmpfs /var/tmp/nubarca-render:size=512m,mode=0700 \
//     -v <dir>:/run/nubarca-render nubarca-document-renderer:local
//   NUBARCA_RENDER_SOCKET=<dir>/render.sock dotnet test --filter OfficeRendererWorker
[Trait("Category", "External")]
public sealed class OfficeRendererWorkerTests
{
    private readonly ITestOutputHelper _output;

    public OfficeRendererWorkerTests(ITestOutputHelper output) => _output = output;

    private static string? SocketPath
    {
        get
        {
            var path = Environment.GetEnvironmentVariable("NUBARCA_RENDER_SOCKET");
            return string.IsNullOrWhiteSpace(path) || !File.Exists(path) ? null : path;
        }
    }

    private static DocumentVisualOptions Options(string socket) => new()
    {
        Enabled = true,
        RenderOfficeEnabled = true,
        OfficeRendererSocketPath = socket,
        MaxOfficeRenderSeconds = 120,
    };

    private static OfficeVisualRenderer Renderer(DocumentVisualOptions options)
        => new(
            Microsoft.Extensions.Options.Options.Create(options),
            new PdfVisualRenderer(
                Microsoft.Extensions.Options.Options.Create(options),
                NullLogger<PdfVisualRenderer>.Instance),
            NullLogger<OfficeVisualRenderer>.Instance);

    [SkippableFact]
    public void The_Worker_Reports_Itself_Ready()
    {
        var socket = SocketPath;
        Skip.If(socket is null, "NUBARCA_RENDER_SOCKET is not set to a running worker.");

        var readiness = Renderer(Options(socket!)).CheckReadiness();

        Assert.True(readiness.Ready, readiness.Reason);
    }

    [SkippableTheory]
    [InlineData("docx")]
    [InlineData("xlsx")]
    [InlineData("pptx")]
    public async Task Each_Office_Family_Lays_Out_Into_Pages(string family)
    {
        var socket = SocketPath;
        Skip.If(socket is null, "NUBARCA_RENDER_SOCKET is not set to a running worker.");

        var options = Options(socket!);
        var (bytes, format) = family switch
        {
            "docx" => (Docx(), DocumentFormatKind.WordOpenXml),
            "xlsx" => (Xlsx(), DocumentFormatKind.SpreadsheetOpenXml),
            _ => (Pptx(), DocumentFormatKind.PresentationOpenXml),
        };

        var started = DateTime.UtcNow;
        var outcome = await Renderer(options).RenderAsync(
            new DocumentVisualRenderRequest(bytes, format, options));
        var elapsed = DateTime.UtcNow - started;

        Assert.True(outcome.Ok, outcome.Reason);
        Assert.NotEmpty(outcome.Artifact!.Units);
        Assert.Equal(DocumentVisualRenderProfiles.LibreOfficePdf, outcome.Artifact.RenderProfileKey);

        _output.WriteLine(
            $"{family}: units={outcome.Artifact.Units.Count} elapsed={elapsed.TotalSeconds:F1}s "
            + $"first_page_bytes={outcome.Artifact.Units[0].Png.Length}");

        // A REAL PAGE WITH REAL PIXELS, and no provenance claimed for it: the
        // ordinal is LibreOffice's pagination, not the author's document.
        Assert.All(outcome.Artifact.Units, u =>
        {
            Assert.Equal(DocumentVisualRenderKinds.OfficeRenderedPage, u.RenderKind);
            Assert.True(u.Png.Length > 0);
            Assert.True(u.Width > 0 && u.Height > 0);
            Assert.Null(u.SourcePage);
            Assert.Null(u.SourceLocator);
        });
    }

    [SkippableFact]
    public async Task A_Document_That_Is_Not_What_It_Claims_Is_Refused()
    {
        var socket = SocketPath;
        Skip.If(socket is null, "NUBARCA_RENDER_SOCKET is not set to a running worker.");

        var options = Options(socket!);
        var outcome = await Renderer(options).RenderAsync(
            new DocumentVisualRenderRequest(
                System.Text.Encoding.UTF8.GetBytes("this is not a Word document at all"),
                DocumentFormatKind.WordOpenXml,
                options));

        Assert.False(outcome.Ok);
        // A sanitized token, never the engine's own message — which can carry a
        // temporary path.
        Assert.Contains(outcome.Reason, new[]
        {
            DocumentVisualReasons.InvalidSource,
            DocumentVisualReasons.RenderProcessFailed,
        });
        Assert.DoesNotContain("/", outcome.Reason!, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task A_Timeout_Is_Retryable_And_The_Worker_Survives_It()
    {
        var socket = SocketPath;
        Skip.If(socket is null, "NUBARCA_RENDER_SOCKET is not set to a running worker.");

        // A one-second bound against a document with thousands of paragraphs.
        // LibreOffice's own startup is most of a second before it lays out
        // anything, so on ordinary hardware this hits the kill path.
        var options = Options(socket!);
        options.MaxOfficeRenderSeconds = 1;

        var outcome = await Renderer(options).RenderAsync(
            new DocumentVisualRenderRequest(
                LargeDocx(), DocumentFormatKind.WordOpenXml, options));

        // A FAST HOST MAY SIMPLY WIN. Asserting that a one-second race is always
        // lost would be a test of this machine's clock speed; the invariant is
        // about what happens WHEN it is lost, so an unexercised kill path is
        // reported as unexercised rather than passed.
        Skip.If(outcome.Ok, "the host converted inside the 1s bound; the kill path did not run.");

        _output.WriteLine($"timeout reason={outcome.Reason}");

        // NEVER A CONTENT VERDICT. A document that timed out on a busy host must
        // not be marked permanently unrenderable — the next pass tries again.
        Assert.False(outcome.IsPermanent);
        Assert.Contains(outcome.Reason, new[]
        {
            DocumentVisualReasons.RenderTimeout,
            DocumentVisualReasons.RenderProcessFailed,
            DocumentVisualReasons.RendererUnavailable,
        });

        // AND THE WORKER SURVIVES IT. A kill that took the worker down with it
        // would turn one slow document into an outage for every other one.
        Assert.True(Renderer(Options(socket!)).CheckReadiness().Ready);
    }

    [SkippableFact]
    public async Task Consecutive_Renders_Do_Not_Accumulate_State()
    {
        var socket = SocketPath;
        Skip.If(socket is null, "NUBARCA_RENDER_SOCKET is not set to a running worker.");

        // A fresh LibreOffice profile per job, and a job directory removed
        // recursively when the job ends. A shared profile is state that survives
        // one document and is read by the next — a correctness problem (a
        // crashed run leaves recovery prompts that hang the following one) and a
        // boundary problem.
        var options = Options(socket!);
        var renderer = Renderer(options);

        for (var i = 0; i < 3; i++)
        {
            var outcome = await renderer.RenderAsync(
                new DocumentVisualRenderRequest(Docx(), DocumentFormatKind.WordOpenXml, options));
            Assert.True(outcome.Ok, $"run {i}: {outcome.Reason}");
        }
    }

    [SkippableFact]
    public async Task A_Killed_Render_Leaves_No_Copy_Of_The_Document_Behind()
    {
        var socket = SocketPath;
        Skip.If(socket is null, "NUBARCA_RENDER_SOCKET is not set to a running worker.");

        var workDir = Environment.GetEnvironmentVariable("NUBARCA_RENDER_WORK_DIR");
        Skip.If(
            workDir is null || !Directory.Exists(workDir),
            "NUBARCA_RENDER_WORK_DIR is not set to the worker's visible job directory.");

        // THE LEAK THIS EXISTS TO CATCH. `subprocess.run(timeout=…)` kills the
        // direct child and not the LibreOffice grandchildren, which keep the job
        // directory open — so the recursive cleanup fails, silently, and a copy
        // of somebody's document sits on disk until the worker restarts.
        var options = Options(socket!);
        options.MaxOfficeRenderSeconds = 1;

        var outcome = await Renderer(options).RenderAsync(
            new DocumentVisualRenderRequest(
                LargeDocx(), DocumentFormatKind.WordOpenXml, options));
        Skip.If(outcome.Ok, "the host converted inside the 1s bound; the kill path did not run.");

        // The kill signals a group and WAITS for it, so the directory is gone by
        // the time the call returns. A short settle allows for a slow unlink.
        for (var attempt = 0; attempt < 10 && Directory.GetDirectories(workDir!).Length > 0; attempt++)
        {
            await Task.Delay(200);
        }

        var leftovers = Directory.GetDirectories(workDir!);
        Assert.True(
            leftovers.Length == 0,
            $"{leftovers.Length} job director(ies) survived a killed render");
    }

    // ---- fixtures --------------------------------------------------------------
    //
    // The SAME synthetic documents Slice 4's extraction tests use. Reusing them
    // is the point: one fixture set means a document that extracts correctly and
    // one that renders correctly are demonstrably the same document, rather than
    // two hand-built approximations that could diverge.

    private static byte[] Docx() => OfficeDocumentFixtures.Contract();

    /// Enough paragraphs that laying them out takes real time. Built here rather
    /// than added to the shared fixtures: nothing about extraction wants a
    /// four-thousand-paragraph document, and this exists only to be slow.
    private static byte[] LargeDocx()
    {
        using var buffer = new MemoryStream();
        using (var document = WordprocessingDocument.Create(
                   buffer, WordprocessingDocumentType.Document, autoSave: true))
        {
            var main = document.AddMainDocumentPart();
            main.Document = new Document();
            var body = main.Document.AppendChild(new Body());
            for (var i = 0; i < 4_000; i++)
            {
                var paragraph = new Paragraph();
                paragraph.AppendChild(new Run(new Text(
                    $"Paragrafo {i}: testo di prova sufficientemente lungo da richiedere "
                    + "un'impaginazione non banale su piu righe.")
                { Space = SpaceProcessingModeValues.Preserve }));
                body.AppendChild(paragraph);
            }
        }
        return buffer.ToArray();
    }

    private static byte[] Xlsx() => OfficeDocumentFixtures.Budget();

    private static byte[] Pptx() => OfficeDocumentFixtures.LaunchPlanForRendering();
}
