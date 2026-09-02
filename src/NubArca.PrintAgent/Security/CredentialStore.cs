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
