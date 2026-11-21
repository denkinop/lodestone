using System.Text.Json.Serialization;
using Lodestone.Core.Cdn;

namespace Lodestone.App;

internal sealed class ClientCommands : IDisposable
{
    private const string LobbyVersionSetting = "lobby-version";
    private readonly OfficialCdnClient cdn = new();
    private readonly GameVersionService versions;
    private readonly object gate = new();
    private VersionOperation? active;
    private bool disposed;

    internal ClientCommands() => versions = new GameVersionService(cdn,
        Path.Combine(FileSystem.AppDataDirectory, "cdn"));

    public PreviewLocation? PreparePreview(int characterId) => characterId == 1
        ? new PreviewLocation(PreviewResources.Root + "scene.json", PreviewResources.Root + "textures/")
        : null;

    public int GetLobbyVersion() => Preferences.Default.Get(LobbyVersionSetting, 7500);

    public bool SetLobbyVersion(int value)
    {
        if (value is < 0 or > ushort.MaxValue)
            return false;
        Preferences.Default.Set(LobbyVersionSetting, value);
        return true;
    }

    public async Task<VersionStatus> RefreshGameVersion(string requestId)
    {
        if (!Guid.TryParseExact(requestId, "D", out var id))
            return new VersionStatus(Error: "刷新请求无效");

        VersionOperation operation;
        lock (gate)
        {
            if (disposed)
                return new VersionStatus(Error: "窗口已关闭");
            if (active is not null)
                return new VersionStatus(Error: "资源版本正在检查");
            operation = active = new VersionOperation(id);
        }

        try
        {
            var version = await versions.RefreshAsync(value =>
            {
                lock (gate)
                    operation.Progress = new VersionProgress(value.BytesRead, value.TotalBytes);
            }, operation.Cancellation.Token);
            return new VersionStatus(version.Revision, version.FromCache);
        }
        catch (OperationCanceledException)
        {
            return new VersionStatus(Error: "已取消检查");
        }
        catch (OfficialCdnException ex)
        {
            return new VersionStatus(Error: ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new VersionStatus(Error: "无法保存资源版本缓存");
        }
        finally
        {
            lock (gate)
            {
                active = null;
                operation.Cancellation.Dispose();
            }
        }
    }

    public VersionProgress? GetGameVersionProgress(string requestId)
    {
        lock (gate)
            return Matches(requestId) ? active!.Progress : null;
    }

    public bool CancelGameVersion(string requestId)
    {
        lock (gate)
        {
            if (!Matches(requestId))
                return false;
            active!.Cancellation.Cancel();
            return true;
        }
    }

    private bool Matches(string requestId) =>
        active is not null && Guid.TryParseExact(requestId, "D", out var id) && active.Id == id;

    void IDisposable.Dispose() => Dispose();

    internal void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            active?.Cancellation.Cancel();
        }
        cdn.Dispose();
    }

    private sealed class VersionOperation(Guid id)
    {
        internal Guid Id { get; } = id;
        internal CancellationTokenSource Cancellation { get; } = new();
        internal VersionProgress? Progress { get; set; }
    }
}

internal sealed record PreviewLocation(
    [property: JsonPropertyName("scene")] string Scene,
    [property: JsonPropertyName("textureRoot")] string TextureRoot);

internal sealed record VersionStatus(
    [property: JsonPropertyName("revision")] string? Revision = null,
    [property: JsonPropertyName("cached")] bool Cached = false,
    [property: JsonPropertyName("error")] string? Error = null);

internal sealed record VersionProgress(
    [property: JsonPropertyName("bytesRead")] long BytesRead,
    [property: JsonPropertyName("totalBytes")] long TotalBytes);
