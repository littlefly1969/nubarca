using NubArca.PrintAgent.Execution;
using NubArca.PrintAgent.Networking;

namespace NubArca.PrintAgent.Tests;

public sealed class PrintBoxPlatformTests
{
    [Fact]
    public async Task Arguments_Reach_The_Program_Verbatim_Without_A_Shell()
    {
        if (!OperatingSystem.IsLinux()) return;
        const string hostile = "a;b && $(id) `id` 'q' \"d\" | > /tmp/x";
        var result = await new ProcessRunner().RunAsync("printf", ["%s", hostile], TimeSpan.FromSeconds(10), default);
        Assert.True(result.Succeeded);
        Assert.Equal(hostile, result.StdOut);
    }

    [Fact]
    public async Task Tools_Answer_In_The_C_Locale()
    {
        if (!OperatingSystem.IsLinux()) return;
        var result = await new ProcessRunner().RunAsync("printenv", ["LC_ALL"], TimeSpan.FromSeconds(10), default);
        Assert.Equal("C", result.StdOut.Trim());
    }

    [Fact]
    public async Task A_Hung_Tool_Times_Out_And_A_Missing_One_Is_Not_Found()
    {
        if (!OperatingSystem.IsLinux()) return;
        var runner = new ProcessRunner();
        var hung = await runner.RunAsync("sleep", ["10"], TimeSpan.FromMilliseconds(200), default);
        Assert.True(hung.TimedOut);
        var missing = await runner.RunAsync("nubarca-no-such-tool", [], TimeSpan.FromSeconds(5), default);
        Assert.True(missing.NotFound);
    }

    [Fact]
    public void Provisioning_Is_Linux_Only_And_Validated()
    {
        static PrintAgentOptions With(Action<NetworkProvisioningOptions> change)
        {
            var options = new PrintAgentOptions { Adapter = "fake" };
            options.NetworkProvisioning.Enabled = true;
            change(options.NetworkProvisioning);
            return options;
        }

        var windows = With(_ => { });
        Assert.Contains("requires Linux",
            Assert.Throws<InvalidOperationException>(() => windows.NormalizeAndValidate(isLinux: false)).Message);

        With(_ => { }).NormalizeAndValidate(isLinux: true);
        With(p => p.AccessPointPassword = "eight-ch").NormalizeAndValidate(isLinux: true);
        Assert.Throws<InvalidOperationException>(() => With(p => p.ConnectionGraceSeconds = 1).NormalizeAndValidate(true));
        Assert.Throws<InvalidOperationException>(() => With(p => p.AccessPointPrefix = "Bad Prefix").NormalizeAndValidate(true));
        Assert.Throws<InvalidOperationException>(() => With(p => p.AccessPointPassword = "short").NormalizeAndValidate(true));
        Assert.Throws<InvalidOperationException>(() => With(p => p.WebPort = 80).NormalizeAndValidate(true));

        // Off, it is not even looked at — on Windows included.
        var off = new PrintAgentOptions { Adapter = "fake" };
        off.NormalizeAndValidate(isLinux: false);
    }

    [Fact]
    public void The_Box_Installer_Keeps_Secrets_Off_The_Command_Line_And_Sudo_Out()
    {
        var linux = Path.Combine(AppContext.BaseDirectory, "linux");
        var installer = File.ReadAllText(Path.Combine(linux, "install-print-box.sh"));
        var rules = File.ReadAllText(Path.Combine(linux, "nubarca-print-box.rules"));
        var unit = File.ReadAllText(Path.Combine(linux, "nubarca-print-agent@.service"));

        Assert.Contains("--token-stdin", installer, StringComparison.Ordinal);
        Assert.Contains("read -r -s -p 'Password for the setup Wi-Fi", installer, StringComparison.Ordinal);
        Assert.Contains("\"Adapter\": \"cups\"", installer, StringComparison.Ordinal);
        Assert.DoesNotContain("NOPASSWD", installer, StringComparison.Ordinal);
        Assert.DoesNotContain("sudoers", installer, StringComparison.Ordinal);
        foreach (var package in new[] { "cups", "printer-driver-gutenprint", "network-manager", "avahi-daemon" })
            Assert.Contains(package, installer, StringComparison.Ordinal);

        // The polkit rule grants NetworkManager actions to the box account, and nothing else.
        Assert.Contains("subject.user !== \"nubarca-print-box\"", rules, StringComparison.Ordinal);
        var granted = System.Text.RegularExpressions.Regex.Matches(rules, "\"(org\\.freedesktop\\.[^\"]+)\"")
            .Select(m => m.Groups[1].Value).ToList();
        Assert.NotEmpty(granted);
        Assert.All(granted, id => Assert.StartsWith("org.freedesktop.NetworkManager.", id));

        // A box without a known network must still start: it is what opens the setup network.
        Assert.DoesNotContain("After=network-online.target", unit, StringComparison.Ordinal);
        Assert.DoesNotContain("Wants=network-online.target", unit, StringComparison.Ordinal);
    }
}
