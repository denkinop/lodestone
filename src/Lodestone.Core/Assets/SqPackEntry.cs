using System.Buffers.Binary;
using System.IO.Compression;
using static Lodestone.Core.Assets.SqPackFormat;

namespace Lodestone.Core.Assets;

internal static class SqPackEntry
{
    private const int EntryHeaderSize = 24;
    private const int MaximumFileSize = 128 << 20;
    private const int MaximumDecodedBlock = 1 << 16;

    internal static async Task<byte[]?> ReadAsync(
        Func<long, int, CancellationToken, Task<byte[]>> read, long offset, CancellationToken token)
    {
        var first = await read(offset, EntryHeaderSize, token);
        var headerSize = U32(first, 0);
        var kind = U32(first, 4);
        var size = U32(first, 8);
        var count = U32(first, 20);
        if (kind == 1)
            return null;
        if (headerSize is < EntryHeaderSize or > 1 << 20 || size > MaximumFileSize)
            throw new SqPackException("SqPack 文件标头超出范围");
        var header = await read(offset, (int)headerSize, token);
        var start = checked(offset + headerSize);
        using var output = new MemoryStream((int)Math.Min(size, 1 << 20));
        switch (kind)
        {
            case 2:
                await ReadStandardAsync(read, start, header, count, size, output, token);
                break;
            case 3:
                await ReadModelAsync(read, start, header, output, token);
                break;
            case 4:
                await ReadTextureAsync(read, start, header, count, output, token);
                break;
            default:
                throw new SqPackException("SqPack 文件类型尚未支持");
        }
        return output.ToArray();
    }

    private static async Task ReadStandardAsync(
        Func<long, int, CancellationToken, Task<byte[]>> read, long start, byte[] header,
        uint count, uint size, MemoryStream output, CancellationToken token)
    {
        if (count > (header.Length - EntryHeaderSize) / 8)
            throw new SqPackException("SqPack 数据块索引截断");
        for (var index = 0; index < count; index++)
        {
            var entry = EntryHeaderSize + 8 * index;
            var block = await ReadBlockAsync(read, start + U32(header, entry), token);
            if (output.Length + block.Length > size)
                throw new SqPackException("SqPack 解包长度超出声明值");
            Add(output, block);
        }
        if (output.Length != size)
            throw new SqPackException("SqPack 解包长度与声明值不匹配");
    }

    private static async Task ReadModelAsync(
        Func<long, int, CancellationToken, Task<byte[]>> read, long start, byte[] header,
        MemoryStream output, CancellationToken token)
    {
        const int ModelHeaderSize = 68;
        const int BlockTable = 208;
        if (header.Length < BlockTable)
            throw new SqPackException("SqPack 模型索引截断");
        var positions = new uint[11];
        var sizes = new uint[11];
        output.SetLength(ModelHeaderSize);
        output.Position = ModelHeaderSize;
        foreach (var section in new[] { 0, 1, 2, 8 })
        {
            var position = start + U32(header, 112 + 4 * section);
            var first = U16(header, 156 + 2 * section);
            var blocks = U16(header, 178 + 2 * section);
            positions[section] = (uint)output.Length;
            await ReadSequentialAsync(read, position, header, BlockTable, first, blocks, output, token);
            sizes[section] = (uint)output.Length - positions[section];
        }

        var modelHeader = new byte[ModelHeaderSize];
        U32To(modelHeader, 0, U32(header, 20));
        U32To(modelHeader, 4, sizes[0]);
        U32To(modelHeader, 8, sizes[1]);
        Field(header, 200, 4).CopyTo(modelHeader.AsSpan(12));
        U32To(modelHeader, 16, positions[2]);
        U32To(modelHeader, 28, positions[8]);
        U32To(modelHeader, 40, sizes[2]);
        U32To(modelHeader, 52, sizes[8]);
        modelHeader[64] = 1;
        output.Position = 0;
        output.Write(modelHeader);
    }

    private static async Task ReadTextureAsync(
        Func<long, int, CancellationToken, Task<byte[]>> read, long start, byte[] header,
        uint lods, MemoryStream output, CancellationToken token)
    {
        if (lods == 0 || lods > (header.Length - EntryHeaderSize) / 20)
            throw new SqPackException("SqPack 贴图索引截断");
        var firstOffset = U32(header, 24);
        var firstBlock = U32(header, 36);
        var count = U32(header, 40);
        if (firstOffset > 1 << 20)
            throw new SqPackException("SqPack 贴图标头超出范围");
        if (firstOffset > 0)
            Add(output, await read(start, (int)firstOffset, token));
        await ReadSequentialAsync(read, start + firstOffset, header, EntryHeaderSize + (int)lods * 20,
            firstBlock, count, output, token);
    }

    private static async Task ReadSequentialAsync(
        Func<long, int, CancellationToken, Task<byte[]>> read, long position, byte[] header,
        int table, uint first, uint count, MemoryStream output, CancellationToken token)
    {
        if ((ulong)first + count > (ulong)(header.Length - table) / 2)
            throw new SqPackException("SqPack 数据块长度表截断");
        for (var index = 0u; index < count; index++)
        {
            var stride = U16(header, table + (int)(first + index) * 2);
            if (stride < 16)
                throw new SqPackException("SqPack 块间距无效");
            Add(output, await ReadBlockAsync(read, position, token, stride));
            position += stride;
        }
    }

    private static async Task<byte[]> ReadBlockAsync(
        Func<long, int, CancellationToken, Task<byte[]>> read, long offset,
        CancellationToken token, int? stride = null)
    {
        token.ThrowIfCancellationRequested();
        var header = await read(offset, 16, token);
        var headerSize = U32(header, 0);
        var stored = U32(header, 8);
        var size = U32(header, 12);
        if (headerSize is < 16 or > 128 || size > MaximumDecodedBlock || stored is 0 or > 32000)
            throw new SqPackException("SqPack 数据块标头无效");

        var compressed = stored != 32000;
        var storedSize = (int)(compressed ? stored : size);
        if (stride is { } blockSize && headerSize + storedSize > blockSize)
            throw new SqPackException("SqPack 数据块超出其索引范围");
        if (storedSize == 0)
            return [];
        var bytes = await read(offset + headerSize, storedSize, token);
        if (!compressed)
            return bytes;
        try
        {
            using var input = new MemoryStream(bytes, writable: false);
            using var deflate = new DeflateStream(input, CompressionMode.Decompress);
            var decoded = new byte[size];
            await deflate.ReadExactlyAsync(decoded, token);
            if (deflate.ReadByte() != -1)
                throw new SqPackException("SqPack 解压长度超出声明值");
            return decoded;
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
        {
            throw new SqPackException("SqPack Deflate 数据损坏或截断");
        }
    }

    private static void Add(MemoryStream output, byte[] bytes)
    {
        if (output.Length + bytes.Length > MaximumFileSize)
            throw new SqPackException("SqPack 解包数据超过上限");
        output.Write(bytes);
    }

    private static void U32To(byte[] header, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(offset), value);
}
