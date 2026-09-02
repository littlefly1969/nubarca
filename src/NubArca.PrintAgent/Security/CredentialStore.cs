using System.Security.Cryptography;
using System.Text;

namespace NubArca.PrintAgent.Security;

public interface ICredentialStore
{
    Task SaveAsync(string credential, CancellationToken cancellationToken);
    Task<string?> LoadAsync(CancellationToken cancellationToken);
}

public sealed class DpapiCredentialStore : ICredentialStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("NubArca.PrintAgent.v1");
    private readonly string _path;
    public DpapiCredentialStore(string path) => _path = path;

    public async Task SaveAsync(string credential, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Agent credentials are protected with Windows DPAPI.");
        var protectedBytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(credential), Entropy,
            DataProtectionScope.LocalMachine);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        var temp = _path + ".new";
        await File.WriteAllBytesAsync(temp, protectedBytes, cancellationToken);
        File.Move(temp, _path, overwrite: true);
    }

    public async Task<string?> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path)) return null;
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Agent credentials are protected with Windows DPAPI.");
        var protectedBytes = await File.ReadAllBytesAsync(_path, cancellationToken);
        var bytes = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.LocalMachine);
        return Encoding.UTF8.GetString(bytes);
    }
}

// Linux has no DPAPI equivalent available to a self-contained .NET service.
// Isolation is therefore explicit: the systemd installer creates one service
// account per station and a mode-0700 state directory; this store refuses a
// credential file readable or writable by another account.
public sealed class LinuxFileCredentialStore : ICredentialStore
{
    private const UnixFileMode CredentialMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode ForbiddenMode = UnixFileMode.GroupRead | UnixFileMode.GroupWrite
        | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
    private readonly string _path;

    public LinuxFileCredentialStore(string path) => _path = path;

    public async Task SaveAsync(string credential, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("The Linux file credential store requires Linux.");
        if (string.IsNullOrWhiteSpace(credential)) throw new ArgumentException("Credential is required.", nameof(credential));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        var temporary = _path + ".new-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllTextAsync(temporary, credential, Encoding.UTF8, cancellationToken);
            File.SetUnixFileMode(temporary, CredentialMode);
            File.Move(temporary, _path, overwrite: true);
            File.SetUnixFileMode(_path, CredentialMode);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public async Task<string?> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path)) return null;
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("The Linux file credential store requires Linux.");
        var mode = File.GetUnixFileMode(_path);
        if ((mode & ForbiddenMode) != 0)
            throw new InvalidOperationException("Print Agent credential permissions must be 0600.");
        return await File.ReadAllTextAsync(_path, cancellationToken);
    }
}
