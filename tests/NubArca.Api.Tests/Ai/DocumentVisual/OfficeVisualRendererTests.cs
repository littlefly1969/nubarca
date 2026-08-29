using System.Buffers.Binary;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NubArca.Api.Ai.DocumentVisual;
using NubArca.Api.Ai.Documents;
using Xunit;

namespace NubArca.Api.Tests.Ai.DocumentVisual;

// THE OFFICE RENDERER CLIENT, against a socket that answers.
//
// LibreOffice does not run here — that is the entire design — so what can be
// tested in process is the CLIENT's half of the isolation: what the protocol
// can express, what it refuses to allocate, how a worker's answers are mapped
// into NubArca's own sanitized vocabulary, and the provenance rule that strips
// the intermediate PDF's page numbers.
//
// A fake worker rather than a mock, because the interesting assertions are
// about the BYTES that cross the socket. A mock would let the client's framing
// be wrong in the same way the test expects.
public sealed class OfficeVisualRendererTests : IDisposable
{
    private const int Magic = 0x4E424452; // "NBDR"

    private readonly string _socketPath;

    public OfficeVisualRendererTests()
    {
        // A short path: Unix domain socket names are capped near 108 bytes, and
        // the temp directory plus a GUID can exceed it.
        _socketPath = Path.Combine(Path.GetTempPath(), $"nbv{Guid.NewGuid():N}"[..24] + ".sock");
    }

    public void Dispose()
    {
        try { File.Delete(_socketPath); } catch (IOException) { }
    }

    private DocumentVisualOptions Options() => new()
    {
        Enabled = true,
        RenderOfficeEnabled = true,
        OfficeRendererSocketPath = _socketPath,
        MaxOfficeRenderSeconds = 5,
    };

    private OfficeVisualRenderer Renderer(DocumentVisualOptions options)
        => new(
            Microsoft.Extensions.Options.Options.Create(options),
            new PdfVisualRenderer(
                Microsoft.Extensions.Options.Options.Create(options),
                NullLogger<PdfVisualRenderer>.Instance),
            NullLogger<OfficeVisualRenderer>.Instance);

    // ---- the protocol's vocabulary -------------------------------------------

    [Fact]
    public async Task The_Request_Carries_Only_A_Format_Ordinal_And_Bytes()
    {
        // WHAT THE PROTOCOL CANNOT SAY is the security argument. The fixed
        // 20-byte header holds a magic, a version, an op, a format ORDINAL, two
        // bounds and a length — and then the document. There is nowhere in it
        // for a path, a filename, a command, an import filter or a URL.
        using var worker = new FakeWorker(_socketPath, PdfFixtures.Pages(2));
        var options = Options();

        var source = System.Text.Encoding.UTF8.GetBytes("SENSITIVE-DOCX-CONTENT");
        await Renderer(options).RenderAsync(
            new DocumentVisualRenderRequest(source, DocumentFormatKind.WordOpenXml, options));

        var header = worker.LastHeader!;
        Assert.Equal(20, header.Length);
        Assert.Equal(Magic, BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(0, 4)));
        Assert.Equal(1, header[4]);                       // version
        Assert.Equal(2, header[5]);                       // op = render
        Assert.Equal(1, header[6]);                       // format = docx
        Assert.Equal((uint)source.Length, BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(16, 4)));

        // The payload is the document and nothing else — no filename prefix, no
        // envelope, no owner.
        Assert.Equal(source, worker.LastPayload);
    }

    [Theory]
    [InlineData(DocumentFormatKind.WordOpenXml, 1)]
    [InlineData(DocumentFormatKind.SpreadsheetOpenXml, 2)]
    [InlineData(DocumentFormatKind.PresentationOpenXml, 3)]
    public async Task Each_Office_Family_Maps_To_Its_Own_Closed_Ordinal(
        DocumentFormatKind format, byte expected)
    {
        using var worker = new FakeWorker(_socketPath, PdfFixtures.Pages(1));
        var options = Options();

        await Renderer(options).RenderAsync(
            new DocumentVisualRenderRequest(new byte[] { 1, 2, 3 }, format, options));

        Assert.Equal(expected, worker.LastHeader![6]);
    }

    [Fact]
    public async Task A_Format_It_Does_Not_Claim_Never_Reaches_The_Socket()
    {
        using var worker = new FakeWorker(_socketPath, PdfFixtures.Pages(1));
        var options = Options();

        var outcome = await Renderer(options).RenderAsync(
            new DocumentVisualRenderRequest(new byte[] { 1 }, DocumentFormatKind.Pdf, options));

        Assert.False(outcome.Ok);
        Assert.Equal(DocumentVisualReasons.FormatUnsupported, outcome.Reason);
        Assert.Equal(0, worker.Connections);
    }

    // ---- provenance -----------------------------------------------------------

    [Fact]
    public async Task Rendered_Office_Pages_Carry_No_Source_Page_Or_Locator()
    {
        // A PDF page number is provenance when the PDF is the OWNER'S document.
        // Here the PDF is an artefact of LibreOffice's pagination, so "page 4"
        // describes where this build's layout engine broke a DOCX. Citing it
        // would attribute NubArca's rendering to the author — Slice 4's typed
        // text provenance stays the authority.
        using var worker = new FakeWorker(_socketPath, PdfFixtures.Pages(3));
        var options = Options();

        var outcome = await Renderer(options).RenderAsync(
            new DocumentVisualRenderRequest(
                new byte[] { 1, 2, 3 }, DocumentFormatKind.WordOpenXml, options));

        Assert.True(outcome.Ok, outcome.Reason);
        Assert.Equal(DocumentVisualRenderProfiles.LibreOfficePdf, outcome.Artifact!.RenderProfileKey);
        Assert.Equal(3, outcome.Artifact.Units.Count);
        Assert.All(outcome.Artifact.Units, u =>
        {
            Assert.Equal(DocumentVisualRenderKinds.OfficeRenderedPage, u.RenderKind);
            Assert.Null(u.SourcePage);
            Assert.Null(u.SourceLocator);
            Assert.NotEmpty(u.Png);
        });
        // Ordinals are still dense and ordered — they are retrieval units, just
        // not citations.
        Assert.Equal(new[] { 0, 1, 2 }, outcome.Artifact.Units.Select(u => u.Ordinal).ToArray());
    }

    // ---- failure mapping -------------------------------------------------------

    [Theory]
    [InlineData((byte)1, (ushort)1, DocumentVisualReasons.FormatUnsupported, true)]
    [InlineData((byte)1, (ushort)2, DocumentVisualReasons.InvalidSource, true)]
    [InlineData((byte)1, (ushort)3, DocumentVisualReasons.OutputTooLarge, true)]
    [InlineData((byte)2, (ushort)4, DocumentVisualReasons.RenderTimeout, false)]
    [InlineData((byte)2, (ushort)5, DocumentVisualReasons.RenderProcessFailed, false)]
    [InlineData((byte)2, (ushort)6, DocumentVisualReasons.RendererUnavailable, false)]
    public async Task Worker_Outcomes_Map_To_Sanitized_Reasons(
        byte status, ushort reason, string expected, bool permanent)
    {
        // AN ORDINAL ON THE WIRE, never a string. Nothing the worker produces —
        // including a native error message carrying a temp path — can become a
        // reason NubArca reports.
        using var worker = new FakeWorker(_socketPath, Array.Empty<byte>())
        {
            Status = status,
            Reason = reason,
        };
        var options = Options();

        var outcome = await Renderer(options).RenderAsync(
            new DocumentVisualRenderRequest(
                new byte[] { 1, 2, 3 }, DocumentFormatKind.WordOpenXml, options));

        Assert.False(outcome.Ok);
        Assert.Equal(expected, outcome.Reason);
        Assert.Equal(permanent, outcome.IsPermanent);
    }

    [Fact]
    public async Task A_Worker_Announcing_More_Than_The_Bound_Is_Refused_Before_Allocation()
    {
        // A compromised — or simply wrong — worker must not be able to make the
        // API allocate four gigabytes by SAYING it will send them. The declared
        // length is checked against the configured ceiling before a buffer
        // exists.
        using var worker = new FakeWorker(_socketPath, Array.Empty<byte>())
        {
            LieAboutLength = 4L * 1024 * 1024 * 1024 - 1,
        };
        var options = Options();
        options.MaxRenderedPdfBytes = 64 * 1024;

        var outcome = await Renderer(options).RenderAsync(
            new DocumentVisualRenderRequest(
                new byte[] { 1, 2, 3 }, DocumentFormatKind.WordOpenXml, options));

        Assert.False(outcome.Ok);
        Assert.Equal(DocumentVisualReasons.RendererUnavailable, outcome.Reason);
        Assert.False(outcome.IsPermanent);
    }

    [Fact]
    public async Task A_Worker_That_Is_Not_There_Is_An_Environment_State()
    {
        // No socket file: the worker is not deployed. Office documents stay
        // text-only and NOTHING is recorded against them, because a container
        // that has not been started must not mark somebody's contract
        // permanently unrenderable.
        var options = Options();
        options.OfficeRendererSocketPath = Path.Combine(Path.GetTempPath(), "nubarca-absent.sock");

        var renderer = Renderer(options);
        Assert.False(renderer.CheckReadiness().Ready);

        var outcome = await renderer.RenderAsync(
            new DocumentVisualRenderRequest(
                new byte[] { 1, 2, 3 }, DocumentFormatKind.WordOpenXml, options));

        Assert.False(outcome.Ok);
        Assert.False(outcome.IsPermanent);
        Assert.Equal(DocumentVisualReasons.RendererUnavailable, outcome.Reason);
    }

    [Fact]
    public void With_Office_Rendering_Off_The_Renderer_Reports_Disabled()
    {
        var options = Options();
        options.RenderOfficeEnabled = false;

        var readiness = Renderer(options).CheckReadiness();

        Assert.False(readiness.Ready);
        Assert.Equal(DocumentVisualReasons.Disabled, readiness.Reason);
    }

    [Fact]
    public async Task An_Empty_Pdf_From_The_Worker_Is_A_Content_Verdict()
    {
        using var worker = new FakeWorker(_socketPath, Array.Empty<byte>());
        var options = Options();

        var outcome = await Renderer(options).RenderAsync(
            new DocumentVisualRenderRequest(
                new byte[] { 1, 2, 3 }, DocumentFormatKind.WordOpenXml, options));

        Assert.False(outcome.Ok);
        Assert.Equal(DocumentVisualReasons.InvalidSource, outcome.Reason);
    }

    // ---- the registered render identity ----------------------------------------

    [Fact]
    public void The_Office_Render_Identity_Names_Its_Engine_And_No_Timestamp()
    {
        var renderer = Renderer(Options());

        Assert.Equal(DocumentVisualRenderProfiles.LibreOfficePdf, renderer.RenderProfileKey);
        Assert.True(DocumentVisualRenderProfiles.IsKnown(renderer.RenderProfileKey));
        Assert.Equal(
            new[]
            {
                DocumentFormatKind.WordOpenXml,
                DocumentFormatKind.SpreadsheetOpenXml,
                DocumentFormatKind.PresentationOpenXml,
            },
            renderer.Formats.ToArray());
    }

    // ---- a worker that answers ---------------------------------------------------

    /// A Unix-socket server speaking the worker's exact framing. It exists to
    /// prove the CLIENT's half: what it sends, what it refuses to accept, and how
    /// it maps what comes back.
    private sealed class FakeWorker : IDisposable
    {
        private readonly Socket _listener;
        private readonly CancellationTokenSource _stopping = new();
        private readonly byte[] _pdf;

        public byte Status { get; init; }
        public ushort Reason { get; init; }
        public long LieAboutLength { get; init; }

        public byte[]? LastHeader { get; private set; }
        public byte[]? LastPayload { get; private set; }
        public int Connections { get; private set; }

        public FakeWorker(string path, byte[] pdf)
        {
            _pdf = pdf;
            _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            _listener.Bind(new UnixDomainSocketEndPoint(path));
            _listener.Listen(4);
            _ = Task.Run(AcceptAsync);
        }

        private async Task AcceptAsync()
        {
            while (!_stopping.IsCancellationRequested)
            {
                Socket connection;
                try
                {
                    connection = await _listener.AcceptAsync(_stopping.Token);
                }
                catch (Exception)
                {
                    return;
                }

                Connections++;
                using (connection)
                {
                    try
                    {
                        var header = new byte[20];
                        await ReceiveExactly(connection, header);
                        LastHeader = header;

                        var length = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(16, 4));
                        var payload = new byte[length];
                        if (length > 0) await ReceiveExactly(connection, payload);
                        LastPayload = payload;

                        var body = Status == 0 ? _pdf : Array.Empty<byte>();
                        var declared = LieAboutLength > 0 ? (uint)LieAboutLength : (uint)body.Length;

                        var response = new byte[12];
                        BinaryPrimitives.WriteInt32BigEndian(response.AsSpan(0, 4), Magic);
                        response[4] = 1;
                        response[5] = Status;
                        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(6, 2), Reason);
                        BinaryPrimitives.WriteUInt32BigEndian(response.AsSpan(8, 4), declared);

                        await connection.SendAsync(response, SocketFlags.None);
                        if (body.Length > 0) await connection.SendAsync(body, SocketFlags.None);
                    }
                    catch (Exception)
                    {
                        // A torn connection is the client giving up, which several
                        // of these tests do on purpose.
                    }
                }
            }
        }

        private static async Task ReceiveExactly(Socket socket, Memory<byte> buffer)
        {
            var offset = 0;
            while (offset < buffer.Length)
            {
                var read = await socket.ReceiveAsync(buffer[offset..], SocketFlags.None);
                if (read == 0) throw new IOException("closed");
                offset += read;
            }
        }

        public void Dispose()
        {
            _stopping.Cancel();
            _listener.Dispose();
            _stopping.Dispose();
        }
    }
}
