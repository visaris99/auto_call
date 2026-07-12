using System.Net;
using System.Security.Cryptography;

namespace Core;

public sealed record UpdateDownloadProgress(long DownloadedBytes, long TotalBytes);

public interface IUpdatePackageDownloader
{
    Task<string> DownloadAsync(
        UpdateManifest manifest,
        string destinationPath,
        IProgress<UpdateDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public sealed class UpdateDownloader : IUpdatePackageDownloader
{
    private readonly HttpClient _http;
    private readonly UpdatePolicy _policy;

    public UpdateDownloader(HttpClient http, UpdatePolicy policy)
    {
        _http = http;
        _policy = policy;
    }

    public async Task<string> DownloadAsync(
        UpdateManifest manifest,
        string destinationPath,
        IProgress<UpdateDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        string partialPath = destinationPath + ".part";
        TryDelete(partialPath);
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)
                                  ?? throw new UpdateDownloadException(
                                      "DOWNLOAD_PATH", "업데이트 저장 경로가 올바르지 않습니다."));

        try
        {
            Uri current = manifest.DownloadUri;
            for (int redirects = 0; ; redirects++)
            {
                _policy.ValidateDownloadUri(current);
                using var request = new HttpRequestMessage(HttpMethod.Get, current);
                using HttpResponseMessage response = await _http.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);

                Uri effectiveUri = response.RequestMessage?.RequestUri ?? current;
                _policy.ValidateDownloadUri(effectiveUri);

                if (IsRedirect(response.StatusCode))
                {
                    if (redirects >= _policy.MaximumRedirects || response.Headers.Location is null)
                        throw new UpdateDownloadException(
                            "DOWNLOAD_REDIRECT", "업데이트 다운로드 리다이렉트가 올바르지 않습니다.");
                    current = response.Headers.Location.IsAbsoluteUri
                        ? response.Headers.Location
                        : new Uri(effectiveUri, response.Headers.Location);
                    _policy.ValidateDownloadUri(current);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                    throw new UpdateDownloadException(
                        "DOWNLOAD_HTTP", $"업데이트 다운로드에 실패했습니다(HTTP {(int)response.StatusCode}).");

                await WriteVerifiedResponseAsync(
                    response, manifest, partialPath, progress, cancellationToken)
                    .ConfigureAwait(false);
                File.Move(partialPath, destinationPath, overwrite: true);
                return destinationPath;
            }
        }
        catch (OperationCanceledException)
        {
            TryDelete(partialPath);
            throw;
        }
        catch (UpdateException)
        {
            TryDelete(partialPath);
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException
                                   or UnauthorizedAccessException)
        {
            TryDelete(partialPath);
            throw new UpdateDownloadException(
                "DOWNLOAD_IO", "업데이트 파일을 다운로드하거나 저장하지 못했습니다.", ex);
        }
    }

    private async Task WriteVerifiedResponseAsync(
        HttpResponseMessage response,
        UpdateManifest manifest,
        string partialPath,
        IProgress<UpdateDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        long? contentLength = response.Content.Headers.ContentLength;
        if (contentLength is null || contentLength.Value != manifest.Size)
            throw new UpdateDownloadException(
                "DOWNLOAD_LENGTH_HEADER", "업데이트 파일 크기 정보가 manifest와 다릅니다.");
        if (contentLength.Value > _policy.MaximumFileBytes)
            throw new UpdateDownloadException(
                "DOWNLOAD_TOO_LARGE", "업데이트 파일이 허용된 최대 크기를 초과합니다.");

        await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var target = new FileStream(
            partialPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[81920];
        long downloaded = 0;

        while (true)
        {
            int read = await source.ReadAsync(buffer.AsMemory(), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0) break;
            downloaded += read;
            if (downloaded > manifest.Size || downloaded > _policy.MaximumFileBytes)
                throw new UpdateDownloadException(
                    "DOWNLOAD_SIZE_OVERFLOW", "업데이트 파일 크기가 manifest보다 큽니다.");
            hash.AppendData(buffer, 0, read);
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                .ConfigureAwait(false);
            progress?.Report(new UpdateDownloadProgress(downloaded, manifest.Size));
        }

        await target.FlushAsync(cancellationToken).ConfigureAwait(false);
        if (downloaded != manifest.Size)
            throw new UpdateDownloadException(
                "DOWNLOAD_SIZE_MISMATCH", "다운로드한 업데이트 파일 크기가 manifest와 다릅니다.");

        byte[] expectedHash = Convert.FromHexString(manifest.Sha256);
        byte[] actualHash = hash.GetHashAndReset();
        if (!CryptographicOperations.FixedTimeEquals(expectedHash, actualHash))
            throw new UpdateDownloadException(
                "DOWNLOAD_HASH_MISMATCH", "다운로드한 업데이트 파일의 SHA-256이 일치하지 않습니다.");
    }

    private static bool IsRedirect(HttpStatusCode statusCode) => statusCode is
        HttpStatusCode.MovedPermanently or
        HttpStatusCode.Redirect or
        HttpStatusCode.RedirectMethod or
        HttpStatusCode.TemporaryRedirect or
        HttpStatusCode.PermanentRedirect;

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
