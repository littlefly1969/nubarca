using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Cli;
using NubArca.Api.Files;
using NubArca.Api.Tests.Endpoints;
using Xunit;

namespace NubArca.Api.Tests.Cli;

// Slice 65 — `storage reconcile` operator CLI command. Dry-run by default;
// counts-only output; destructive deletion only with --delete-orphans.
public sealed class StorageReconcileCliTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory;

    public StorageReconcileCliTests()
    {
        _factory = new SqliteWebApplicationFactory();
        _factory.EnsureDatabaseCreated();
    }

    public void Dispose() => _factory.Dispose();

    private async Task SeedBlobAsync(Guid ownerId)
    {
        using var scope = _factory.Services.CreateScope();
        var files = scope.ServiceProvider.GetRequiredService<IFileItemService>();
        await files.CreateAsync(ownerId, null, "a.bin", "application/octet-stream",
            new MemoryStream(new byte[64]));
    }

    private string WriteOrphanObject()
    {
        var sha = new string('b', 64);
        var dir = Path.Combine(_factory.StorageRoot, "objects", sha[..2], sha[2..4]);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, sha);
        File.WriteAllBytes(path, new byte[] { 9, 9, 9 });
        return path;
    }

    private async Task<(int exit, string stdout, string stderr)> RunCli(params string[] args)
    {
        using var scope = _factory.Services.CreateScope();
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = await CliEntryPoint.RunAsync(args, stdout, stderr, () => scope.ServiceProvider);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public async Task Reconcile_DryRun_By_Default_Reports_Counts_And_Does_Not_Delete()
    {
        var owner = await _factory.SeedUserAsync();
        await SeedBlobAsync(owner);
        var orphan = WriteOrphanObject();

        var (exit, stdout, stderr) = await RunCli("storage", "reconcile");

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stderr);
        Assert.Contains("dry-run", stdout);
        Assert.Contains("orphan-on-disk 1", stdout);
        Assert.True(File.Exists(orphan)); // default never deletes
    }

    [Fact]
    public async Task Reconcile_Delete_Orphans_Removes_Orphan()
    {
        var owner = await _factory.SeedUserAsync();
        await SeedBlobAsync(owner);
        var orphan = WriteOrphanObject();

        var (exit, stdout, _) = await RunCli("storage", "reconcile", "--delete-orphans");

        Assert.Equal(0, exit);
        Assert.Contains("deleted 1", stdout);
        Assert.False(File.Exists(orphan));
    }

    [Fact]
    public async Task Reconcile_Invalid_Limit_Returns_64()
    {
        var (exit, _, stderr) = await RunCli("storage", "reconcile", "--limit", "nope");
        Assert.Equal(64, exit);
        Assert.Contains("--limit", stderr);
    }

    [Fact]
    public async Task Reconcile_Output_Has_No_Storage_Key_Or_Path()
    {
        var owner = await _factory.SeedUserAsync();
        await SeedBlobAsync(owner);
        WriteOrphanObject();

        var (_, stdout, _) = await RunCli("storage", "reconcile");

        Assert.DoesNotContain("objects/", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('b', 64), stdout, StringComparison.Ordinal);
        Assert.DoesNotContain(_factory.StorageRoot, stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Help_Lists_Storage_Reconcile()
    {
        var (exit, stdout, _) = await RunCli("--help");
        Assert.Equal(0, exit);
        Assert.Contains("storage reconcile", stdout);
    }
}
