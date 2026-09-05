using Microsoft.Extensions.Configuration;
using NubArca.PrintAgent.Adapters;
using NubArca.PrintAgent;

namespace NubArca.PrintAgent.Tests;

public sealed class AdapterContractTests
{
    [Fact]
    public void Instance_Config_File_Overrides_Platform_Defaults()
    {
        var root = Path.Combine(Path.GetTempPath(), $"nubarca-print-config-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "sim-sala.json");
            File.WriteAllText(path, """
                {
                  "PrintAgent": {
                    "ServerOrigin": "https://example.invalid",
                    "CredentialPath": "/var/lib/nubarca-print-agent/sim-sala/credential.bin",
                    "Adapter": "fake"
                  }
                }
                """);
            var configuration = new ConfigurationManager();

            PrintAgentConfiguration.AddInstanceFile(configuration, ["enroll", "--config", path]);

            var options = configuration.GetSection(PrintAgentOptions.SectionName)
                .Get<PrintAgentOptions>();
            Assert.NotNull(options);
            Assert.Equal("https://example.invalid", options.ServerOrigin);
            Assert.Equal("/var/lib/nubarca-print-agent/sim-sala/credential.bin", options.CredentialPath);
            Assert.Equal("fake", options.Adapter);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Instance_Config_Requires_One_Absolute_Path()
    {
        var missing = Assert.Throws<InvalidOperationException>(() =>
            PrintAgentConfiguration.AddInstanceFile(new ConfigurationManager(), ["--config"]));
        Assert.Contains("exactly one", missing.Message, StringComparison.OrdinalIgnoreCase);

        var relative = Assert.Throws<InvalidOperationException>(() =>
            PrintAgentConfiguration.AddInstanceFile(new ConfigurationManager(), ["--config", "station.json"]));
        Assert.Contains("absolute", relative.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Linux_Installer_And_Systemd_Unit_Load_The_Instance_Config()
    {
        var linux = Path.Combine(AppContext.BaseDirectory, "linux");
        var installer = File.ReadAllText(Path.Combine(linux, "install-fake-instance.sh"));
        var unit = File.ReadAllText(Path.Combine(linux, "nubarca-print-agent@.service"));

        Assert.Contains("--config \"$config_path\"", installer, StringComparison.Ordinal);
        Assert.Contains("--config /etc/nubarca-print-agent/%i.json", unit, StringComparison.Ordinal);
        Assert.Contains("install -d -o root -g root -m 0755 \"$config_dir\"", installer,
            StringComparison.Ordinal);
        Assert.DoesNotContain("-g \"$user\" -m 0750 \"$config_dir\"", installer,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fake_Adapter_Discovers_Ready_10x15_Color_Printer()
    {
        var output = Path.Combine(Path.GetTempPath(), $"nubarca-fake-{Guid.NewGuid():N}");
        try
        {
            var adapter = new FakePrinterAdapter(output, TimeSpan.Zero);
            var printer = Assert.Single(await adapter.DiscoverAsync(default));
            Assert.Equal("fake", printer.AdapterKind);
            var capabilities = await adapter.GetCapabilitiesAsync(printer, default);
            Assert.Contains("10x15", capabilities.Formats);
            Assert.True(capabilities.Color);
            Assert.Equal("ready", (await adapter.GetStatusAsync(printer, default)).State);
        }
        finally
        {
            if (Directory.Exists(output)) Directory.Delete(output, recursive: true);
        }
    }

    [Fact]
    public void Cups_Is_An_Explicit_Future_Adapter_Not_A_Silent_Fallback()
    {
        var options = new PrintAgentOptions { Adapter = PrintAdapterKinds.Cups };
        options.NormalizeAndValidate();
        var error = Assert.Throws<NotSupportedException>(() => PrintAgentPlatform.CreatePrinterAdapter(options));
        Assert.Contains("not implemented", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_Fake_Printer_Takes_As_Long_As_A_Sheet_Takes()
    {
        // A simulator that returns instantly is a poor model of a printer: a
        // queue with depth in it, a guest told how many sheets are ahead of
        // theirs, and a job observably in `submitting` all depend on the sheet
        // taking time. This is what makes those observable at all.
        var output = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var artifact = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".jpg");
        await File.WriteAllBytesAsync(artifact, [1, 2, 3]);
        try
        {
            var adapter = new FakePrinterAdapter(output, TimeSpan.FromMilliseconds(250));
            var started = DateTime.UtcNow;
            var result = await adapter.SubmitAsync(
                new PrintSubmission(Guid.NewGuid(), "fake-10x15", artifact, "image/jpeg", "10x15"), default);
            var elapsed = DateTime.UtcNow - started;

            Assert.True(result.Accepted);
            Assert.True(elapsed >= TimeSpan.FromMilliseconds(200),
                $"the sheet came out in {elapsed.TotalMilliseconds:F0}ms, which is not a printer");
            Assert.Single(Directory.GetFiles(output));
        }
        finally
        {
            if (Directory.Exists(output)) Directory.Delete(output, recursive: true);
            if (File.Exists(artifact)) File.Delete(artifact);
        }
    }

    [Fact]
    public async Task A_Cancelled_Sheet_Leaves_No_Output()
    {
        // Nothing is written until the sheet is finished, so a run stopped
        // mid-print does not leave a file claiming a print that never happened.
        var output = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var artifact = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".jpg");
        await File.WriteAllBytesAsync(artifact, [1, 2, 3]);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        try
        {
            var adapter = new FakePrinterAdapter(output, TimeSpan.FromSeconds(5));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => adapter.SubmitAsync(
                new PrintSubmission(Guid.NewGuid(), "fake-10x15", artifact, "image/jpeg", "10x15"),
                cancel.Token));
            Assert.False(Directory.Exists(output) && Directory.GetFiles(output).Length > 0);
        }
        finally
        {
            if (Directory.Exists(output)) Directory.Delete(output, recursive: true);
            if (File.Exists(artifact)) File.Delete(artifact);
        }
    }
}
