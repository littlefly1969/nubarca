using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Options;

namespace NubArca.Api.TvUpdates;

public sealed partial class TvUpdateStore : IDisposable
{
    private readonly string _root;
    private readonly RSA? _verificationKey;
    private readonly ILogger<TvUpdateStore> _logger;

    public TvUpdateStore(IOptions<TvUpdateOptions> options, ILogger<TvUpdateStore> logger)
    {
        _root = string.IsNullOrWhiteSpace(options.Value.RootPath)
            ? string.Empty
            : Path.GetFullPath(options.Value.RootPath);
        _logger = logger;
        _verificationKey = LoadVerificationKey(options.Value.CodeSigningCertificatePath);
    }

    public TvManifestResult? FindManifest(string platform, string runtime, string channel)
    {
        if (string.IsNullOrWhiteSpace(_root) || !Directory.Exists(_root) || _verificationKey is null) return null;
        if (platform != "android" || !IsSafe(runtime) || !IsSafe(channel)) return null;
        try
        {
            var pointerPath = UnderRoot("channels", channel, platform, $"{runtime}.json");
            if (!File.Exists(pointerPath)) return null;
            var pointer = JsonSerializer.Deserialize<TvChannelPointer>(File.ReadAllText(pointerPath), JsonOptions);
            if (pointer?.Current is null || !Guid.TryParse(pointer.Current, out _)) return null;
            var directory = UnderRoot("publications", platform, runtime, pointer.Current);
            var manifestPath = Path.Combine(directory, "manifest.json");
            var metadataPath = Path.Combine(directory, "publication.json");
            if (!File.Exists(manifestPath) || !File.Exists(metadataPath)) return null;
            var body = File.ReadAllText(manifestPath);
            var metadata = JsonSerializer.Deserialize<TvPublicationMetadata>(File.ReadAllText(metadataPath), JsonOptions);
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (metadata is null || metadata.Id != pointer.Current || metadata.RuntimeVersion != runtime || metadata.Platform != platform ||
                root.GetProperty("id").GetString() != pointer.Current || root.GetProperty("runtimeVersion").GetString() != runtime ||
                metadata.Channel != channel || root.GetProperty("metadata").GetProperty("channel").GetString() != channel ||
                root.GetProperty("metadata").GetProperty("platform").GetString() != platform ||
                string.IsNullOrWhiteSpace(metadata.GitSha) || metadata.GitSha != root.GetProperty("metadata").GetProperty("gitSha").GetString() ||
                !GitSha().IsMatch(metadata.GitSha) || !VerifyManifest(body, metadata.Signature) ||
                !IsSymlinkFree(directory, manifestPath) || !IsSymlinkFree(directory, metadataPath)) return null;
            ValidateAssets(root, directory, runtime, pointer.Current);
            return new TvManifestResult(body, metadata.Signature!);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException
            or KeyNotFoundException or InvalidOperationException or UriFormatException or FormatException)
        {
            _logger.LogWarning(exception, "Ignoring malformed TV OTA publication for {Platform}/{Runtime}/{Channel}", platform, runtime, channel);
            return null;
        }
    }

    public TvAssetResult? FindAsset(string runtime, string updateId, string assetPath)
    {
        if (string.IsNullOrWhiteSpace(_root) || _verificationKey is null || !IsSafe(runtime) || !Guid.TryParse(updateId, out _) || !IsSafeAssetPath(assetPath)) return null;
        try
        {
            var publication = UnderRoot("publications", "android", runtime, updateId);
            // Reject unpublished/malformed directories, even if a file happens to exist.
            var metadataPath = Path.Combine(publication, "publication.json");
            if (!File.Exists(metadataPath)) return null;
            var metadata = JsonSerializer.Deserialize<TvPublicationMetadata>(File.ReadAllText(metadataPath), JsonOptions);
            if (metadata?.Id != updateId || metadata.RuntimeVersion != runtime || metadata.Platform != "android") return null;
            var manifestPath = Path.Combine(publication, "manifest.json");
            if (!File.Exists(manifestPath)) return null;
            var body = File.ReadAllText(manifestPath);
            if (!VerifyManifest(body, metadata.Signature) || !IsSymlinkFree(publication, manifestPath) ||
                !IsSymlinkFree(publication, metadataPath)) return null;
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.GetProperty("id").GetString() != updateId ||
                document.RootElement.GetProperty("runtimeVersion").GetString() != runtime ||
                document.RootElement.GetProperty("metadata").GetProperty("platform").GetString() != "android" ||
                metadata.Channel != document.RootElement.GetProperty("metadata").GetProperty("channel").GetString() ||
                metadata.GitSha != document.RootElement.GetProperty("metadata").GetProperty("gitSha").GetString() ||
                string.IsNullOrWhiteSpace(metadata.GitSha) || !GitSha().IsMatch(metadata.GitSha)) return null;
            ValidateAssets(document.RootElement, publication, runtime, updateId);
            if (!ManifestReferences(document.RootElement, runtime, updateId, assetPath)) return null;
            var file = Path.GetFullPath(Path.Combine(publication, "files", assetPath.Replace('/', Path.DirectorySeparatorChar)));
            var filesRoot = Path.GetFullPath(Path.Combine(publication, "files")) + Path.DirectorySeparatorChar;
            if (!file.StartsWith(filesRoot, StringComparison.Ordinal) || !File.Exists(file)) return null;
            if (!IsSymlinkFree(publication, file)) return null;
            return new TvAssetResult(file, ContentType(file));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException
            or KeyNotFoundException or InvalidOperationException or UriFormatException or FormatException)
        {
            _logger.LogWarning(exception, "Unable to serve TV OTA asset {UpdateId}/{AssetPath}", updateId, assetPath);
            return null;
        }
    }

    private void ValidateAssets(JsonElement manifest, string publication, string runtime, string updateId)
    {
        var assets = new List<JsonElement> { manifest.GetProperty("launchAsset") };
        assets.AddRange(manifest.GetProperty("assets").EnumerateArray());
        var expectedMarker = $"/assets/{Uri.EscapeDataString(runtime)}/{updateId}/";
        foreach (var asset in assets)
        {
            var url = asset.GetProperty("url").GetString();
            var hash = asset.GetProperty("hash").GetString();
            if (url is null || hash is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) throw new InvalidDataException("Invalid asset URL");
            var marker = uri.AbsolutePath.IndexOf(expectedMarker, StringComparison.Ordinal);
            if (marker < 0) throw new InvalidDataException("Asset URL is not immutable");
            var relativePath = Uri.UnescapeDataString(uri.AbsolutePath[(marker + expectedMarker.Length)..]);
            if (!IsSafeAssetPath(relativePath)) throw new InvalidDataException("Unsafe asset path");
            var file = Path.GetFullPath(Path.Combine(publication, "files", relativePath.Replace('/', Path.DirectorySeparatorChar)));
            var filesRoot = Path.GetFullPath(Path.Combine(publication, "files")) + Path.DirectorySeparatorChar;
            if (!file.StartsWith(filesRoot, StringComparison.Ordinal) || !File.Exists(file)) throw new InvalidDataException("Missing asset");
            if (!IsSymlinkFree(publication, file)) throw new InvalidDataException("Symlinked assets are forbidden");
            var actual = Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(file)))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');
            if (!CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(actual), System.Text.Encoding.ASCII.GetBytes(hash)))
                throw new InvalidDataException("Asset hash mismatch");
        }
    }

    private static bool ManifestReferences(JsonElement manifest, string runtime, string updateId, string assetPath)
    {
        var expected = $"/assets/{Uri.EscapeDataString(runtime)}/{updateId}/{string.Join('/', assetPath.Split('/').Select(Uri.EscapeDataString))}";
        if (new Uri(manifest.GetProperty("launchAsset").GetProperty("url").GetString()!).AbsolutePath.EndsWith(expected, StringComparison.Ordinal)) return true;
        return manifest.GetProperty("assets").EnumerateArray()
            .Any(asset => new Uri(asset.GetProperty("url").GetString()!).AbsolutePath.EndsWith(expected, StringComparison.Ordinal));
    }

    private string UnderRoot(params string[] parts)
    {
        var value = Path.GetFullPath(Path.Combine(new[] { _root }.Concat(parts).ToArray()));
        if (value != _root && !value.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidDataException("Path escaped OTA storage root");
        return value;
    }

    private RSA? LoadVerificationKey(string certificatePath)
    {
        if (string.IsNullOrWhiteSpace(certificatePath) || !File.Exists(certificatePath))
        {
            _logger.LogWarning("TV OTA serving is disabled because no code-signing certificate is configured");
            return null;
        }
        try
        {
            var pem = File.ReadAllText(certificatePath);
            if (pem.Contains("PRIVATE KEY-----", StringComparison.Ordinal))
                throw new InvalidDataException("TV OTA trust file must not contain a private key");
            using var certificate = X509Certificate2.CreateFromPem(pem);
            var now = DateTimeOffset.UtcNow;
            if (now < new DateTimeOffset(certificate.NotBefore.ToUniversalTime()) ||
                now > new DateTimeOffset(certificate.NotAfter.ToUniversalTime()))
                throw new InvalidDataException("TV OTA certificate is not currently valid");
            var enhancedUsage = certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>()
                .SelectMany(extension => extension.EnhancedKeyUsages.Cast<Oid>())
                .Any(oid => oid.Value == "1.3.6.1.5.5.7.3.3");
            var digitalSignature = certificate.Extensions.OfType<X509KeyUsageExtension>()
                .Any(extension => extension.KeyUsages.HasFlag(X509KeyUsageFlags.DigitalSignature));
            if (!enhancedUsage || !digitalSignature) throw new InvalidDataException("TV OTA certificate is not valid for code signing");
            return certificate.GetRSAPublicKey() ?? throw new InvalidDataException("TV OTA certificate must use RSA");
        }
        catch (Exception exception) when (exception is CryptographicException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _logger.LogError(exception, "TV OTA serving is disabled because its verification certificate is invalid");
            return null;
        }
    }

    private bool VerifyManifest(string body, string? signatureHeader)
    {
        if (_verificationKey is null || string.IsNullOrWhiteSpace(signatureHeader)) return false;
        var match = SignatureHeader().Match(signatureHeader);
        if (!match.Success) return false;
        try
        {
            return _verificationKey.VerifyData(Encoding.UTF8.GetBytes(body), Convert.FromBase64String(match.Groups[1].Value),
                HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool IsSymlinkFree(string root, string path)
    {
        var fullRoot = Path.GetFullPath(root);
        var current = Path.GetFullPath(path);
        if (current != fullRoot && !current.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal)) return false;
        while (current.Length >= fullRoot.Length)
        {
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (info.Exists && info.LinkTarget is not null) return false;
            if (current == fullRoot) break;
            current = Path.GetDirectoryName(current)!;
        }
        return true;
    }

    public void Dispose() => _verificationKey?.Dispose();

    public static bool IsSafe(string value) => SafeSegment().IsMatch(value);
    public static bool IsSafeAssetPath(string value) => !string.IsNullOrWhiteSpace(value) && !Path.IsPathRooted(value) &&
        value.Split('/', StringSplitOptions.None).All(segment => segment.Length > 0 && segment is not "." and not ".." && !segment.Contains('\\'));

    private static string ContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".js" => "application/javascript", ".json" => "application/json", ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg", ".webp" => "image/webp", ".gif" => "image/gif",
        ".svg" => "image/svg+xml", ".ttf" => "font/ttf", ".otf" => "font/otf",
        ".mp4" => "video/mp4", ".webm" => "video/webm", _ => "application/octet-stream"
    };

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeSegment();
    [GeneratedRegex("^sig=\"([A-Za-z0-9+/]+={0,2})\", keyid=\"main\", alg=\"rsa-v1_5-sha256\"$", RegexOptions.CultureInvariant)]
    private static partial Regex SignatureHeader();
    [GeneratedRegex("^[0-9a-f]{40}$", RegexOptions.CultureInvariant)]
    private static partial Regex GitSha();
}

public sealed record TvManifestResult(string Body, string Signature);
public sealed record TvAssetResult(string Path, string ContentType);
internal sealed record TvChannelPointer(string? Current, string? Previous);
internal sealed record TvPublicationMetadata(string Id, string RuntimeVersion, string Platform, string Channel, string GitSha, string? Signature);
