using System.Globalization;
using System.Security.Cryptography;
using Core;

namespace Tests;

internal sealed record SignedUpdateFixture(
    VersionInfo Info,
    UpdateManifest Manifest,
    UpdatePolicy Policy,
    IReadOnlyDictionary<string, string> TrustedKeys,
    byte[] PackageBytes);

internal static class UpdateTestData
{
    internal const string Host = "updates.example.test";
    internal const string KeyId = "0123456789abcdef";

    internal static SignedUpdateFixture Create(
        byte[]? packageBytes = null,
        string version = "2.4.1",
        string? downloadUrl = null,
        long? declaredSize = null,
        DateTimeOffset? publishedAt = null)
    {
        packageBytes ??= "MZ-test-setup"u8.ToArray();
        downloadUrl ??= $"https://{Host}/downloads/milestone_dialer_setup.exe";
        publishedAt ??= DateTimeOffset.UtcNow.AddMinutes(-1);
        string sha256 = Convert.ToHexString(SHA256.HashData(packageBytes)).ToLowerInvariant();
        var unsigned = new VersionInfo(
            "2.0.0",
            version,
            downloadUrl,
            sha256,
            declaredSize ?? packageBytes.LongLength,
            publishedAt.Value.ToString("O", CultureInfo.InvariantCulture),
            KeyId,
            "AA==");
        UpdateManifest unsignedManifest = UpdateManifest.FromVersionInfo(unsigned);

        using var rsa = RSA.Create(2048);
        byte[] signature = rsa.SignData(
            unsignedManifest.CanonicalPayloadBytes(),
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        VersionInfo signed = unsigned with { Signature = Convert.ToBase64String(signature) };
        UpdateManifest manifest = UpdateManifest.FromVersionInfo(signed);
        var keys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [KeyId] = rsa.ExportSubjectPublicKeyInfoPem(),
        };
        var policy = new UpdatePolicy(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Host },
            MaximumFileBytes: 1024 * 1024,
            MaximumRedirects: 2);
        return new SignedUpdateFixture(signed, manifest, policy, keys, packageBytes);
    }
}

internal sealed class DelegateHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

    internal DelegateHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
    {
        _handler = handler;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken) => _handler(request, cancellationToken);
}
