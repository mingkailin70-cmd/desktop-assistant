using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using XiaoK.Core;

namespace XiaoK.Adapters.Browser;

/// <summary>独立、无 Cookie 的 HTTPS 公网文件下载器；只在内存中缓冲受限文件。</summary>
public sealed class PublicFileDownloader : IPublicFileDownloader
{
    private const int MaximumRedirects = 5;
    private static readonly TimeSpan OverallTimeout = TimeSpan.FromSeconds(45);

    public async Task<PublicFileDownloadResult> DownloadAsync(string url, CancellationToken cancellationToken)
    {
        if (!PublicWebUrlPolicy.IsAllowedUrlShape(url))
            return Failure("只支持用户明确提供的 HTTPS 公网文件地址（443端口）。", "WEB_DOWNLOAD_URL_NOT_ALLOWED");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(OverallTimeout);
        try
        {
            return await FetchAsync(new Uri(url, UriKind.Absolute), timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException)
        {
            return Failure("公网文件下载超过45秒时限，已停止；没有保存部分文件。", "WEB_DOWNLOAD_TIMEOUT");
        }
        catch (PublicWebHostResolutionException)
        {
            return Failure("本机 DNS 无法解析下载主机名；没有建立网络连接。", "WEB_DNS_RESOLUTION_FAILED");
        }
        catch (PublicWebAddressException)
        {
            return Failure("下载主机或重定向目标不是可确认的公网地址；已阻止连接。", "WEB_PRIVATE_ADDRESS_BLOCKED");
        }
        catch (PublicDownloadTooLargeException)
        {
            return Failure("下载解压后内容超过50 MiB上限；没有保存文件。", "WEB_DOWNLOAD_TOO_LARGE");
        }
        catch (HttpRequestException)
        {
            return Failure("文件无法通过受限 HTTPS 连接下载；未使用代理或浏览器登录状态。", "WEB_DOWNLOAD_FAILED");
        }
        catch (IOException)
        {
            return Failure("下载内容超过50 MiB上限、连接提前结束或无法读取；没有保存部分文件。", "WEB_DOWNLOAD_CONTENT_UNAVAILABLE");
        }
        catch (UriFormatException)
        {
            return Failure("下载地址或服务器文件名格式无效；没有保存文件。", "WEB_DOWNLOAD_INVALID_RESPONSE");
        }
        catch (FormatException)
        {
            return Failure("下载响应头格式无效；没有保存文件。", "WEB_DOWNLOAD_INVALID_RESPONSE");
        }
    }

    private static async Task<PublicFileDownloadResult> FetchAsync(Uri initialUri, CancellationToken cancellationToken)
    {
        using var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            UseCookies = false,
            UseProxy = false,
            MaxResponseHeadersLength = 32,
            ConnectCallback = ConnectToPublicAddressAsync
        };
        using var client = new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        var currentUri = initialUri;

        for (var redirectCount = 0; redirectCount <= MaximumRedirects; redirectCount++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!PublicWebUrlPolicy.IsAllowedUriShape(currentUri))
                throw new PublicWebAddressException("下载重定向不符合 HTTPS 公网策略。");
            _ = await PublicWebUrlPolicy.ResolvePublicAddressesAsync(currentUri.IdnHost, cancellationToken)
                .ConfigureAwait(false);

            using var request = new HttpRequestMessage(HttpMethod.Get, currentUri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*", 0.5));
            request.Headers.UserAgent.ParseAdd("XiaoK-PublicFileDownloader/0.1");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (IsRedirect(response.StatusCode))
            {
                var location = response.Headers.Location;
                if (location is null || redirectCount == MaximumRedirects)
                    throw new HttpRequestException("下载重定向缺少目标或超过上限。");
                currentUri = location.IsAbsoluteUri ? location : new Uri(currentUri, location);
                continue;
            }

            if (response.StatusCode != HttpStatusCode.OK)
                throw new HttpRequestException("下载服务器没有返回完整的 HTTP 200 文件响应。");

            var name = ResolveFileName(response.Content.Headers.ContentDisposition, currentUri);
            if (!PublicFileDownloadPolicy.IsAllowedFileName(name))
                return Failure("下载文件名无效，或属于可执行/脚本类型；没有保存文件。", "WEB_DOWNLOAD_FILE_NAME_REJECTED");

            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (!PublicFileDownloadPolicy.IsAllowedMediaType(mediaType))
                return Failure("该下载内容是网页或可执行/脚本类型；没有保存文件。", "WEB_DOWNLOAD_MEDIA_TYPE_REJECTED");

            if (response.Content.Headers.ContentLength is > PublicFileDownloadPolicy.MaximumDownloadBytes)
                return Failure("下载内容超过50 MiB上限；没有保存文件。", "WEB_DOWNLOAD_TOO_LARGE");

            var bytes = await ReadBoundedBodyAsync(response.Content, cancellationToken).ConfigureAwait(false);
            if (bytes.Length == 0)
                return Failure("下载内容为空；没有保存文件。", "WEB_DOWNLOAD_EMPTY");
            if (!PublicFileDownloadPolicy.IsAllowedContent(bytes))
                return Failure("内容具有可执行文件或脚本特征；没有保存文件。", "WEB_DOWNLOAD_CONTENT_TYPE_REJECTED");

            return new(true, "公网文件已下载到内存，等待写入固定导出目录。", FileName: name,
                MediaType: mediaType, Content: bytes, Sha256: Convert.ToHexString(SHA256.HashData(bytes)));
        }

        throw new HttpRequestException("下载重定向次数超过上限。");
    }

    private static async ValueTask<Stream> ConnectToPublicAddressAsync(
        SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var endpoint = context.DnsEndPoint;
        if (endpoint.Port != 443)
            throw new PublicWebAddressException("只允许连接 HTTPS 默认端口。");
        var addresses = await PublicWebUrlPolicy.ResolvePublicAddressesAsync(endpoint.Host, cancellationToken)
            .ConfigureAwait(false);
        Exception? lastError = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, endpoint.Port), cancellationToken).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException)
            {
                socket.Dispose();
                if (ex is OperationCanceledException) throw;
                lastError = ex;
            }
        }
        throw new HttpRequestException("无法连接到已校验的公网地址。", lastError);
    }

    private static async Task<byte[]> ReadBoundedBodyAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using var input = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) return output.ToArray();
            if (output.Length + read > PublicFileDownloadPolicy.MaximumDownloadBytes)
                throw new PublicDownloadTooLargeException();
            output.Write(buffer, 0, read);
        }
    }

    internal static string? ResolveFileName(ContentDispositionHeaderValue? disposition, Uri finalUri)
    {
        string? candidate = null;
        if (!string.IsNullOrWhiteSpace(disposition?.FileNameStar))
        {
            // HttpContentHeaders parses and decodes RFC 5987 filename* before exposing it.
            candidate = disposition.FileNameStar.Trim().Trim('"');
        }
        else if (!string.IsNullOrWhiteSpace(disposition?.FileName))
        {
            candidate = disposition.FileName.Trim().Trim('"');
        }
        else
        {
            var lastSegment = finalUri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
            if (!string.IsNullOrEmpty(lastSegment)) candidate = Uri.UnescapeDataString(lastSegment);
        }

        return string.IsNullOrWhiteSpace(candidate) ? null : candidate;
    }

    private static bool IsRedirect(HttpStatusCode status) =>
        status is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    private static PublicFileDownloadResult Failure(string summary, string code) => new(false, summary, code);

    private sealed class PublicDownloadTooLargeException : IOException { }
}
