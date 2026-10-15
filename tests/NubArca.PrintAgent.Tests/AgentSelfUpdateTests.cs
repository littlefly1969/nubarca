using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using NubArca.PrintAgent.Api;
using NubArca.PrintAgent.Update;

namespace NubArca.PrintAgent.Tests;

/// <summary>
/// The agent only rings the bell for the box's root update service: once per
/// request, pausing claims while the install may restart it, and relaying the
/// service's outcome only in the exact form the server accepts. The root
/// service's fetch installs nothing the server did not name, byte for byte.
/// </summary>
public sealed class AgentSelfUpdateTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"nubarca-agent-update-{Guid.NewGuid():N}");
    private readonly string _state;
    private readonly string _marker;

    public AgentSelfUpdateTests()
    {
        _state = Path.Combine(_directory, "box");
        _marker = Path.Combine(_directory, "self-update");
        Directory.CreateDirectory(_state);
        File.WriteAllText(_marker, "");
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private AgentSelfUpdate Agent() => new(_state, _marker, NullLogger.Instance);

    private static AgentUpdateOffer Offer(string requestId = "1700000000000") =>
        new("0.9.0", "nubarca-print-agent-0.9.0-linux-x64.zip", new string('a', 64), 1024, requestId);

    private string RequestPath => Path.Combine(_state, AgentSelfUpdate.RequestFile);

    [Fact]
    public void Without_The_Root_Service_Nothing_Is_Asked()
    {
        if (!OperatingSystem.IsLinux()) return;
        File.Delete(_marker);
        var agent = Agent();
        Assert.False(agent.Supported);
        agent.Handle(Offer(), DateTimeOffset.UtcNow);
        Assert.False(File.Exists(RequestPath));
        Assert.False(agent.Pending(DateTimeOffset.UtcNow));
    }

    [Fact]
    public void An_Offer_Rings_Once_And_Pauses_Claims_For_A_Bounded_Time()
    {
        if (!OperatingSystem.IsLinux()) return;
        var agent = Agent();
        var now = DateTimeOffset.UtcNow;
        File.WriteAllText(Path.Combine(_state, AgentSelfUpdate.ResultFile), "failed 0.9.0 1\n");

        agent.Handle(Offer(), now);
        var request = JsonDocument.Parse(File.ReadAllText(RequestPath)).RootElement;
        Assert.Equal("0.9.0", request.GetProperty("version").GetString());
        Assert.Equal("1700000000000", request.GetProperty("requestId").GetString());
        // An earlier attempt's outcome is not relayed as this one's.
        Assert.False(File.Exists(Path.Combine(_state, AgentSelfUpdate.ResultFile)));
        Assert.True(agent.Pending(now.AddMinutes(1)));
        Assert.False(agent.Pending(now + AgentSelfUpdate.PendingLimit));

        // The same request is not rung again, even after the root service took the bell down.
        File.Delete(RequestPath);
        agent.Handle(Offer(), now.AddMinutes(20));
        Assert.False(File.Exists(RequestPath));

        // A withdrawn offer resumes work at once.
        agent.Handle(Offer("1700000000001"), now.AddMinutes(21));
        Assert.True(agent.Pending(now.AddMinutes(22)));
        agent.Handle(null, now.AddMinutes(22));
        Assert.False(agent.Pending(now.AddMinutes(22)));
    }

    [Theory]
    [InlineData("ok 0.9.0 1700000000000\n", "ok 0.9.0 1700000000000")]
    [InlineData("failed 0.9.0 42", "failed 0.9.0 42")]
    [InlineData("ok 0.9.0", null)]
    [InlineData("done 0.9.0 42", null)]
    [InlineData("ok 0.9.0 42 /etc/shadow", null)]
    public void Only_A_Well_Formed_Outcome_Is_Relayed(string content, string? relayed)
    {
        if (!OperatingSystem.IsLinux()) return;
        File.WriteAllText(Path.Combine(_state, AgentSelfUpdate.ResultFile), content);
        Assert.Equal(relayed, Agent().ReadResult());
    }

    [Fact]
    public void The_Health_Mark_Carries_The_Three_Part_Version()
    {
        if (!OperatingSystem.IsLinux()) return;
        Agent().MarkHeartbeat(new Version(0, 9, 0, 0).ToString());
        Assert.Equal("0.9.0\n", File.ReadAllText(Path.Combine(_state, AgentSelfUpdate.HeartbeatFile)));
    }

    private sealed class Server(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.True(request.Headers.Contains("X-NubArca-Print-Credential"));
            return Task.FromResult(respond(request));
        }
    }

    private static PrintAgentApiClient Client(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var api = new PrintAgentApiClient(new HttpClient(new Server(respond)) { BaseAddress = new Uri("https://nubarca.test/") });
        api.SetCredential("station-credential");
        return api;
    }

    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(value), System.Text.Encoding.UTF8, "application/json"),
    };

    private static PrintAgentApiClient Serving(byte[] bundle, byte[] named, string version = "0.9.0") => Client(request =>
        request.RequestUri!.AbsolutePath.EndsWith("/bundle", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bundle) }
            : Json(new
            {
                version, fileName = $"nubarca-print-agent-{version}-linux-x64.zip",
                sha256 = Convert.ToHexStringLower(SHA256.HashData(named)), sizeBytes = named.Length,
                requestId = "1700000000000",
            }));

    [Fact]
    public async Task The_Fetch_Stages_Exactly_The_Bundle_The_Server_Named()
    {
        var bundle = RandomNumberGenerator.GetBytes(4096);
        var output = Path.Combine(_directory, "staging");
        Assert.Equal(0, await AgentSelfUpdate.FetchAsync(Serving(bundle, bundle), output, CancellationToken.None));
        Assert.Equal(bundle, File.ReadAllBytes(Path.Combine(output, "nubarca-print-agent-0.9.0-linux-x64.zip")));
        var offer = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "offer.json"))).RootElement;
        Assert.Equal("0.9.0", offer.GetProperty("version").GetString());
        Assert.Equal("1700000000000", offer.GetProperty("requestId").GetString());
    }

    [Fact]
    public async Task Other_Bytes_Or_A_Malformed_Offer_Stage_Nothing()
    {
        var output = Path.Combine(_directory, "staging");
        var named = RandomNumberGenerator.GetBytes(4096);
        var other = RandomNumberGenerator.GetBytes(4096);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            AgentSelfUpdate.FetchAsync(Serving(other, named), output, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            AgentSelfUpdate.FetchAsync(Serving(named, named, "../0.9.0"), output, CancellationToken.None));
        Assert.Empty(Directory.Exists(output) ? Directory.GetFiles(output) : []);
    }

    [Fact]
    public async Task Nothing_Offered_Fetches_Nothing()
    {
        var api = Client(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        Assert.Equal(3, await AgentSelfUpdate.FetchAsync(api, Path.Combine(_directory, "staging"), CancellationToken.None));
    }
}
