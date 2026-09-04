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
    }

    [Fact]
    public async Task Fake_Adapter_Discovers_Ready_10x15_Color_Printer()
    {
        var output = Path.Combine(Path.GetTempPath(), $"nubarca-fake-{Guid.NewGuid():N}");
        try
        {
            var adapter = new FakePrinterAdapter(output);
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
}
