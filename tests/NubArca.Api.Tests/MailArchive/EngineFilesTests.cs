using System.Diagnostics;
using NubArca.Api.MailArchive;
using Xunit;

namespace NubArca.Api.Tests.MailArchive;

// The worker reads the engine's output through EngineFiles only: a parser
// compromised by a malformed PST must not be able to make the worker read a
// file outside the staging directory (links, FIFOs, directories, escapes).
public sealed class EngineFilesTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("nubarca-engine-files-").FullName;
    private readonly string _staging;
    private readonly string _secret;

    public EngineFilesTests()
    {
        _staging = Directory.CreateDirectory(Path.Combine(_root, "serve")).FullName;
        Directory.CreateDirectory(Path.Combine(_staging, "i1"));
        File.WriteAllText(Path.Combine(_staging, "i1", "att-0.bin"), "attachment");
        var outside = Directory.CreateDirectory(Path.Combine(_root, "outside")).FullName;
        _secret = Path.Combine(outside, "secret.txt");
        File.WriteAllText(_secret, "worker-only secret");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static string Read(Stream stream)
    {
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private void AssertUnsafe(string relative)
    {
        var ex = Assert.Throws<PstEngineException>(() => EngineFiles.Open(_staging, "i1", relative).Dispose());
        Assert.Equal(EngineFiles.UnsafeOutput, ex.Code);
    }

    [Fact]
    public void A_regular_file_in_the_request_directory_opens()
    {
        using var stream = EngineFiles.Open(_staging, "i1", "i1/att-0.bin");
        Assert.Equal(10, stream.Length);
        Assert.Equal("attachment", Read(stream));
    }

    [Theory]
    [InlineData("i2/att-0.bin")]
    [InlineData("i1/../i2/x")]
    [InlineData("i1/./att-0.bin")]
    [InlineData("i1//att-0.bin")]
    [InlineData("/etc/passwd")]
    [InlineData("i1")]
    [InlineData("i1/..\\x")]
    public void A_reference_outside_the_request_directory_is_refused(string relative) => AssertUnsafe(relative);

    [Fact]
    public void A_missing_file_is_not_found()
    {
        Assert.Throws<FileNotFoundException>(() => EngineFiles.Open(_staging, "i1", "i1/att-9.bin").Dispose());
    }

    [SkippableFact]
    public void A_symlink_to_a_file_outside_is_refused_and_not_read()
    {
        Skip.IfNot(OperatingSystem.IsLinux());
        File.CreateSymbolicLink(Path.Combine(_staging, "i1", "att-1.bin"), _secret);
        AssertUnsafe("i1/att-1.bin");
        // Relative links that stay inside are links too.
        File.CreateSymbolicLink(Path.Combine(_staging, "i1", "att-2.bin"), "att-0.bin");
        AssertUnsafe("i1/att-2.bin");
    }

    [SkippableFact]
    public void A_symlinked_request_directory_is_refused()
    {
        Skip.IfNot(OperatingSystem.IsLinux());
        Directory.CreateSymbolicLink(Path.Combine(_staging, "i3"), Path.GetDirectoryName(_secret)!);
        var ex = Assert.Throws<PstEngineException>(() => EngineFiles.Open(_staging, "i3", "i3/secret.txt").Dispose());
        Assert.Equal(EngineFiles.UnsafeOutput, ex.Code);
    }

    [SkippableFact]
    public void A_hard_link_to_another_file_is_refused()
    {
        Skip.IfNot(OperatingSystem.IsLinux());
        Run("ln", _secret, Path.Combine(_staging, "i1", "att-3.bin"));
        AssertUnsafe("i1/att-3.bin");
    }

    [SkippableFact]
    public void A_fifo_or_a_directory_is_refused_without_blocking()
    {
        Skip.IfNot(OperatingSystem.IsLinux());
        Run("mkfifo", Path.Combine(_staging, "i1", "att-4.bin"));
        AssertUnsafe("i1/att-4.bin");
        Directory.CreateDirectory(Path.Combine(_staging, "i1", "att-5.bin"));
        AssertUnsafe("i1/att-5.bin");
    }

    [SkippableFact]
    public void The_component_walk_used_without_openat2_gives_the_same_answers()
    {
        Skip.IfNot(OperatingSystem.IsLinux());
        using (var ok = EngineFiles.OpenWithWalkForTests(_staging, "i1", "i1/att-0.bin"))
        {
            Assert.Equal("attachment", Read(ok));
        }
        File.CreateSymbolicLink(Path.Combine(_staging, "i1", "att-1.bin"), _secret);
        Assert.Equal(EngineFiles.UnsafeOutput, Assert.Throws<PstEngineException>(
            () => EngineFiles.OpenWithWalkForTests(_staging, "i1", "i1/att-1.bin").Dispose()).Code);
        Directory.CreateSymbolicLink(Path.Combine(_staging, "i3"), Path.GetDirectoryName(_secret)!);
        Assert.Equal(EngineFiles.UnsafeOutput, Assert.Throws<PstEngineException>(
            () => EngineFiles.OpenWithWalkForTests(_staging, "i3", "i3/secret.txt").Dispose()).Code);
    }

    private static void Run(string tool, params string[] args)
    {
        var info = new ProcessStartInfo(tool) { UseShellExecute = false };
        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }
        using var process = Process.Start(info)!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }
}
