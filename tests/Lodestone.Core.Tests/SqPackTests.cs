using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Lodestone.Core.Assets;
using Lodestone.Core.Cdn;
using Xunit;

namespace Lodestone.Core.Tests;

public sealed class SqPackTests
{
    private static readonly byte[] RawDeflate =
        Convert.FromHexString("2b4ecc2dc8495548cecf2d284a2d2e4e4d51282e2c484cce0600");
    private static readonly byte[] Decoded = "sample compressed sqpack"u8.ToArray();

    [Fact]
    public void ResourceKeyMatchesReferenceAndIgnoresCase()
    {
        const ulong sample = 0x356a0255d7687511;
        Assert.Equal(sample, SqPackFormat.ResourceKey("chara/Tests/EXAMPLE.mdl"));
        var table = new byte[SqPackFormat.IndexEntrySize * 2];
        BinaryPrimitives.WriteUInt64LittleEndian(table, sample);
        BinaryPrimitives.WriteUInt64LittleEndian(table.AsSpan(16), sample);
        Set32(table, 8, 17); // Synonym table marker
        Set32(table, 24, 0x32);
        Assert.Equal(0x32u, SqPackFormat.Locate(table, sample, CancellationToken.None));
        Assert.Throws<ArgumentException>(() => SqPackFormat.ResourceKey("chara/../example.mdl"));
    }

    [Fact]
    public void Index2MatchesTheFullPathAndSkipsSynonymMarkers()
    {
        var key = SqPackFormat.PathHash("ui/icon/040000/040001.tex");
        var table = new byte[16];
        Set32(table, 0, key);
        Set32(table, 4, 1);
        Set32(table, 8, key);
        Set32(table, 12, 0x854);
        Assert.Equal(0x854u, SqPackFormat.LocateFullPath(table, key, CancellationToken.None));
        Assert.Equal(0u, SqPackFormat.LocateFullPath(table, key + 1, CancellationToken.None));
        Assert.Throws<SqPackException>(() => SqPackFormat.LocateFullPath(table[..15], key, CancellationToken.None));
    }

    [Fact]
    public async Task StandardEntryCombinesCompressedAndPlainBlocks()
    {
        var header = EntryHeader(40, 2, (uint)Decoded.Length + 4, 2);
        var first = Block(RawDeflate, Decoded.Length);
        Set32(header, 32, (uint)first.Length);
        byte[] data = [..header, ..first, ..Block("tail"u8.ToArray())];
        var result = Assert.IsType<byte[]>(await Read(data));
        byte[] expected = [..Decoded, .."tail"u8];
        Assert.Equal(expected, result);
        Set32(data, 8, 18);
        await Assert.ThrowsAsync<SqPackException>(() => Read(data));
    }

    [Fact]
    public async Task ConcurrentReadsShareTheIndexAndDataPage()
    {
        const string resource = "chara/test/entry.mdl";
        const string indexPath = "game/sqpack/ffxiv/040000.win32.index";
        const string dataPath = "game/sqpack/ffxiv/040000.win32.dat0";
        var index = new byte[56];
        "SqPack\0"u8.CopyTo(index);
        Set32(index, 12, 24);
        Set32(index, 32, 40);
        Set32(index, 36, 16);
        BinaryPrimitives.WriteUInt64LittleEndian(index.AsSpan(40), SqPackFormat.ResourceKey(resource));
        Set32(index, 48, 16); // Entry at byte offset 128 in dat0.
        byte[] data = [.. new byte[128], .. EntryHeader(32, 2, 4, 1), .. Block("tail"u8.ToArray())];

        var hash = new string('0', 32);
        var manifest = CdnManifest.Parse(Encoding.UTF8.GetBytes(
            $"https://ff14.jijiagames.com/v3client/build/100001900/8847/apppc/1179|100001900|0.0.0.31\n"
            + $"{indexPath}|{index.Length}|{hash}\n{dataPath}|{data.Length}|{hash}"));
        var signing = new CdnSigning(new string('a', 32), "test", "test");
        var dataName = signing.FileUri(manifest, manifest.Files[dataPath], DateTimeOffset.UnixEpoch).Segments[^1];
        using var handler = new TestArchive(index, data, dataName);
        using var cdn = new OfficialCdnClient(handler, TimeProvider.System);
        var archive = new SqPackArchive(cdn, new CdnSnapshot(manifest, signing));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var first = archive.ReadAsync(resource, timeout.Token);
        await handler.PageStarted.Task.WaitAsync(timeout.Token);
        var second = archive.ReadAsync(resource, timeout.Token);
        handler.ReleasePage.TrySetResult();
        var entries = await Task.WhenAll(first, second);
        Assert.All(entries, entry => Assert.Equal("tail"u8.ToArray(), entry));
        entries[0]![0] = 0;
        Assert.Equal("tail"u8.ToArray(), await archive.ReadAsync(resource, timeout.Token));
        Assert.Equal(3, handler.IndexReads);
        Assert.Equal(1, handler.PageReads);
    }

    [Fact]
    public async Task ModelEntryBuildsLodZeroOffsetsAndLengths()
    {
        var header = EntryHeader(216, 3, 1000, 0x00010005);
        var blocks = new[]
        {
            Block("stack"u8.ToArray()), Block("runtime"u8.ToArray()),
            Block("verts"u8.ToArray()), Block("indices"u8.ToArray())
        };
        var cursor = 0u;
        var sections = new[] { 0, 1, 2, 8 };
        for (var index = 0; index < sections.Length; index++)
        {
            var section = sections[index];
            Set32(header, 112 + 4 * section, cursor);
            Set16(header, 156 + 2 * section, (ushort)index);
            Set16(header, 178 + 2 * section, 1);
            Set16(header, 208 + 2 * index, (ushort)blocks[index].Length);
            cursor += (uint)blocks[index].Length;
        }
        var decoded = Assert.IsType<byte[]>(await Read([..header, ..blocks.SelectMany(block => block)]));
        Assert.Equal("stackruntimevertsindices", Encoding.ASCII.GetString(decoded[68..]));
        Assert.Equal(0x00010005u, SqPackFormat.U32(decoded, 0));
        Assert.Equal(80u, SqPackFormat.U32(decoded, 16));
        Assert.Equal(85u, SqPackFormat.U32(decoded, 28));
        Assert.Equal(5u, SqPackFormat.U32(decoded, 40));
        Assert.Equal(7u, SqPackFormat.U32(decoded, 52));
        Assert.Equal(1, decoded[64]);
    }

    [Fact]
    public async Task TextureEntryKeepsHeaderAndTopMip()
    {
        var top = Block("top"u8.ToArray());
        var detail = Block("detail"u8.ToArray());
        var next = Block("lower"u8.ToArray());
        var header = EntryHeader(70, 4, 17, 2);
        Set32(header, 24, 3);
        Set32(header, 40, 2);
        Set16(header, 64, (ushort)top.Length);
        Set16(header, 66, (ushort)detail.Length);
        Set16(header, 68, (ushort)next.Length);
        var reads = new List<(long Offset, int Size)>();
        var decoded = Assert.IsType<byte[]>(await Read([..header, .."tex"u8, ..top, ..detail, ..next],
            (offset, size) => reads.Add((offset, size))));
        Assert.Equal("textopdetail"u8.ToArray(), decoded);
        Assert.Equal([(0L, 24), (0L, 70), (70L, 3), (73L, top.Length + detail.Length)], reads);
    }

    [Fact]
    public async Task RejectsTruncatedAndOversizedBlocks()
    {
        var header = EntryHeader(32, 2, (uint)Decoded.Length, 1);
        var truncated = Block(RawDeflate[..^4], Decoded.Length);
        await Assert.ThrowsAsync<SqPackException>(() => Read([..header, ..truncated]));
        var excessive = Block(RawDeflate, 2);
        await Assert.ThrowsAsync<SqPackException>(() => Read([..header, ..excessive]));
    }

    private static Task<byte[]?> Read(byte[] entry, Action<long, int>? record = null) => SqPackEntry.ReadAsync((offset, size, token) =>
    {
        token.ThrowIfCancellationRequested();
        record?.Invoke(offset, size);
        if (offset < 0 || offset > entry.LongLength - size)
            throw new SqPackException("Test entry was truncated");
        return Task.FromResult(entry.AsSpan((int)offset, size).ToArray());
    }, 0, CancellationToken.None);

    private static byte[] EntryHeader(int length, uint kind, uint rawSize, uint count)
    {
        var header = new byte[length];
        Set32(header, 0, (uint)length);
        Set32(header, 4, kind);
        Set32(header, 8, rawSize);
        Set32(header, 20, count);
        return header;
    }

    private static byte[] Block(byte[] bytes, int? decoded = null)
    {
        var output = new byte[16 + bytes.Length];
        Set32(output, 0, 16);
        Set32(output, 8, decoded.HasValue ? (uint)bytes.Length : 32000);
        Set32(output, 12, (uint)(decoded ?? bytes.Length));
        bytes.CopyTo(output, 16);
        return output;
    }

    private static void Set32(byte[] bytes, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);

    private static void Set16(byte[] bytes, int offset, ushort value) =>
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), value);

    private sealed class TestArchive(byte[] index, byte[] data, string dataName) : HttpMessageHandler
    {
        internal readonly TaskCompletionSource PageStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource ReleasePage = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int IndexReads;
        internal int PageReads;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var isData = request.RequestUri!.Segments[^1] == dataName;
            if (isData)
            {
                Interlocked.Increment(ref PageReads);
                PageStarted.TrySetResult();
                await ReleasePage.Task.WaitAsync(token);
            }
            else
                Interlocked.Increment(ref IndexReads);
            var bytes = isData ? data : index;
            var range = Assert.Single(request.Headers.Range!.Ranges);
            var start = (int)range.From!.Value;
            var end = (int)range.To!.Value;
            var content = new ByteArrayContent(bytes.AsSpan(start, end - start + 1).ToArray());
            content.Headers.ContentRange = new ContentRangeHeaderValue(start, end, bytes.Length);
            return new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = content };
        }
    }
}
