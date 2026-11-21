using System.Buffers.Binary;
using System.Text;
using Lodestone.Core.Assets;
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
        var next = Block("lower"u8.ToArray());
        var header = EntryHeader(68, 4, 11, 2);
        Set32(header, 24, 3);
        Set32(header, 40, 1);
        Set16(header, 64, (ushort)top.Length);
        Set16(header, 66, (ushort)next.Length);
        var decoded = Assert.IsType<byte[]>(await Read([..header, .."tex"u8, ..top, ..next]));
        Assert.Equal("textop"u8.ToArray(), decoded);
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

    private static Task<byte[]?> Read(byte[] entry) => SqPackEntry.ReadAsync((offset, size, token) =>
    {
        token.ThrowIfCancellationRequested();
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
}
