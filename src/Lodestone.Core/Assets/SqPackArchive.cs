using System.Globalization;
using Lodestone.Core.Cdn;
using static Lodestone.Core.Assets.SqPackFormat;

namespace Lodestone.Core.Assets;

public sealed class SqPackArchive(OfficialCdnClient cdn, CdnSnapshot snapshot)
{
    private const int PageSize = 128 << 10;
    private const int MaximumPages = 64;
    private const int MaximumIndexBytes = 64 << 20;
    private const int MaximumResourceBytes = 24 << 20;
    private const int MaximumResources = 256;
    // Requests for the same index or page share a lock while unrelated downloads can overlap.
    private readonly SemaphoreSlim[] downloadLocks = Enumerable.Range(0, 32).Select(_ => new SemaphoreSlim(1)).ToArray();
    private readonly LinkedList<(string Path, byte[] Data)> indexes = new();
    private readonly LinkedList<(string Path, long Offset, byte[] Data)> pages = new();
    private readonly Dictionary<string, LinkedListNode<(string Path, byte[]? Data)>> resources = new(StringComparer.Ordinal);
    private readonly LinkedList<(string Path, byte[]? Data)> resourceOrder = new();
    private int indexBytes;
    private int resourceBytes;

    public async Task<byte[]?> ReadAsync(string virtualPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        (bool Found, byte[]? Data) cached = default;
        lock (resources)
        {
            if (resources.TryGetValue(virtualPath, out var node))
            {
                resourceOrder.Remove(node);
                resourceOrder.AddFirst(node);
                cached = (true, node.Value.Data);
            }
        }
        if (cached.Found)
            return cached.Data?.ToArray();

        var data = await ReadEntryAsync(virtualPath, cancellationToken);
        if ((data?.Length ?? 0) <= MaximumResourceBytes)
        {
            // Keep the cached bytes isolated from callers that modify their returned buffer.
            var copy = data?.ToArray();
            var size = copy?.Length ?? 0;
            lock (resources)
            {
                if (!resources.ContainsKey(virtualPath))
                {
                    while (resourceOrder.Last is { } last
                        && (resourceBytes + size > MaximumResourceBytes || resources.Count >= MaximumResources))
                    {
                        resourceBytes -= last.Value.Data?.Length ?? 0;
                        resources.Remove(last.Value.Path);
                        resourceOrder.RemoveLast();
                    }
                    resources.Add(virtualPath, resourceOrder.AddFirst((virtualPath, copy)));
                    resourceBytes += size;
                }
            }
        }
        return data;
    }

    private async Task<byte[]?> ReadEntryAsync(string virtualPath, CancellationToken cancellationToken)
    {
        var key = ResourceKey(virtualPath);
        var fullKey = PathHash(virtualPath.ToLowerInvariant());
        var category = virtualPath.AsSpan(0, virtualPath.IndexOf('/'));
        SqPackCategory type;
        if (category.Equals("chara", StringComparison.OrdinalIgnoreCase))
            type = SqPackCategory.Character;
        else if (category.Equals("ui", StringComparison.OrdinalIgnoreCase))
            type = SqPackCategory.Ui;
        else
            throw new SqPackException("此 SqPack 类别尚未支持");

        var candidates = SqPackCatalog.Files(snapshot, type)
            .Where(file => file.Key.EndsWith(".index", StringComparison.OrdinalIgnoreCase)
                || file.Key.EndsWith(".index2", StringComparison.OrdinalIgnoreCase)
                    && !snapshot.Files.ContainsKey(file.Key[..^1]))
            .OrderBy(file => file.Key, StringComparer.OrdinalIgnoreCase);
        foreach (var index in candidates)
        {
            var index2 = index.Key.EndsWith('2');
            var table = await GetIndexAsync(index.Key, index.Value.Size, index2, cancellationToken);
            var locator = index2 ? LocateFullPath(table, fullKey, cancellationToken) : Locate(table, key, cancellationToken);
            if (locator == 0)
                continue;
            var dat = index.Key[..(index.Key.LastIndexOf('.') + 1)] + "dat" + ((locator & 15) >> 1).ToString(CultureInfo.InvariantCulture);
            if (!snapshot.Files.TryGetValue(dat, out var archive))
                throw new SqPackException("官方修复清单缺少索引对应的数据包");
            return await SqPackEntry.ReadAsync((offset, size, token) => ReadBytesAsync(dat, archive.Size, offset, size, token),
                (long)(locator & ~15u) * 8, cancellationToken);
        }
        return null;
    }

    private SemaphoreSlim DownloadLock(string path, long offset) =>
        downloadLocks[(HashCode.Combine(path, offset) & int.MaxValue) % downloadLocks.Length];

    private async Task<byte[]> GetIndexAsync(string path, long length, bool index2, CancellationToken token)
    {
        var downloadLock = DownloadLock(path, -1);
        await downloadLock.WaitAsync(token);
        try
        {
            lock (indexes)
                for (var node = indexes.First; node is not null; node = node.Next)
                {
                    if (node.Value.Path != path)
                        continue;
                    indexes.Remove(node);
                    indexes.AddFirst(node);
                    return node.Value.Data;
                }
            if (length < 24)
                throw new SqPackException("SqPack 索引截断");
            var header = await cdn.ReadRangeAsync(snapshot, path, 0, 24, token);
            var headOffset = IndexHeaderOffset(header);
            if (headOffset + 16L > length)
                throw new SqPackException("SqPack 索引头截断");
            var table = IndexTable(await cdn.ReadRangeAsync(snapshot, path, headOffset + 8L, 8, token), length, index2);
            var bytes = table.Size == 0 ? [] : await cdn.ReadRangeAsync(snapshot, path, table.Offset, table.Size, token);
            lock (indexes)
            {
                while (indexes.Last is { } last && (indexBytes + bytes.Length > MaximumIndexBytes || indexes.Count >= 8))
                {
                    indexBytes -= last.Value.Data.Length;
                    indexes.RemoveLast();
                }
                indexes.AddFirst((path, bytes));
                indexBytes += bytes.Length;
            }
            return bytes;
        }
        finally { downloadLock.Release(); }
    }

    private async Task<byte[]> ReadBytesAsync(string path, long fileLength, long offset, int length, CancellationToken token)
    {
        if (offset < 0 || length < 0 || length > RangeLimit || offset > fileLength - length)
            throw new SqPackException("SqPack 数据范围超出文件");
        if (length >= PageSize)
            return await cdn.ReadRangeAsync(snapshot, path, offset, length, token);
        var bytes = new byte[length];
        for (var written = 0; written < length;)
        {
            var start = offset - offset % PageSize;
            var page = await ReadPageAsync(path, fileLength, start, token);
            var inner = (int)(offset - start);
            var count = Math.Min(length - written, page.Length - inner);
            page.AsSpan(inner, count).CopyTo(bytes.AsSpan(written));
            offset += count;
            written += count;
        }
        return bytes;
    }

    private async Task<byte[]> ReadPageAsync(string path, long fileLength, long start, CancellationToken token)
    {
        var downloadLock = DownloadLock(path, start);
        await downloadLock.WaitAsync(token);
        try
        {
            lock (pages)
                for (var node = pages.First; node is not null; node = node.Next)
                {
                    if (node.Value.Path != path || node.Value.Offset != start)
                        continue;
                    pages.Remove(node);
                    pages.AddFirst(node);
                    return node.Value.Data;
                }
            var bytes = await cdn.ReadRangeAsync(snapshot, path, start, (int)Math.Min(PageSize, fileLength - start), token);
            lock (pages)
            {
                if (pages.Count == MaximumPages)
                    pages.RemoveLast();
                pages.AddFirst((path, start, bytes));
            }
            return bytes;
        }
        finally { downloadLock.Release(); }
    }
}
