using System.Buffers.Binary;

namespace Lodestone.Core.Assets;

public sealed class SqPackException(string message) : IOException(message);

internal static class SqPackFormat
{
    internal const int IndexEntrySize = 16;
    internal const int Index2EntrySize = 8;
    internal const int RangeLimit = 32 << 20;
    private static readonly uint[] CrcTable = MakeCrcTable();

    internal static ulong ResourceKey(string path)
    {
        var normalized = path.ToLowerInvariant();
        var slash = normalized.LastIndexOf('/');
        if (normalized.Length > 512 || slash < 1 || slash == normalized.Length - 1
            || normalized.Split('/').Any(part => part is "" or "." or ".."
                || part.Any(c => c is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-' or '.'))))
        {
            throw new ArgumentException("SqPack 资源路径无效", nameof(path));
        }
        return (ulong)PathHash(normalized.AsSpan(0, slash)) << 32 | PathHash(normalized.AsSpan(slash + 1));
    }

    internal static uint PathHash(ReadOnlySpan<char> value)
    {
        var crc = uint.MaxValue;
        foreach (var character in value)
            crc = (crc >> 8) ^ CrcTable[(crc ^ (byte)character) & 255];
        return crc;
    }

    internal static uint UpdateCrc(uint crc, ReadOnlySpan<byte> value)
    {
        foreach (var item in value)
            crc = (crc >> 8) ^ CrcTable[(crc ^ item) & 255];
        return crc;
    }

    private static uint[] MakeCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < table.Length; i++)
        {
            var crc = i;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0u : 0xedb88320u);
            table[i] = crc;
        }
        return table;
    }

    internal static uint IndexHeaderOffset(byte[] header)
    {
        if (header.Length != 24 || !header.AsSpan().StartsWith("SqPack\0"u8))
            throw new SqPackException("SqPack 索引签名无效");
        var offset = U32(header, 12);
        if (offset is < 24 or > 1 << 20)
            throw new SqPackException("SqPack 索引头超出范围");
        return offset;
    }

    internal static (long Offset, int Size) IndexTable(byte[] header, long fileSize, bool index2)
    {
        var offset = U32(header, 0);
        var size = U32(header, 4);
        if (size > RangeLimit || size % (index2 ? Index2EntrySize : IndexEntrySize) != 0
            || (long)offset + size > fileSize)
            throw new SqPackException("SqPack 索引表长度无效");
        return (offset, (int)size);
    }

    internal static uint Locate(ReadOnlySpan<byte> table, ulong key, CancellationToken token)
    {
        if (table.Length % IndexEntrySize != 0)
            throw new SqPackException("SqPack 索引表截断");
        for (var offset = 0; offset < table.Length; offset += IndexEntrySize)
        {
            if ((offset & 65535) == 0)
                token.ThrowIfCancellationRequested();
            if (U64(table, offset) != key)
                continue;
            var locator = U32(table, offset + 8);
            // Synonym table entries have a different layout and are outside this reader's coverage.
            if (locator != 0 && (locator & 1) == 0)
                return locator;
        }
        return 0;
    }

    internal static uint LocateFullPath(ReadOnlySpan<byte> table, uint key, CancellationToken token)
    {
        if (table.Length % Index2EntrySize != 0)
            throw new SqPackException("SqPack 索引表截断");
        for (var offset = 0; offset < table.Length; offset += Index2EntrySize)
        {
            if ((offset & 65535) == 0)
                token.ThrowIfCancellationRequested();
            if (U32(table, offset) != key)
                continue;
            var locator = U32(table, offset + 4);
            if (locator != 0 && (locator & 1) == 0)
                return locator;
        }
        return 0;
    }

    internal static ushort U16(ReadOnlySpan<byte> data, int offset) =>
        BinaryPrimitives.ReadUInt16LittleEndian(Field(data, offset, sizeof(ushort)));

    internal static uint U32(ReadOnlySpan<byte> data, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(Field(data, offset, sizeof(uint)));

    private static ulong U64(ReadOnlySpan<byte> data, int offset) =>
        BinaryPrimitives.ReadUInt64LittleEndian(Field(data, offset, sizeof(ulong)));

    internal static ReadOnlySpan<byte> Field(ReadOnlySpan<byte> data, int offset, int size) =>
        offset >= 0 && size >= 0 && offset <= data.Length - size
            ? data.Slice(offset, size) : throw new SqPackException("SqPack 字段截断");
}
