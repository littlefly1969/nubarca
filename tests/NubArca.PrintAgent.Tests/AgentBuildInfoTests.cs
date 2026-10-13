namespace NubArca.PrintAgent.Tests;

/// <summary>
/// The build an agent reports is the commit its CI release wrote beside it —
/// nothing else: a development build, or a file someone edited into nonsense,
/// reports no build rather than a wrong one.
/// </summary>
public sealed class AgentBuildInfoTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"nubarca-agent-build-{Guid.NewGuid():N}");

    public AgentBuildInfoTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void The_Release_Commit_Is_Read_As_It_Was_Written()
    {
        File.WriteAllText(Path.Combine(_directory, "source-commit.txt"), "ED46FF75108E1698CA009C40B331B241ADA47871\n");
        Assert.Equal("ed46ff75108e1698ca009c40b331b241ada47871", AgentBuildInfo.ReadBuild(_directory));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ed46ff7")]
    [InlineData("not-a-commit-not-a-commit-not-a-commit-x")]
    public void Anything_Else_Is_No_Build(string? content)
    {
        if (content is not null) File.WriteAllText(Path.Combine(_directory, "source-commit.txt"), content);
        Assert.Null(AgentBuildInfo.ReadBuild(_directory));
    }
}
