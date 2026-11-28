using System.Text.Json;
using System.Text.Json.Serialization;
using Lodestone.Core.Assets;
using Lodestone.Core.Auth;
using Lodestone.Core.Cdn;
using Lodestone.Core.Community;

namespace Lodestone.App;

internal sealed partial class ClientCommands : IDisposable
{
    private const string LobbyVersionSetting = "lobby-version";
    private static readonly ExdDataStore Exd = new(Path.Combine(FileSystem.AppDataDirectory, "exd", "zh-cn.json.gz"));
    private readonly OfficialCdnClient cdn = new();
    private readonly OfficialHomeClient officialHome = new();
    private readonly GameVersionService versions;
    private readonly GameCommands game;
    private readonly GamePreviewSource previewSource;
    private readonly GamePreview preview;
    private readonly GamePreview wardrobe;
    private readonly CommunityBrowser browser = new();
    private CommunityConnection? community;
    private readonly object gate = new();
    private VersionOperation? active;
    private readonly CancellationTokenSource lifetime = new();
    private string? exdProgress;
    private bool disposed;

    internal ClientCommands()
    {
        versions = new GameVersionService(cdn, Path.Combine(FileSystem.AppDataDirectory, "cdn"));
        game = new GameCommands(versions, GetLobbyVersion, Exd);
        previewSource = new GamePreviewSource(cdn);
        preview = new GamePreview(previewSource, game.FindPreviewCharacter, "/_game_preview/");
        wardrobe = new GamePreview(previewSource, game.FindSelectedPreviewCharacter, "/_wardrobe/", () => Exd.Current.Character.Wardrobe);
        game.SessionChanged += OnGameSessionChanged;
    }

    public Task<OfficialFeed<OfficialBanner>> GetOfficialBanners() => officialHome.GetBannersAsync();
    public Task<OfficialFeed<OfficialNews>> GetOfficialNews() => officialHome.GetNewsAsync();
    public Task<OfficialFeed<OfficialActivity>> GetOfficialCalendar(string month) => officialHome.GetCalendarAsync(month);
    public Task<AreaResponse> GetGameAreas() => game.GetAreasAsync();
    public GameResponse? GetCurrentGame() => game.GetCurrent();
    public Task<ExdWeatherView> GetExdWeather() => Task.Run(() => WeatherView(Exd.Current));
    public string? GetExdProgress() => Volatile.Read(ref exdProgress);

    public async Task<ExdUpdateResult> RefreshExd()
    {
        CancellationToken token;
        lock (gate)
        {
            if (disposed)
                return new(Error: "窗口已关闭");
            if (exdProgress is not null)
                return new(Error: "EXD 数据正在更新");
            token = lifetime.Token;
            Volatile.Write(ref exdProgress, "正在连接 XIVAPI…");
        }

        try
        {
            var catalog = await Task.Run(() => Exd.RefreshAsync(status => Volatile.Write(ref exdProgress, status), token), token);
            return new(Data: WeatherView(catalog));
        }
        catch (HttpRequestException ex)
        {
            return new(Error: ex.StatusCode is { } code ? $"XIVAPI 返回 HTTP {(int)code}，请稍后重试"
                : "无法连接 XIVAPI，请检查网络后重试");
        }
        catch (OperationCanceledException)
        {
            return new(Error: token.IsCancellationRequested ? "EXD 更新已取消" : "XIVAPI 响应超时，请稍后重试");
        }
        catch (InvalidDataException ex) { return new(Error: ex.Message); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(Error: "无法读取或保存 EXD 快照，请稍后重试");
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or KeyNotFoundException
            or ArgumentException or FormatException or OverflowException or IndexOutOfRangeException)
        {
            return new(Error: "XIVAPI 返回的 EXD 数据格式无效");
        }
        finally
        {
            lock (gate)
                Volatile.Write(ref exdProgress, null);
        }
    }

    private static ExdWeatherView WeatherView(ExdCatalog catalog) => new(catalog.Version, catalog.Weather);

    public Task<int?> GetSavedGameArea() => game.GetSavedAreaAsync();
    public Task<GameResponse> BeginGameLogin(string id, string mode, string account, int areaId, bool remember) =>
        game.LoginAsync(id, mode, account, areaId, remember);
    public Task<GameResponse> SelectServiceAccount(string id, int index) => game.SelectAsync(id, index, refresh: false);
    public async Task<GameResponse> RefreshLobby(string id, int index)
    {
        var result = await game.SelectAsync(id, index, refresh: true);
        if (result.Error is null)
            previewSource.Refresh();
        return result;
    }
    public GameResponse ChooseGameCharacter(string id)
    {
        preview.Invalidate();
        wardrobe.Invalidate();
        return game.ChooseCharacter(id);
    }
    public GameProgress? GetGameProgress(string id) => game.GetProgress(id);
    public bool CancelGameOperation(string id) => game.Cancel(id);
    public Task<bool> SignOutGame() => game.SignOutAsync();
    public CommunityStatus? GetCommunityStatus()
    {
        var connection = Volatile.Read(ref community);
        var snapshot = connection is not null && ReferenceEquals(connection.Owner, game.CurrentCommunity)
            ? connection.Session.Snapshot : null;
        return snapshot is null ? null : new CommunityStatus(snapshot.State, snapshot.JobsStatus, snapshot.JobProfile is { } profile
                ? new CommunityJobStatus(profile.Character, profile.Area, profile.World, profile.Levels) : null);
    }

    public Task<string?> SelectCommunityCharacter(string contentId) => MainThread.InvokeOnMainThreadAsync(async () =>
    {
        if (game.FindCommunityCharacter(contentId) is not { } selection || !ReferenceEquals(selection.Owner, community?.Owner))
            return "无法核对所选游戏角色，请重新读取角色列表。";
        return await browser.SelectRoleAsync(selection.Character, () => game.FindCommunityCharacter(contentId) == selection);
    });

    private void OnGameSessionChanged()
    {
        preview.Invalidate();
        wardrobe.Invalidate();
        MainThread.BeginInvokeOnMainThread(UpdateCommunitySession);
    }

    private void UpdateCommunitySession()
    {
        var owner = game.CurrentCommunity;
        if (ReferenceEquals(owner, community?.Owner))
            return;
        var session = owner is null ? null : new RisingStonesSession(owner.AccountId, reusesSession: true,
            restoresSavedSession: owner.Ticket is null && owner.SavedCookies.Count > 0);
        Volatile.Write(ref community, owner is not null && session is not null ? new CommunityConnection(owner, session) : null);
        browser.SetSession(session, owner?.FederatedCookies ?? [],
            owner is { Remember: true } ? cookies => game.SaveCommunityAsync(owner, cookies) : null);
        if (owner is null || session is null)
            return;
        if (owner.Ticket is { } exchange)
            _ = ResumeCommunityAsync(owner, session, exchange);
        else
            _ = browser.ResumeSavedAsync(owner.SavedCookies);
    }

    private async Task ResumeCommunityAsync(
        CommunityAccess owner, RisingStonesSession session, Task<CommunityTicketResult> exchange)
    {
        try
        {
            var result = await exchange;
            if (ReferenceEquals(game.CurrentCommunity, owner))
                await browser.ResumeAsync(result.TakeCallback(), result.Code);
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(game.CurrentCommunity, owner))
                session.RequireConfirmation();
        }
    }

    internal void GetLoginResource(object? sender, WebViewWebResourceRequestedEventArgs request)
    {
        if (!LodestoneApplication.IsLocalWebViewUri(request.Uri) || !request.Uri.AbsolutePath.StartsWith("/_login/", StringComparison.Ordinal))
            return;
        request.Handled = true;
        var content = game.OpenQr(request.Uri);
        request.SetResponse(content is null ? 404 : 200, content is null ? "Not Found" : "OK", "image/png",
            Task.FromResult(content));
    }

    public Task<GamePreviewResult> PrepareGamePreview(string characterId, string requestId) => preview.PrepareAsync(characterId, requestId);
    public bool ReleaseGamePreview(string requestId) => preview.Release(requestId);
    public bool SaveGamePortrait(string requestId, string mode, string png) => preview.SavePortrait(requestId, mode, png);
    public Task<WardrobePage> SearchWardrobe(string slot, string encodedQuery, int page) => Task.Run(() =>
        Exd.Current.Character.Wardrobe.Search(slot, Uri.UnescapeDataString(encodedQuery), page));
    public IReadOnlyList<EquipmentStain> GetWardrobeDyes() => Exd.Current.Character.Wardrobe.Dyes;

    public Task<GamePreviewResult> PrepareWardrobePreview(string characterId, string requestId, string changes)
    {
        try
        {
            if (changes is { Length: <= 1024 } && JsonSerializer.Deserialize<WardrobeChange[]>(changes, PreviewCache.Json) is { } outfit)
                return wardrobe.PrepareAsync(characterId, requestId, outfit);
        }
        catch (JsonException) { }
        return Task.FromResult(new GamePreviewResult(Error: "无法读取试穿组合"));
    }

    public bool ReleaseWardrobePreview(string requestId) => wardrobe.Release(requestId);

    internal void GetGamePreviewResource(object? sender, WebViewWebResourceRequestedEventArgs request)
    {
        preview.Respond(sender, request);
        if (!request.Handled)
            wardrobe.Respond(sender, request);
    }

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
            lifetime.Cancel();
            lifetime.Dispose();
#if ANDROID
            portraitTransfers.Clear();
#endif
        }
        game.SessionChanged -= OnGameSessionChanged;
        preview.Dispose();
        wardrobe.Dispose();
        game.Dispose();
        Volatile.Write(ref community, null);
        MainThread.BeginInvokeOnMainThread(() => browser.SetSession(null, []));
        officialHome.Dispose();
        market.Dispose();
        cdn.Dispose();
    }

    private sealed record CommunityConnection(CommunityAccess Owner, RisingStonesSession Session);

    private sealed class VersionOperation(Guid id)
    {
        internal Guid Id { get; } = id;
        internal CancellationTokenSource Cancellation { get; } = new();
        internal VersionProgress? Progress { get; set; }
    }
}

internal sealed record ExdWeatherView(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("catalog")] WeatherCatalog Catalog);

internal sealed record ExdUpdateResult(
    [property: JsonPropertyName("data")] ExdWeatherView? Data = null,
    [property: JsonPropertyName("error")] string? Error = null);

internal sealed record VersionStatus(
    [property: JsonPropertyName("revision")] string? Revision = null,
    [property: JsonPropertyName("cached")] bool Cached = false,
    [property: JsonPropertyName("error")] string? Error = null);

internal sealed record VersionProgress(
    [property: JsonPropertyName("bytesRead")] long BytesRead,
    [property: JsonPropertyName("totalBytes")] long TotalBytes);

internal sealed record CommunityStatus(
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("jobsStatus")] string JobsStatus,
    [property: JsonPropertyName("jobProfile")] CommunityJobStatus? JobProfile);

internal sealed record CommunityJobStatus(
    [property: JsonPropertyName("character")] string Character,
    [property: JsonPropertyName("area")] string Area,
    [property: JsonPropertyName("world")] string World,
    [property: JsonPropertyName("levels")] IReadOnlyDictionary<string, int> Levels);
