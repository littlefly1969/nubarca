using System.Diagnostics;

namespace NubArca.Api.Tests.Branding;

// Binds scripts/check-nubarca-identity.sh into the canonical test matrix, so a
// drifted identifier or a leaked installation-specific value fails `dotnet test`
// rather than only a pre-commit step somebody can forget to run.
//
// Two facts are asserted, and they are different:
//   * the contract's OWN detectors are correct (--self-test), and
//   * the tracked tree actually satisfies the contract (the plain run).
// A checker that passes while asserting nothing is the failure mode this guards.
public class NubArcaIdentityTests
{
    [Fact]
    public void Identity_Contract_Self_Test_Passes()
    {
        var (exit, stdout, stderr) = RunContract("--self-test");
        Assert.True(exit == 0, $"identity contract self-test failed:\n{stdout}\n{stderr}");
        Assert.Contains("cases correct", stdout);
    }

    [Fact]
    public void Tracked_Source_Satisfies_The_NubArca_Identity_Contract()
    {
        var (exit, stdout, stderr) = RunContract();
        Assert.True(exit == 0, $"tracked source violates the identity contract:\n{stdout}\n{stderr}");
    }

    private static (int Exit, string Stdout, string Stderr) RunContract(params string[] args)
    {
        var repositoryRoot = FindRepositoryRoot();
        var script = Path.Combine(repositoryRoot, "scripts", "check-nubarca-identity.sh");
        Assert.True(File.Exists(script), $"identity contract not found at {script}");

        var startInfo = new ProcessStartInfo("bash")
        {
            WorkingDirectory = repositoryRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(script);
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo);
        Assert.NotNull(process);
        var stdout = process!.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(120_000), "identity contract did not finish in 120 s");
        return (process.ExitCode, stdout, stderr);
    }

    // Walk up from the test assembly to the directory holding the solution. The
    // test working directory is the build output, several levels below the root.
    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "NubArca.sln")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        throw new InvalidOperationException("could not locate the repository root (NubArca.sln)");
    }
}
