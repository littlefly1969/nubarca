using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using NubArca.PrintAgent;
using NubArca.PrintAgent.Adapters;
using NubArca.PrintAgent.Api;
using NubArca.PrintAgent.Execution;
using NubArca.PrintAgent.Journal;
using NubArca.PrintAgent.Security;

namespace NubArca.PrintAgent.Tests;

public sealed class AgentExecutionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"nubarca-print-agent-{Guid.NewGuid():N}");
    public AgentExecutionTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    [Fact]
    public async Task Fake_Adapter_Produces_Deterministic_Copy()
    {
        var source = Path.Combine(_root, "source.png");
        await File.WriteAllBytesAsync(source, [1, 2, 3, 4]);
        var adapter = new FakePrinterAdapter(Path.Combine(_root, "out"));
        var result = await adapter.SubmitAsync(new(Guid.Parse("11111111-1111-1111-1111-111111111111"),
            "fake-10x15", source, "image/png", "10x15"), default);
        Assert.True(result.Accepted);
        Assert.Equal(1, adapter.SubmissionCount);
        Assert.Equal([1, 2, 3, 4], await File.ReadAllBytesAsync(Path.Combine(_root, "out",
            "11111111111111111111111111111111.png")));
    }

    [Fact]
    public async Task Ack_Failure_Is_Retried_Without_Resubmitting()
    {
        var handler = new RecordingHandler { FailFirstResult = true };
        var (coordinator, journal, adapter, api) = await BuildAsync(handler);
        var claim = new AgentClaim(Guid.NewGuid(), "claim", "diagnostic", "10x15",
            "/artifact", 4, "image/png", "fake-10x15");
        await coordinator.ExecuteAsync(claim, default);
        Assert.Equal(1, adapter.SubmissionCount);
        Assert.Equal(LocalExecutionStates.Completed, (await journal.LoadPendingAsync(default)).Single().State);

        var retry = new AgentExecutionCoordinator(api, adapter, journal, Options(),
            NullLogger<AgentExecutionCoordinator>.Instance);
        await retry.RecoverAsync(default);
        Assert.Equal(1, adapter.SubmissionCount);
        Assert.Empty(await journal.LoadPendingAsync(default));
        Assert.Equal(2, handler.ResultCalls);
    }

    [Fact]
    public async Task Ambiguous_Submitting_On_Restart_Becomes_DeliveryUnknown_Without_Print()
    {
        var handler = new RecordingHandler();
        var (coordinator, journal, adapter, _) = await BuildAsync(handler);
        var artifact = Path.Combine(_root, "temp", "ambiguous.png");
        await File.WriteAllBytesAsync(artifact, [1, 2, 3, 4]);
        var entry = new JournalEntry(Guid.NewGuid(), "claim", artifact, "fake-10x15",
            "image/png", "10x15", LocalExecutionStates.Submitting, null, null);
        await journal.MarkSubmittingAsync(entry, default);

        await coordinator.RecoverAsync(default);

        Assert.Equal(0, adapter.SubmissionCount);
        Assert.Empty(await journal.LoadPendingAsync(default));
        Assert.Equal("delivery-unknown", handler.LastOutcome);
    }

    [Fact]
    public async Task Definite_Adapter_Failure_Is_Acknowledged_As_Failed()
    {
        var handler = new RecordingHandler();
        var (coordinator, journal, adapter, _) = await BuildAsync(handler);
        adapter.FailNextSubmission = true;
        await coordinator.ExecuteAsync(new AgentClaim(Guid.NewGuid(), "claim", "diagnostic", "10x15",
            "/artifact", 4, "image/png", "fake-10x15"), default);
        Assert.Equal("failed", handler.LastOutcome);
        Assert.Empty(await journal.LoadPendingAsync(default));
    }

    [Fact]
    public async Task Linux_Credential_Store_Uses_OwnerOnly_Mode_And_Refuses_Weaker_Mode()
    {
        if (!OperatingSystem.IsLinux()) return;
        var path = Path.Combine(_root, "credential.bin");
        var store = new LinuxFileCredentialStore(path);
        await store.SaveAsync("station.credential", default);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        Assert.Equal("station.credential", await store.LoadAsync(default));

        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.GroupRead);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.LoadAsync(default));
    }

    private async Task<(AgentExecutionCoordinator Coordinator, ExecutionJournal Journal,
        FakePrinterAdapter Adapter, PrintAgentApiClient Api)> BuildAsync(RecordingHandler handler)
    {
        var options = Options();
        Directory.CreateDirectory(options.TemporaryPath);
        var journal = new ExecutionJournal(options.JournalPath);
        await journal.InitializeAsync(default);
        var api = new PrintAgentApiClient(new HttpClient(handler) { BaseAddress = new Uri("https://example.invalid/") });
        api.SetCredential("station.credential");
        var adapter = new FakePrinterAdapter(Path.Combine(_root, "out"));
        var coordinator = new AgentExecutionCoordinator(api, adapter, journal, options,
            NullLogger<AgentExecutionCoordinator>.Instance);
        return (coordinator, journal, adapter, api);
    }

    private PrintAgentOptions Options() => new()
    {
        JournalPath = Path.Combine(_root, "journal.db"),
        TemporaryPath = Path.Combine(_root, "temp"),
        FakeOutputPath = Path.Combine(_root, "out"),
        MaxArtifactBytes = 1024,
        MaxTemporaryBytes = 4096,
    };

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public bool FailFirstResult { get; set; }
        public int ResultCalls { get; private set; }
        public string? LastOutcome { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get)
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3, 4]) };
            if (request.RequestUri!.AbsolutePath.EndsWith("/result", StringComparison.Ordinal))
            {
                ResultCalls++;
                var json = await request.Content!.ReadAsStringAsync(cancellationToken);
                LastOutcome = System.Text.Json.JsonDocument.Parse(json).RootElement
                    .GetProperty("outcome").GetString();
                if (FailFirstResult && ResultCalls == 1) return new(HttpStatusCode.InternalServerError);
            }
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }
    }
}
