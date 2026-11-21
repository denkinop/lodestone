using System.Net;
using System.Net.Http.Headers;
using System.Xml;

namespace Lodestone.Core.Cdn;

public sealed class OfficialCdnException(string message, Exception? inner = null) : IOException(message, inner);

public sealed class OfficialCdnClient : IDisposable
{
    internal const int MetadataLimit = 1 << 20;
    private const int MaximumRangeLength = 32 << 20;
    internal static readonly Uri ManifestUri = new(
        $"https://v3launcher.jijiagames.com/v3launcher/build/{CdnManifest.GameId}/8847/client-all-files-list/client_all_files_list.dat");
    internal static readonly Uri ControlUri = new($"https://downloader.dorado.sdo.com/v3launcher/{CdnManifest.GameId}/v3ctrl.xml");

    private readonly HttpClient http;
    private readonly TimeProvider timeProvider;

    public OfficialCdnClient() : this(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        ConnectTimeout = TimeSpan.FromSeconds(8),
        UseCookies = false,
    }, TimeProvider.System) { }

    internal OfficialCdnClient(HttpMessageHandler handler, TimeProvider timeProvider)
    {
        http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        this.timeProvider = timeProvider;
    }

    public async Task<CdnSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var manifestRequest = new HttpRequestMessage(HttpMethod.Get, ManifestUri);
            using var controlRequest = new HttpRequestMessage(HttpMethod.Get, ControlUri);
            var manifest = CdnManifest.Parse(await DownloadAsync(manifestRequest, MetadataLimit, cancellationToken));
            var signing = CdnSigning.Parse(await DownloadAsync(controlRequest, MetadataLimit, cancellationToken));
            return new CdnSnapshot(manifest, signing);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or XmlException)
        {
            throw new OfficialCdnException("无法解析官方修复清单或下载配置", ex);
        }
    }

    public Task<byte[]> ReadRangeAsync(
        CdnSnapshot snapshot, string path, long offset, int size, CancellationToken cancellationToken = default)
    {
        if (!snapshot.Files.TryGetValue(path, out var file))
            throw new OfficialCdnException("官方修复清单缺少请求的文件");
        if (size <= 0 || size > MaximumRangeLength || offset < 0 || offset > file.Size - size)
            throw new ArgumentOutOfRangeException(nameof(size), "官方资源请求超出文件范围或单次下载限制");
        return ReadRangeAsync(snapshot, file, offset, size, cancellationToken);
    }

    private async Task<byte[]> ReadRangeAsync(
        CdnSnapshot snapshot, CdnFile file, long offset, int size, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            snapshot.Signing.FileUri(snapshot.Manifest, file, timeProvider.GetUtcNow()));
        request.Headers.Range = new RangeHeaderValue(offset, offset + size - 1);
        request.Headers.TryAddWithoutValidation("Referer", snapshot.Signing.Referer);
        request.Headers.TryAddWithoutValidation("User-Agent", snapshot.Signing.UserAgent);
        return await DownloadAsync(request, size, cancellationToken, file.Size);
    }

    private async Task<byte[]> DownloadAsync(
        HttpRequestMessage request, int limit, CancellationToken cancellationToken, long? fileSize = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        request.Headers.AcceptEncoding.ParseAdd("identity");

        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            var expected = fileSize.HasValue ? HttpStatusCode.PartialContent : HttpStatusCode.OK;
            if (response.StatusCode != expected)
                throw new OfficialCdnException($"官方更新源返回 HTTP {(int)response.StatusCode}，需要 HTTP {(int)expected}");

            var headers = response.Content.Headers;
            if (headers.ContentEncoding.Any(encoding => !encoding.Equals("identity", StringComparison.OrdinalIgnoreCase)))
                throw new OfficialCdnException("官方更新源返回了压缩的 HTTP 响应");
            if (fileSize.HasValue)
            {
                var range = request.Headers.Range!.Ranges.Single();
                var received = headers.ContentRange;
                if (received is null || !received.Unit.Equals("bytes", StringComparison.OrdinalIgnoreCase)
                    || received.From != range.From || received.To != range.To || received.Length != fileSize)
                {
                    throw new OfficialCdnException("官方更新源返回的范围与版本清单不匹配");
                }
            }
            if (headers.ContentLength is { } length && (length > limit || fileSize.HasValue && length != limit))
                throw new OfficialCdnException("官方更新源声明的数据长度不匹配");

            await using var content = await response.Content.ReadAsStreamAsync(token);
            if (fileSize.HasValue)
            {
                var bytes = new byte[limit];
                var read = await content.ReadAtLeastAsync(bytes, limit, false, token);
                if (read != limit || await content.ReadAsync(new byte[1], token) != 0)
                    throw new OfficialCdnException("官方更新源返回的数据长度不匹配");
                return bytes;
            }

            using var buffer = new MemoryStream();
            var block = new byte[8192];
            int count;
            while ((count = await content.ReadAsync(block, token)) != 0)
            {
                if (buffer.Length + count > limit)
                    throw new OfficialCdnException("官方更新源返回数据超过上限");
                buffer.Write(block, 0, count);
            }
            if (headers.ContentLength is { } declared && buffer.Length != declared)
                throw new OfficialCdnException("官方更新源返回的数据长度不匹配");
            return buffer.ToArray();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new OfficialCdnException("读取官方更新源超时");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException and not OfficialCdnException)
        {
            throw new OfficialCdnException("无法完整读取官方更新源，请稍后重试");
        }
    }

    public void Dispose() => http.Dispose();
}
