using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Core;

public sealed record UpdateManifest(
    string Version,
    string DownloadUrl,
    string Sha256,
    long Size,
    DateTimeOffset PublishedAt,
    string KeyId,
    string Signature)
{
    public const string PayloadDomain = "milestone-dialer-update-v1";

    private static readonly Regex VersionPattern = new(
        @"^\d+\.\d+\.\d+$", RegexOptions.CultureInvariant);
    private static readonly Regex Sha256Pattern = new(
        "^[a-f0-9]{64}$", RegexOptions.CultureInvariant);
    private static readonly Regex KeyIdPattern = new(
        "^[a-f0-9]{16,64}$", RegexOptions.CultureInvariant);

    public Uri DownloadUri => new(DownloadUrl, UriKind.Absolute);

    public static UpdateManifest FromVersionInfo(VersionInfo info)
    {
        string version = info.LatestVersion?.Trim() ?? "";
        string downloadUrl = info.DownloadUrl?.Trim() ?? "";
        string sha256 = info.Sha256?.Trim().ToLowerInvariant() ?? "";
        string publishedAt = info.PublishedAt?.Trim() ?? "";
        string keyId = info.KeyId?.Trim().ToLowerInvariant() ?? "";
        string signature = info.Signature?.Trim() ?? "";

        if (!VersionPattern.IsMatch(version) || !System.Version.TryParse(version, out _))
            throw new UpdateSecurityException("MANIFEST_VERSION", "업데이트 버전 형식이 올바르지 않습니다.");
        if (!Uri.TryCreate(downloadUrl, UriKind.Absolute, out _))
            throw new UpdateSecurityException("MANIFEST_URL", "업데이트 주소 형식이 올바르지 않습니다.");
        if (!Sha256Pattern.IsMatch(sha256))
            throw new UpdateSecurityException("MANIFEST_SHA256", "업데이트 해시가 없거나 올바르지 않습니다.");
        if (info.Size is not > 0)
            throw new UpdateSecurityException("MANIFEST_SIZE", "업데이트 파일 크기가 올바르지 않습니다.");
        if (!DateTimeOffset.TryParse(publishedAt, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsedPublishedAt))
            throw new UpdateSecurityException("MANIFEST_DATE", "업데이트 게시 시각이 올바르지 않습니다.");
        if (!KeyIdPattern.IsMatch(keyId))
            throw new UpdateSecurityException("MANIFEST_KEY", "업데이트 서명 키가 올바르지 않습니다.");
        if (string.IsNullOrWhiteSpace(signature))
            throw new UpdateSecurityException("MANIFEST_SIGNATURE", "업데이트 서명이 없습니다.");

        return new UpdateManifest(
            version,
            downloadUrl,
            sha256,
            info.Size.Value,
            parsedPublishedAt,
            keyId,
            signature);
    }

    public string CanonicalPayload() => string.Join('\n',
        PayloadDomain,
        $"version={Version}",
        $"downloadUrl={DownloadUrl}",
        $"sha256={Sha256}",
        $"size={Size.ToString(CultureInfo.InvariantCulture)}",
        $"publishedAtUnixSeconds={PublishedAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)}",
        $"keyId={KeyId}");

    public byte[] CanonicalPayloadBytes() => Encoding.UTF8.GetBytes(CanonicalPayload());
}
