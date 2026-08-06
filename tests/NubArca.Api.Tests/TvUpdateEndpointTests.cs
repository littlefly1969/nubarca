using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using NubArca.Api.TvUpdates;

namespace NubArca.Api.Tests;

public sealed class TvUpdateEndpointTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"nubarca-tv-ota-{Guid.NewGuid():N}");
    private readonly RSA _signingKey = RSA.Create(2048);
    private readonly string _certificatePath;
    private const string Runtime = "nubarca-tv-native-2";
    private const string UpdateId = "11111111-1111-4111-8111-111111111111";
    private const string GitSha = "1234567890abcdef1234567890abcdef12345678";

    public TvUpdateEndpointTests()
    {
        Directory.CreateDirectory(_root);
        _certificatePath = Path.Combine(_root, "ota-certificate.pem");
        var request = new CertificateRequest("CN=NubArca TV OTA Test", _signingKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new("1.3.6.1.5.5.7.3.3") }, critical: true));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        File.WriteAllText(_certificatePath, certificate.ExportCertificatePem());
    }

    [Fact]
    public async Task CompatibleAndroidRuntimeReturnsProtocolV1ManifestAndImmutableAsset()
    {
        WritePublication(signed: true);
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        using var request = ManifestRequest(Runtime);
        request.Headers.TryAddWithoutValidation("expo-expect-signature", "sig, keyid=\"main\", alg=\"rsa-v1_5-sha256\"");
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/expo+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("1", response.Headers.GetValues("Expo-Protocol-Version").Single());
        Assert.Contains("no-cache", response.Headers.CacheControl?.ToString());
        Assert.True(response.Headers.Contains("Expo-Signature"));
        var manifest = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(UpdateId, manifest.GetProperty("id").GetString());
        Assert.Equal(Runtime, manifest.GetProperty("runtimeVersion").GetString());
        var assetUrl = manifest.GetProperty("launchAsset").GetProperty("url").GetString();
        Assert.Contains($"/{Runtime}/{UpdateId}/", assetUrl);

        var asset = await client.GetAsync(new Uri(assetUrl!).PathAndQuery);
        Assert.Equal(HttpStatusCode.OK, asset.StatusCode);
        Assert.Contains("immutable", asset.Headers.CacheControl?.ToString());
        Assert.Equal("bundle", await asset.Content.ReadAsStringAsync());
        File.WriteAllText(Path.Combine(PublicationDirectory(), "files", "unreferenced.txt"), "private");
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.GetAsync($"/api/tv-app/updates/assets/{Runtime}/{UpdateId}/unreferenced.txt")).StatusCode);
    }

    [Fact]
    public async Task IncompatibleRuntimeOrMissingPublicationReturnsNoUpdate()
    {
        WritePublication(signed: true);
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(ManifestRequest("tv-native-2"))).StatusCode);
        Directory.Delete(_root, recursive: true);
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(ManifestRequest(Runtime))).StatusCode);
    }

    [Fact]
    public async Task RejectsWrongPlatformProtocolAndUnsafeInputs()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        using var ios = ManifestRequest(Runtime, "ios");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(ios)).StatusCode);
        using var oldProtocol = ManifestRequest(Runtime);
        oldProtocol.Headers.Remove("Expo-Protocol-Version");
        oldProtocol.Headers.TryAddWithoutValidation("Expo-Protocol-Version", "0");
        Assert.Equal(HttpStatusCode.NotAcceptable, (await client.SendAsync(oldProtocol)).StatusCode);
        Assert.False(TvUpdateStore.IsSafeAssetPath("../secret"));
        Assert.False(TvUpdateStore.IsSafe("../../runtime"));
    }

    [Fact]
    public async Task MalformedUnsignedTamperedOrWrongCertificatePublicationIsIgnored()
    {
        WritePublication(signed: false);
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(ManifestRequest(Runtime))).StatusCode);

        WritePublication(signed: true);
        File.AppendAllText(Path.Combine(PublicationDirectory(), "manifest.json"), " ");
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(ManifestRequest(Runtime))).StatusCode);

        WritePublication(signed: true);
        var wrongCertificate = Path.Combine(_root, "wrong-certificate.pem");
        using var wrongKey = RSA.Create(2048);
        var request = new CertificateRequest("CN=Wrong OTA", wrongKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.3") }, true));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        File.WriteAllText(wrongCertificate, certificate.ExportCertificatePem());
        using var wrongFactory = CreateFactory(wrongCertificate);
        using var wrongClient = wrongFactory.CreateClient();
        Assert.Equal(HttpStatusCode.NoContent, (await wrongClient.SendAsync(ManifestRequest(Runtime))).StatusCode);
    }

    [Fact]
    public async Task MissingVerificationCertificateWrongChannelAndCrossRuntimeFailClosed()
    {
        WritePublication(signed: true);
        using var noCertificateFactory = CreateFactory("");
        using var noCertificateClient = noCertificateFactory.CreateClient();
        Assert.Equal(HttpStatusCode.NoContent, (await noCertificateClient.SendAsync(ManifestRequest(Runtime))).StatusCode);

        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        using var wrongChannel = ManifestRequest(Runtime);
        wrongChannel.Headers.Remove("expo-channel-name");
        wrongChannel.Headers.TryAddWithoutValidation("expo-channel-name", "staging");
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(wrongChannel)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(ManifestRequest("nubarca-tv-native-1"))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(ManifestRequest("tv-native-3"))).StatusCode);
    }

    private WebApplicationFactory<Program> CreateFactory(string? certificatePath = null) => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] = "",
            ["TvUpdates:RootPath"] = _root,
            ["TvUpdates:CodeSigningCertificatePath"] = certificatePath ?? _certificatePath,
        })));

    private static HttpRequestMessage ManifestRequest(string runtime, string platform = "android")
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/tv-app/updates");
        request.Headers.TryAddWithoutValidation("Expo-Protocol-Version", "1");
        request.Headers.TryAddWithoutValidation("Expo-Platform", platform);
        request.Headers.TryAddWithoutValidation("Expo-Runtime-Version", runtime);
        request.Headers.TryAddWithoutValidation("expo-channel-name", "production");
        request.Headers.TryAddWithoutValidation("Accept", "multipart/mixed,application/expo+json,application/json");
        return request;
    }

    private string PublicationDirectory() => Path.Combine(_root, "publications", "android", Runtime, UpdateId);

    private void WritePublication(bool signed)
    {
        var publication = PublicationDirectory();
        var relative = "_expo/static/js/android/index.hbc";
        var asset = Path.Combine(publication, "files", relative);
        Directory.CreateDirectory(Path.GetDirectoryName(asset)!);
        File.WriteAllText(asset, "bundle");
        var hash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes("bundle"))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var url = $"https://nubarca.test/api/tv-app/updates/assets/{Runtime}/{UpdateId}/{relative}";
        var manifest = JsonSerializer.Serialize(new
        {
            id = UpdateId, createdAt = "2026-07-10T12:00:00.000Z", runtimeVersion = Runtime,
            launchAsset = new { hash, key = hash, contentType = "application/octet-stream", url },
            assets = Array.Empty<object>(), metadata = new { channel = "production", platform = "android", gitSha = GitSha },
            extra = new { release = new { gitSha = GitSha } }
        });
        var signature = Convert.ToBase64String(_signingKey.SignData(Encoding.UTF8.GetBytes(manifest), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        File.WriteAllText(Path.Combine(publication, "manifest.json"), manifest);
        File.WriteAllText(Path.Combine(publication, "publication.json"), JsonSerializer.Serialize(new
        {
            id = UpdateId, runtimeVersion = Runtime, platform = "android", channel = "production", gitSha = GitSha,
            signature = signed ? $"sig=\"{signature}\", keyid=\"main\", alg=\"rsa-v1_5-sha256\"" : null
        }));
        var channel = Path.Combine(_root, "channels", "production", "android");
        Directory.CreateDirectory(channel);
        File.WriteAllText(Path.Combine(channel, $"{Runtime}.json"), JsonSerializer.Serialize(new { current = UpdateId, previous = (string?)null }));
    }

    public void Dispose()
    {
        _signingKey.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
