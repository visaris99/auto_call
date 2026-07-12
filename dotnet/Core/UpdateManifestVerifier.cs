using System.Security.Cryptography;

namespace Core;

public class UpdateException : Exception
{
    public string Code { get; }

    public UpdateException(string code, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
    }
}

public sealed class UpdateSecurityException : UpdateException
{
    public UpdateSecurityException(string code, string message, Exception? innerException = null)
        : base(code, message, innerException)
    {
    }
}

public sealed class UpdateDownloadException : UpdateException
{
    public UpdateDownloadException(string code, string message, Exception? innerException = null)
        : base(code, message, innerException)
    {
    }
}

public sealed class UpdateInstallException : UpdateException
{
    public UpdateInstallException(string message, Exception? innerException = null)
        : base("INSTALL_START", message, innerException)
    {
    }
}

public sealed record UpdatePolicy(
    IReadOnlySet<string> AllowedHosts,
    long MaximumFileBytes = 300L * 1024 * 1024,
    int MaximumRedirects = 3)
{
    public static UpdatePolicy OfficeProduction { get; } = new(
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "crm.milestone-sales.xyz",
        });

    public void ValidateDownloadUri(Uri uri)
    {
        if (!uri.IsAbsoluteUri
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || (!uri.IsDefaultPort && uri.Port != 443)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new UpdateSecurityException(
                "DOWNLOAD_URL_POLICY", "업데이트 주소가 보안 정책을 충족하지 않습니다.");
        }

        if (!AllowedHosts.Contains(uri.IdnHost))
            throw new UpdateSecurityException(
                "DOWNLOAD_HOST_POLICY", "허용되지 않은 업데이트 서버입니다.");
    }
}

public sealed class UpdateManifestVerifier
{
    private readonly UpdatePolicy _policy;
    private readonly IReadOnlyDictionary<string, string> _trustedPublicKeys;
    private readonly Func<DateTimeOffset> _utcNow;

    public UpdateManifestVerifier(
        UpdatePolicy policy,
        IReadOnlyDictionary<string, string>? trustedPublicKeys = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        _policy = policy;
        _trustedPublicKeys = trustedPublicKeys ?? UpdateTrust.TrustedPublicKeys;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public void Verify(UpdateManifest manifest)
    {
        _policy.ValidateDownloadUri(manifest.DownloadUri);
        if (manifest.Size <= 0 || manifest.Size > _policy.MaximumFileBytes)
            throw new UpdateSecurityException(
                "MANIFEST_SIZE_POLICY", "업데이트 파일 크기가 허용 범위를 벗어났습니다.");
        if (manifest.PublishedAt > _utcNow().AddHours(24))
            throw new UpdateSecurityException(
                "MANIFEST_FUTURE_DATE", "업데이트 게시 시각이 올바르지 않습니다.");
        if (!_trustedPublicKeys.TryGetValue(manifest.KeyId, out string? publicKeyPem))
            throw new UpdateSecurityException(
                "MANIFEST_UNKNOWN_KEY", "신뢰할 수 없는 업데이트 서명 키입니다.");

        if (manifest.Signature.Length > 2048)
            throw new UpdateSecurityException(
                "MANIFEST_SIGNATURE_FORMAT", "업데이트 서명 형식이 올바르지 않습니다.");

        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(manifest.Signature);
        }
        catch (FormatException ex)
        {
            throw new UpdateSecurityException(
                "MANIFEST_SIGNATURE_FORMAT", "업데이트 서명 형식이 올바르지 않습니다.", ex);
        }

        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(publicKeyPem);
            if (!rsa.VerifyData(
                    manifest.CanonicalPayloadBytes(),
                    signature,
                    HashAlgorithmName.SHA256,
                    RSASignaturePadding.Pkcs1))
            {
                throw new UpdateSecurityException(
                    "MANIFEST_SIGNATURE_INVALID", "업데이트 서명 검증에 실패했습니다.");
            }
        }
        catch (UpdateSecurityException)
        {
            throw;
        }
        catch (CryptographicException ex)
        {
            throw new UpdateSecurityException(
                "MANIFEST_KEY_INVALID", "업데이트 공개키를 읽을 수 없습니다.", ex);
        }
    }
}
