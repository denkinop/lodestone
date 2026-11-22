using Lodestone.Core.Assets;
using Lodestone.Core.Cdn;

namespace Lodestone.App;

internal sealed class GamePreviewSource(OfficialCdnClient cdn)
{
    private readonly SemaphoreSlim snapshotLock = new(1);
    private readonly SemaphoreSlim iconLock = new(1);
    private readonly Dictionary<int, byte[]> icons = [];
    private readonly Queue<int> iconOrder = new();
    private CdnSnapshot? snapshot;
    private CharacterScene? builder;
    private CharacterScene? iconSource;
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

    internal async Task<Stream?> ReadWardrobeIconAsync(int id)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var token = timeout.Token;
        try
        {
            await iconLock.WaitAsync(token);
            try
            {
                var (current, _) = await GetAsync(token);
                if (current != iconSource)
                {
                    icons.Clear();
                    iconOrder.Clear();
                    iconSource = current;
                }
                if (!icons.TryGetValue(id, out var bytes))
                {
                    if (await current.ReadIconAsync(id, token) is not { } png)
                        return null;
                    if (iconOrder.Count == 72)
                        icons.Remove(iconOrder.Dequeue());
                    icons.Add(id, bytes = png);
                    iconOrder.Enqueue(id);
                }
                return new MemoryStream(bytes, writable: false);
            }
            finally { iconLock.Release(); }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or OverflowException or ArgumentException
            or ObjectDisposedException)
        {
            return null;
        }
    }
}
