using System.Text.Json.Serialization;
using Lodestone.Core.Auth;
using Lodestone.Core.Cdn;
using Lodestone.Core.Community;

namespace Lodestone.App;

internal sealed class ClientCommands : IDisposable
{
    private const string LobbyVersionSetting = "lobby-version";
    private readonly OfficialCdnClient cdn = new();
    private readonly GameVersionService versions;
    private readonly GameCommands game;
    private readonly GamePreview preview;
    private readonly CommunityBrowser browser = new();
    private RisingStonesSession? community;
    private GameAuthorization? communityOwner;
    private readonly object gate = new();
    private VersionOperation? active;
    private bool disposed;

    internal ClientCommands()
    {
        versions = new GameVersionService(cdn, Path.Combine(FileSystem.AppDataDirectory, "cdn"));
        game = new GameCommands(versions, GetLobbyVersion);
        preview = new GamePreview(cdn, game.FindPreviewCharacter);
        game.SessionChanged += OnGameSessionChanged;
    }

    public Task<AreaResponse> GetGameAreas() => game.GetAreasAsync();
    public Task<int?> GetSavedGameArea() => game.GetSavedAreaAsync();
    public Task<GameResponse> BeginGameLogin(string id, string mode, string account, int areaId, bool remember) =>
        game.LoginAsync(id, mode, account, areaId, remember);
    public Task<GameResponse> SelectServiceAccount(string id, int index) => game.SelectAsync(id, index, refresh: false);
    public async Task<GameResponse> RefreshLobby(string id, int index)
    {
        var result = await game.SelectAsync(id, index, refresh: true);
        if (result.Error is null)
            preview.RefreshSource();
        return result;
    }
    public GameResponse ChooseGameCharacter(string id)
    {
        preview.Invalidate();
        return game.ChooseCharacter(id);
    }
    public GameProgress? GetGameProgress(string id) => game.GetProgress(id);
    public bool CancelGameOperation(string id) => game.Cancel(id);
    public Task<bool> SignOutGame() => game.SignOutAsync();
    public CommunityStatus? GetCommunityStatus()
    {
        var snapshot = Volatile.Read(ref community)?.Snapshot;
        return snapshot is null ? null : new CommunityStatus(snapshot.State, snapshot.JobProfile is { } profile
                ? new CommunityJobStatus(profile.Character, profile.Area, profile.World, profile.Levels) : null);
    }

    public Task<string?> SelectCommunityCharacter(string contentId) => MainThread.InvokeOnMainThreadAsync(async () =>
    {
        if (game.FindCommunityCharacter(contentId) is not { } selection || !ReferenceEquals(selection.Authorization, communityOwner))
            return "无法核对所选游戏角色，请重新读取角色列表。";
        return await browser.SelectRoleAsync(selection.Character, () => game.FindCommunityCharacter(contentId) == selection);
    });

    private void OnGameSessionChanged()
    {
        preview.Invalidate();
        MainThread.BeginInvokeOnMainThread(UpdateCommunitySession);
    }

    private void UpdateCommunitySession()
    {
        var (owner, exchange) = game.CurrentAuthorization;
        if (ReferenceEquals(owner, communityOwner))
            return;
        communityOwner = owner;
        var session = owner is null ? null : new RisingStonesSession(owner.AccountId, reusesSession: true);
        Volatile.Write(ref community, session);
        browser.SetSession(session, owner?.CommunityCookies ?? []);
        if (owner is not null && session is not null && exchange is not null)
            _ = ResumeCommunityAsync(owner, session, exchange);
    }

    private async Task ResumeCommunityAsync(
        GameAuthorization owner, RisingStonesSession session, Task<CommunityTicketResult> exchange)
    {
        try
        {
            var result = await exchange;
            if (ReferenceEquals(game.CurrentAuthorization.Authorization, owner))
                await browser.ResumeAsync(result.TakeCallback(), result.Code);
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(game.CurrentAuthorization.Authorization, owner))
                session.RequireConfirmation();
        }
    }

    internal void GetLoginResource(object? sender, WebViewWebResourceRequestedEventArgs request)
    {
        if (!PreviewResources.IsLocal(request.Uri) || !request.Uri.AbsolutePath.StartsWith("/_login/", StringComparison.Ordinal))
            return;
        request.Handled = true;
        var content = game.OpenQr(request.Uri);
        request.SetResponse(content is null ? 404 : 200, content is null ? "Not Found" : "OK", "image/png",
            Task.FromResult(content));
    }

    public PreviewLocation? PreparePreview(int characterId) => characterId == 1
        ? new PreviewLocation(PreviewResources.Root + "scene.json", PreviewResources.Root + "textures/")
        : null;

    public Task<GamePreviewResult> PrepareGamePreview(string characterId, string requestId) => preview.PrepareAsync(characterId, requestId);
    public bool ReleaseGamePreview(string requestId) => preview.Release(requestId);
    public bool SaveGamePortrait(string requestId, string mode, string png) => preview.SavePortrait(requestId, mode, png);
    internal void GetGamePreviewResource(object? sender, WebViewWebResourceRequestedEventArgs request) => preview.Respond(sender, request);

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
        game.SessionChanged -= OnGameSessionChanged;
        preview.Dispose();
        game.Dispose();
        Volatile.Write(ref community, null);
        MainThread.BeginInvokeOnMainThread(() => browser.SetSession(null, []));
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

internal sealed record CommunityStatus(
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("jobProfile")] CommunityJobStatus? JobProfile);

internal sealed record CommunityJobStatus(
    [property: JsonPropertyName("character")] string Character,
    [property: JsonPropertyName("area")] string Area,
    [property: JsonPropertyName("world")] string World,
    [property: JsonPropertyName("levels")] IReadOnlyDictionary<string, int> Levels);
