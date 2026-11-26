using static Lodestone.Core.Assets.CharacterModel;
using static Lodestone.Core.Assets.SqPackFormat;

namespace Lodestone.Core.Assets;

internal sealed class StainingTemplate
{
    private readonly byte[] data;
    private readonly Dictionary<int, (int Offset, int Length)> entries = [];
    private readonly int columns;
    private readonly int stainCount;

    internal StainingTemplate(byte[] data)
    {
        this.data = data;
        var header = Field(data, 0, 8);
        var version = U16(header, 2);
        var count = U16(header, 4);
        if (U16(header, 0) != 0x534d || count == 0)
            throw new SqPackException("游戏染色模板标头无效");
        columns = version switch
        {
            0x0101 when header[6] == 0 && header[7] == 0 => 5,
            0x0200 or 0x0201 when header[6] == 3 && header[7] == 9 => 12,
            _ => throw new SqPackException("不支持此游戏染色模板格式"),
        };

        // Older STM files use 16-bit keys and offsets. Legacy templates kept their version when the index widened.
        var indexWidth = version == 0x0200 || version == 0x0101 && U16(data, 10) != 0 ? 2 : 4;
        stainCount = indexWidth == 2 ? 128 : 254;
        var offsets = 8 + count * indexWidth;
        var content = 8 + count * indexWidth * 2;
        Field(data, 8, content - 8);
        for (var index = 0; index < count; index++)
        {
            var key = ReadIndex(8 + index * indexWidth);
            var start = EntryOffset(index);
            var end = index + 1 < count ? EntryOffset(index + 1) : data.Length;
            if (end - start < columns * 2 || !entries.TryAdd(checked((int)key), (start, end - start)))
                throw new SqPackException("游戏染色模板索引无效");
        }

        uint ReadIndex(int offset) => indexWidth == 2 ? U16(data, offset) : U32(data, offset);
        int EntryOffset(int index)
        {
            var offset = content + ReadIndex(offsets + index * indexWidth) * 2L;
            return offset <= data.Length ? (int)offset : throw new SqPackException("游戏染色模板偏移超出文件");
        }
    }

    internal bool TryRead(int template, int stain, int column, Span<float> values)
    {
        if (stain < 1 || stain > stainCount || column < 0 || column >= columns
            || !entries.TryGetValue(template, out var entry))
            return false;
        var row = Field(data, entry.Offset, entry.Length);
        var start = column == 0 ? 0 : 2 * U16(row, (column - 1) * 2);
        var end = 2 * U16(row, column * 2);
        var array = Field(row, columns * 2 + start, end - start);
        var width = column < 3 ? 6 : 2;
        int offset;
        if (array.Length == width)
            offset = 0;
        else if (array.Length == width * stainCount)
            offset = (stain - 1) * width;
        else
        {
            // Indexed columns store their values first, then one-based indices with an unused leading byte.
            var indexStart = array.Length - stainCount;
            if (indexStart < width || indexStart % width != 0 || stain == stainCount)
                return false;
            var index = array[indexStart + stain] - 1;
            if (index < 0 || index >= indexStart / width)
                return false;
            offset = index * width;
        }
        for (var index = 0; index < width / 2; index++)
            values[index] = F16(array, offset + index * 2);
        return true;
    }
}
