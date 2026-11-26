using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Lodestone.Core.Cdn;
using Xunit;

namespace Lodestone.Core.Tests;

public sealed class CdnTests : IDisposable
{
    private const int BlockSize = 1 << 20;
    private const string BaseUri = "https://ff14.jijiagames.com/v3client/build/100001900/8847/apppc/1179";
    private const string FileHash = "00000000000000000000000000000000";
    private const string Version = "2026.11.03.0000.0000";
    private static readonly byte[] SampleExe = Enumerable.Range(0, BlockSize + 17)
        .Select(index => (byte)(index * 31 ^ (index >> 8))).ToArray();
    private readonly string cache = Path.Combine(Path.GetTempPath(), "lodestone-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task VersionMatchesPythonAndReusesVerifiedCache()
    {
        using var handler = new TestCdn { RequireConcurrentChunks = true };
        using var cdn = new OfficialCdnClient(handler, new FixedTimeProvider());
        var service = new GameVersionService(cdn, cache);
        var progress = new List<VersionDownloadProgress>();
        var first = await service.RefreshAsync(progress.Add);
        var second = await service.RefreshAsync();

        Assert.Equal("ffxiv_dx11.exe/1048593/b5b357677feee80c5ee80f23d975e30659515352+2026.11.03.0000", first.VersionInfo);
        Assert.Equal("0.0.0.31", first.Revision);
        Assert.False(first.FromCache);
        Assert.Equal(first with { FromCache = true }, second);
        Assert.Equal([0, BlockSize, BlockSize + 17], progress.Select(value => value.BytesRead));
        Assert.Equal(3, handler.Ranges.Count);
        Assert.Contains((0L, BlockSize - 1L), handler.Ranges);
        Assert.Contains(((long)BlockSize, BlockSize + 16L), handler.Ranges);
        Assert.All(progress, value => Assert.Equal(SampleExe.LongLength, value.TotalBytes));
    }

    [Fact]
    public async Task ChangedManifestInvalidatesCacheEvenWhenRevisionIsUnchanged()
    {
        using var handler = new TestCdn();
        using var cdn = new OfficialCdnClient(handler, new FixedTimeProvider());
        var service = new GameVersionService(cdn, cache);
        await service.RefreshAsync();
        handler.FileHash = new string('1', 32);

        Assert.False((await service.RefreshAsync()).FromCache);
        Assert.Equal(6, handler.Ranges.Count);
    }

    [Fact]
    public async Task FailedRefreshPreservesPreviousCache()
    {
        using var handler = new TestCdn();
        using var cdn = new OfficialCdnClient(handler, new FixedTimeProvider());
        var service = new GameVersionService(cdn, cache);
        await service.RefreshAsync();
        var saved = await File.ReadAllTextAsync(Path.Combine(cache, "version-info.json"));
        handler.FileHash = new string('1', 32);
        handler.Fault = "status";

        await Assert.ThrowsAsync<OfficialCdnException>(() => service.RefreshAsync());
        Assert.Equal(saved, await File.ReadAllTextAsync(Path.Combine(cache, "version-info.json")));
    }

    [Fact]
    public async Task ConcurrentRefreshesDoNotDownloadTheSameExecutableTwice()
    {
        using var handler = new TestCdn();
        using var cdn = new OfficialCdnClient(handler, new FixedTimeProvider());
        var service = new GameVersionService(cdn, cache);
        var results = await Task.WhenAll(service.RefreshAsync(), service.RefreshAsync());

        Assert.Single(results, result => result.FromCache);
        Assert.Equal(3, handler.Ranges.Count);
    }

    [Theory]
    [InlineData("status")]
    [InlineData("range")]
    [InlineData("compressed")]
    [InlineData("truncated")]
    [InlineData("too-long")]
    public async Task RejectsInvalidRangeResponse(string fault)
    {
        using var handler = new TestCdn { Fault = fault };
        using var cdn = new OfficialCdnClient(handler, new FixedTimeProvider());
        var snapshot = await cdn.GetSnapshotAsync();

        await Assert.ThrowsAsync<OfficialCdnException>(() => cdn.ReadRangeAsync(snapshot, "game/ffxiv_dx11.exe", 0, 16));
    }

    [Theory]
    [InlineData(@"game\sqpack\ex1\040100.win32.index", "ex1.ver")]
    [InlineData(@"game\sqpack\ex6\040100.win32.index", "尚未支持")]
    public async Task FailsBeforeDownloadingIfExpansionFilesCannotBeResolved(string archive, string error)
    {
        using var handler = new TestCdn { AddFile = archive, IncludeVersion = false };
        using var cdn = new OfficialCdnClient(handler, new FixedTimeProvider());
        var service = new GameVersionService(cdn, cache);

        Assert.Contains(error, (await Assert.ThrowsAsync<OfficialCdnException>(() => service.RefreshAsync())).Message);
        Assert.Empty(handler.Ranges);
    }

    [Fact]
    public async Task CancellationDuringDownloadDoesNotPublishCache()
    {
        using var cts = new CancellationTokenSource();
        using var handler = new TestCdn();
        using var cdn = new OfficialCdnClient(handler, new FixedTimeProvider());
        var service = new GameVersionService(cdn, cache);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RefreshAsync(
            value => { if (value.BytesRead > 0) cts.Cancel(); }, cts.Token));
        Assert.False(File.Exists(Path.Combine(cache, "version-info.json")));
    }

    [Fact]
    public async Task CatalogRecognizesUiAndCharacterFilesAndKeepsOriginalCaseForSigning()
    {
        using var handler = new TestCdn { AddFile = @"game\sqpack\ffxiv\060000.WIN32.INDEX2" };
        using var cdn = new OfficialCdnClient(handler, new FixedTimeProvider());
        var snapshot = await cdn.GetSnapshotAsync();

        Assert.Equal(@"game\sqpack\ffxiv\060000.WIN32.INDEX2", Assert.Single(SqPackCatalog.Files(snapshot, SqPackCategory.Ui)).Value.RelativePath);
        Assert.Equal(@"game\sqpack\ffxiv\040000.win32.index", Assert.Single(SqPackCatalog.Files(snapshot, SqPackCategory.Character)).Value.RelativePath);
    }

    [Theory]
    [InlineData("https://example.com/v3client/build/100001900/8847/apppc/1179", @"game\win.exe")]
    [InlineData(BaseUri, @"game\..\other.exe")]
    public void RejectsUntrustedManifestLocations(string baseUri, string path)
    {
        Assert.Throws<FormatException>(() => CdnManifest.Parse(Encoding.UTF8.GetBytes(
            $"{baseUri}|100001900|0.0.0.31\n{path}|16|{FileHash}")));
    }

    [Fact]
    public void SigningMatchesTheReferenceEncoding()
    {
        var manifest = CdnManifest.Parse(Encoding.UTF8.GetBytes(
            $@"{BaseUri}|100001900|0.0.0.31" + "\n" + $@"game\Fancy Folder\FFXIV_dx11.exe|16|{FileHash}"));
        var signing = new CdnSigning("0123456789abcdef0123456789abcdef", "https://example.com/", "test");
        var url = signing.FileUri(manifest, Assert.Single(manifest.Files).Value, new FixedTimeProvider().GetUtcNow());

        Assert.Equal(
            "https://ff14.jijiagames.com/698f411bb17230fb8739ed337404edf0/6553f100/v3client/build/100001900/8847/apppc/1179/game/Fancy%20Folder/C87A49E44402D1260FC28B78EF745B9F",
            url.AbsoluteUri);
    }

    public void Dispose()
    {
        if (Directory.Exists(cache))
            Directory.Delete(cache, recursive: true);
    }

    private sealed class TestCdn : HttpMessageHandler
    {
        private readonly TaskCompletionSource secondChunk = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentQueue<(long Start, long End)> Ranges { get; } = new();
        public string FileHash { get; set; } = CdnTests.FileHash;
        public string? Fault { get; set; }
        public string? AddFile { get; init; }
        public bool IncludeVersion { get; init; } = true;
        public bool RequireConcurrentChunks { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            if (request.RequestUri == OfficialCdnClient.ManifestUri)
            {
                var entries = new List<string>
                {
                    $"{BaseUri}|100001900|0.0.0.31",
                    $@"game\ffxiv_dx11.exe|{SampleExe.Length}|{FileHash}",
                    $@"game\sqpack\ffxiv\040000.win32.index|16|{FileHash}",
                };
                if (IncludeVersion)
                    entries.Add($@"game\sqpack\ex1\ex1.ver|{Version.Length}|{FileHash}");
                if (AddFile is not null)
                    entries.Add($"{AddFile}|16|{FileHash}");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(string.Join('\n', entries)) };
            }
            if (request.RequestUri == OfficialCdnClient.ControlUri)
            {
                var control = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "public-cdn-control.xml"),
                    cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(control) };
            }

            Assert.Equal("ff14.jijiagames.com", request.RequestUri?.Host);
            Assert.Equal("identity", Assert.Single(request.Headers.AcceptEncoding).Value);
            var range = Assert.Single(request.Headers.Range!.Ranges);
            var start = Assert.IsType<long>(range.From);
            var end = Assert.IsType<long>(range.To);
            Ranges.Enqueue((start, end));
            var isVersion = request.RequestUri!.AbsolutePath.Contains("/ex1/", StringComparison.Ordinal);
            if (RequireConcurrentChunks && !isVersion)
            {
                if (start == 0)
                    await secondChunk.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
                else
                    secondChunk.TrySetResult();
            }
            var data = isVersion ? Encoding.ASCII.GetBytes(Version) : SampleExe;
            var bytes = data.AsSpan((int)start, (int)(end - start + 1)).ToArray();
            var body = Fault switch { "truncated" => bytes[..^1], "too-long" => [..bytes, 0], _ => bytes };
            var response = new HttpResponseMessage(Fault == "status" ? HttpStatusCode.OK : HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(body)
            };
            response.Content.Headers.ContentLength = bytes.Length;
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, end, data.Length + (Fault == "range" ? 1 : 0));
            if (Fault == "compressed")
                response.Content.Headers.ContentEncoding.Add("gzip");
            return response;
        }
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeSeconds(1700000000);
    }
}
