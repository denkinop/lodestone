using System.Runtime.CompilerServices;
using Lodestone.Core.Assets;
using Lodestone.Core.Cdn;

namespace Lodestone.App;

internal sealed class GamePreviewSource(OfficialCdnClient cdn)
{
    private readonly SemaphoreSlim snapshotLock = new(1);
    private readonly ConditionalWeakTable<CharacterScene, IconCache> iconCaches = new();
    private CdnSnapshot? snapshot;
    private CharacterScene? builder;
    private long checkedAt;

    internal async Task<(CharacterScene Builder, string Revision)> GetAsync(CancellationToken token)
    {
        await snapshotLock.WaitAsync(token);
        try
        {
            var timestamp = Interlocked.Read(ref checkedAt);
            if (snapshot is null || timestamp == 0 || Environment.TickCount64 - timestamp > 300_000)
            {
                var current = await cdn.GetSnapshotAsync(token);
                if (snapshot?.Fingerprint != current.Fingerprint)
                    builder = new CharacterScene(new SqPackArchive(cdn, current));
                snapshot = current;
                Interlocked.Exchange(ref checkedAt, Environment.TickCount64);
            }
            return (builder!, snapshot.Revision);
        }
        finally { snapshotLock.Release(); }
    }

    internal void Refresh() => Interlocked.Exchange(ref checkedAt, 0);

    internal async Task<byte[]?> ReadIconAsync(int id, CancellationToken token)
    {
        if (id is < 1 or > 999999)
            return null;
        var (current, _) = await GetAsync(token);
        return await iconCaches.GetValue(current, static _ => new IconCache()).ReadAsync(current, id, token);
    }

    internal async Task<Stream?> ReadWardrobeIconAsync(int id)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var token = timeout.Token;
        try
        {
            var bytes = await ReadIconAsync(id, token);
            return bytes is null ? null : new MemoryStream(bytes, writable: false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or OverflowException or ArgumentException
            or ObjectDisposedException)
        {
            return null;
        }
    }

    private sealed class IconCache
    {
        private readonly SemaphoreSlim[] downloads = Enumerable.Range(0, 16).Select(_ => new SemaphoreSlim(1)).ToArray();
        private readonly Dictionary<int, byte[]> icons = [];
        private readonly Queue<int> order = new();

        internal async Task<byte[]?> ReadAsync(CharacterScene scene, int id, CancellationToken token)
        {
            var download = downloads[id % downloads.Length];
            await download.WaitAsync(token);
            try
            {
                lock (icons)
                    if (icons.TryGetValue(id, out var cached))
                        return cached;
                if (await scene.ReadIconAsync(id, token) is not { } bytes)
                    return null;
                lock (icons)
                {
                    if (order.Count == 72)
                        icons.Remove(order.Dequeue());
                    icons.Add(id, bytes);
                    order.Enqueue(id);
                }
                return bytes;
            }
            finally { download.Release(); }
        }
    }
}
