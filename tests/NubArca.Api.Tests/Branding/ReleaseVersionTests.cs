using System.Diagnostics;

namespace NubArca.Api.Tests.Branding;

// Binds scripts/check-release-version.py into `dotnet test`. CI runs the real
// comparison against main's tip; here its own comparison is proven, so a
// checker that waves every version through cannot go unnoticed.
public class ReleaseVersionTests
{
    [Fact]
    public void Release_Version_Check_Self_Test_Passes()
    {
        var root = FindRepositoryRoot();
        var startInfo = new ProcessStartInfo("python3")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(Path.Combine(root, "scripts", "check-release-version.py"));
        startInfo.ArgumentList.Add("--self-test");

        using var process = Process.Start(startInfo);
        Assert.NotNull(process);
        var stdout = process!.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(60_000), "release version check did not finish in 60 s");
        Assert.True(process.ExitCode == 0, $"release version self-test failed:\n{stdout}\n{stderr}");
        Assert.Contains("cases correct", stdout);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "NubArca.sln")))
        {
            directory = directory.Parent;
        }
        Assert.NotNull(directory);
        return directory!.FullName;
    }
}
