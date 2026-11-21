using System.Globalization;
using System.Text.Json.Serialization;
using Lodestone.Core.Assets;
using Lodestone.Core.Auth;
using Lodestone.Core.Cdn;
using Lodestone.Core.Community;
using Lodestone.Core.Lobby;

namespace Lodestone.App;

internal sealed class GameCommands(GameVersionService versions, Func<int> lobbyVersion)
{
    private const string InstallationKey = "sdo-installation";
    private static readonly string[] RaceNames = ["", "人族", "精灵族", "拉拉菲尔族", "猫魅族", "鲁加族", "敖龙族", "硌狮族", "维埃拉族"];
    private readonly GameAreaDirectory directory = new(Path.Combine(FileSystem.AppDataDirectory, "game-areas.js"));
    private readonly GameLobbyClient lobby = new();
    private readonly SavedGameLoginStore savedLogins = new();
    private readonly CharacterCache cache = new(FileSystem.AppDataDirectory);
    private readonly object gate = new();
    private Dictionary<int, GameArea> areas = [];
    private LoginOperation? operation;
    private GameSession? session;
    private Task<bool>? signingOut;
    private bool disposed;
    internal event Action? SessionChanged;
    internal (GameAuthorization? Authorization, Task<CommunityTicketResult>? Community) CurrentAuthorization
    {
        get { lock (gate) return (session?.Authorization, session?.Community.Ticket); }
    }

    internal async Task<AreaResponse> GetAreasAsync()
    {
        try
        {
            var fetched = await directory.GetAsync();
            lock (gate)
                areas = fetched.ToDictionary(area => area.Id);
            return new AreaResponse(fetched.Select(area => new AreaOption(area.Id, area.Name)).ToArray());
        }
        catch (Exception ex) when (ex is LobbyException or ObjectDisposedException)
        {
            return new AreaResponse([], "无法读取官方大区目录");
        }
    }

    internal async Task<int?> GetSavedAreaAsync() => (await savedLogins.ReadAsync(GetInstallation()))?.AreaId;

    internal async Task<GameResponse> LoginAsync(string requestId, string mode, string account, int areaId, bool remember)
    {
        if (!Guid.TryParseExact(requestId, "D", out var id) || mode is not ("phone" or "scan" or "saved"))
            return Failed("登录请求无效");
        var restoring = mode == "saved";
        LoginOperation current;
        GameArea area;
        lock (gate)
        {
            if (disposed || operation is not null || signingOut is { IsCompleted: false })
                return Failed("已有操作正在处理");
            if (!areas.TryGetValue(areaId, out var selected))
                return Failed("请选择有效大区");
            area = selected;
            ClearSession();
            current = operation = new LoginOperation(id, restoring ? "正在恢复盛趣登录" : "正在连接盛趣");
        }
        SessionChanged?.Invoke();

        SdoAuthenticator? auth = null;
        try
        {
            var token = current.Cancellation.Token;
            var installation = GetInstallation();
            auth = new SdoAuthenticator(installation);
            var credentialStored = false;
            void PublishChallenge(LoginChallenge challenge)
            {
                lock (gate)
                {
                    current.Challenge = challenge;
                    current.Status = challenge is PhoneConfirmation ? "请在叨鱼确认登录" : "请用叨鱼扫描二维码";
                }
            }
            GameAuthorization authorization;
            if (restoring)
            {
                var previous = await savedLogins.ReadAsync(installation) ?? throw new SavedGameLoginRejectedException();
                authorization = await auth.RestoreAsync(previous.Login, async renewed =>
                    credentialStored = await SaveLoginAsync(current, installation, areaId, renewed, CancellationToken.None), token);
            }
            else
            {
                authorization = mode == "phone"
                    ? await auth.ConfirmByPhoneAsync(account, PublishChallenge, token, remember)
                    : await auth.ConfirmByScanAsync(PublishChallenge, token, remember);
            }
            var directoryPath = cache.AccountPath(authorization.AccountId, areaId);
            var catalog = CharacterCache.Read(directoryPath);
            var firstRead = catalog is null;
            if (catalog is null)
            {
                var result = await ReadLobbyAsync(current, area, authorization, null);
                catalog = new CachedCatalog([], [], null, null).WithRoster(result);
            }
            token.ThrowIfCancellationRequested();
            string? notice = null;
            if (!restoring)
            {
                if (authorization.SavedLogin is { } saved)
                    credentialStored = await SaveLoginAsync(current, installation, areaId, saved, token);
                else if (!await savedLogins.ForgetAsync())
                    notice = "无法清除本机此前保存的登录状态";
            }
            if (notice is null && (remember || restoring) && !credentialStored)
                notice = "已登录，本次未能保存登录状态";
            GameResponse response;
            lock (gate)
            {
                token.ThrowIfCancellationRequested();
                if (firstRead)
                    CharacterCache.Save(directoryPath, catalog);
                var next = new GameSession(authorization, area, directoryPath, new CommunityAttempt(auth, authorization), catalog);
                session = next;
                response = Present(next);
                auth = null;
            }
            SessionChanged?.Invoke();
            return response with { Notice = notice };
        }
        catch (OperationCanceledException)
        {
            return Failed("已取消登录");
        }
        catch (SavedGameLoginRejectedException ex)
        {
            var cleared = await savedLogins.ForgetAsync();
            return Failed(cleared ? ex.Message : "保存的盛趣登录已失效，且无法清除，请重新授权");
        }
        catch (Exception ex) when (ex is SdoAuthException or LobbyException or OfficialCdnException)
        {
            return Failed(ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            return Failed("无法完成游戏登录，请稍后重试");
        }
        finally
        {
            auth?.Dispose();
            Finish(current);
        }
    }

    private Task<bool> SaveLoginAsync(LoginOperation current, Guid installation, int areaId, SavedGameLogin login,
        CancellationToken token)
    {
        lock (gate)
            return current.SignedOut ? Task.FromResult(false) : savedLogins.SaveAsync(installation, areaId, login, token);
    }

    private async Task<LobbyRoster> ReadLobbyAsync(LoginOperation current, GameArea area, GameAuthorization authorization, int? index)
    {
        lock (gate)
        {
            current.Challenge = null;
            current.Status = "正在检查游戏版本并连接 Lobby";
        }
        var token = current.Cancellation.Token;
        var version = await versions.RefreshAsync(cancellationToken: token);
        return await lobby.ReadAsync(area, authorization, version.VersionInfo, (ushort)lobbyVersion(), index, token);
    }

    internal async Task<GameResponse> SelectAsync(string requestId, int index, bool refresh)
    {
        if (!Guid.TryParseExact(requestId, "D", out var id) || index is < -1 or > 7)
            return Failed("服务账号选择无效");
        LoginOperation current;
        GameSession authorized;
        lock (gate)
        {
            if (disposed || operation is not null || session is null)
                return Failed("游戏会话不可用，请重新登录");
            authorized = session;
            if (!refresh)
            {
                if (!authorized.Catalog.Accounts.Any(account => account.Index == index))
                    return Failed("所选服务账号不可用");
                authorized.ViewIndex = index;
                return Present(authorized);
            }
            current = operation = new LoginOperation(id, "正在读取角色列表");
        }
        try
        {
            var entries = await directory.GetAsync(current.Cancellation.Token, refresh: true);
            var area = entries.FirstOrDefault(entry => entry.Id == authorized.Area.Id) ?? authorized.Area;
            var result = await ReadLobbyAsync(current, area, authorized.Authorization, index == -1 ? null : index);
            var catalog = authorized.Catalog.WithRoster(result);
            lock (gate)
            {
                current.Cancellation.Token.ThrowIfCancellationRequested();
                CharacterCache.Save(authorized.Directory, catalog);
                areas = entries.ToDictionary(entry => entry.Id);
                authorized.Area = area;
                authorized.Catalog = catalog;
                authorized.ViewIndex = catalog.ViewIndex;
                return Present(authorized);
            }
        }
        catch (OperationCanceledException) { return Failed("已取消角色查询"); }
        catch (LobbyException ex) { return Failed(ex.Message); }
        catch (OfficialCdnException ex) { return Failed(ex.Message); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return Failed("无法写入角色缓存，已保留上次数据"); }
        finally { Finish(current); }
    }

    internal GameResponse ChooseCharacter(string id)
    {
        lock (gate)
        {
            if (session is not { ViewIndex: { } index } active || operation is not null
                || !active.Catalog.Rosters.TryGetValue(index, out var roster) || !roster.Characters.Any(character => character.ContentId == id))
                return Failed("所选角色不可用");
            var catalog = active.Catalog with { Choice = new(index, id), ViewIndex = index };
            try { CharacterCache.Save(active.Directory, catalog); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Failed("无法保存当前角色"); }
            active.Catalog = catalog;
            return Present(active);
        }
    }

    internal (GameAuthorization Authorization, CommunityCharacter Character)? FindCommunityCharacter(string contentId)
    {
        lock (gate)
        {
            if (session is not { } active || active.Catalog.Choice?.Id != contentId)
                return null;
            var character = Preview(active, contentId)?.Character;
            var world = character is null ? null : KnownWorld(character.HomeWorld);
            return character is not null && world is not null
                ? (active.Authorization, new CommunityCharacter(character.Name, active.Area.Name, world)) : null;
        }
    }

    internal PreviewCharacter? FindPreviewCharacter(string contentId)
    {
        lock (gate)
            return session is { } active ? Preview(active, contentId) : null;
    }

    private static PreviewCharacter? Preview(GameSession active, string id)
    {
        if (!ulong.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            return null;
        foreach (var entry in active.Catalog.Rosters)
            if (entry.Value.Characters.FirstOrDefault(character => character.ContentId == id) is { } character)
                return new(character, active.Id, entry.Value.Revision, Path.Combine(active.Directory, id));
        return null;
    }

    internal GameProgress? GetProgress(string requestId)
    {
        lock (gate)
        {
            if (!Matches(requestId))
                return null;
            return new GameProgress(operation!.Status, (operation.Challenge as PhoneConfirmation)?.Number,
                operation.Challenge is ScanConfirmation ? "/_login/" + operation.Id.ToString("D") + ".png" : null);
        }
    }

    internal Stream? OpenQr(Uri uri)
    {
        lock (gate)
        {
            return operation?.Challenge is ScanConfirmation scan
                && uri.AbsolutePath == "/_login/" + operation.Id.ToString("D") + ".png"
                ? new MemoryStream(scan.Png, writable: false) : null;
        }
    }

    internal bool Cancel(string requestId)
    {
        lock (gate)
        {
            if (!Matches(requestId))
                return false;
            operation!.Cancellation.Cancel();
            operation.Challenge = null;
            return true;
        }
    }

    internal Task<bool> SignOutAsync()
    {
        Task<bool> forget;
        lock (gate)
        {
            if (operation is { } current)
            {
                current.SignedOut = true;
                current.Cancellation.Cancel();
                current.Challenge = null;
            }
            ClearSession();
            forget = signingOut = savedLogins.ForgetAsync();
        }
        SessionChanged?.Invoke();
        return forget;
    }

    private void ClearSession()
    {
        session?.Community.Dispose();
        session = null;
    }

    private bool Matches(string requestId) =>
        operation is not null && Guid.TryParseExact(requestId, "D", out var id) && operation.Id == id;

    private void Finish(LoginOperation current)
    {
        lock (gate)
        {
            operation = null;
            current.Cancellation.Dispose();
        }
    }

    private static Guid GetInstallation()
    {
        if (Guid.TryParseExact(Preferences.Default.Get(InstallationKey, ""), "N", out var existing))
            return existing;
        var created = Guid.NewGuid();
        Preferences.Default.Set(InstallationKey, created.ToString("N"));
        return created;
    }

    private static GameResponse Failed(string reason) => new([], null, [], reason);

    private static GameResponse Present(GameSession session)
    {
        var catalog = session.Catalog;
        var current = catalog.Choice is { } choice ? Preview(session, choice.Id) : null;
        var index = session.ViewIndex;
        var roster = index is { } selected ? catalog.Rosters.GetValueOrDefault(selected) : null;
        return new(catalog.Accounts.Select(account => new GameAccount(account.Index, account.Name)).ToArray(), index,
            roster?.Characters.Select(character => Present(Preview(session, character.ContentId)!, session.Area)).ToArray() ?? [],
            Current: current is null ? null : Present(current, session.Area), Cached: roster is not null,
            CurrentIndex: catalog.Choice?.Index);
    }

    private static GameCharacter Present(PreviewCharacter entry, GameArea area)
    {
        var character = entry.Character;
        return new(character.ContentId, character.Name, area.Name, World(character.HomeWorld), World(character.CurrentWorld),
            Race(character.Appearance?.Race), character.Appearance?.Gender switch { 0 => "男", 1 => "女", _ => null },
            character.ClassJobId, character.Level, character.DetailError,
            CharacterScene.DescribeGear(character.Equipment), CharacterData.GetTribeName(character.Appearance?.Tribe),
            entry.PortraitRoot, entry.Revision.ToString("D"), FormatBirthday(character.Birthday),
            CharacterData.GetGuardianName(character.GuardianDeityId));
    }

    private static string? FormatBirthday(CharacterBirthday? birthday) => birthday is { Month: >= 1 and <= 12, Day: >= 1 and <= 32 }
        ? $"{(birthday.Month % 2 == 1 ? '星' : '灵')}{"一二三四五六"[(birthday.Month - 1) / 2]}月{birthday.Day}日" : null;

    private static string? Race(int? value) => value is >= 1 and <= 8 ? RaceNames[value.Value] : null;

    private static string? KnownWorld(CharacterWorld world) => world.Name.Length > 0 ? world.Name : CharacterData.GetWorldName(world.Id);
    private static string World(CharacterWorld world) => KnownWorld(world) ?? (world.Id == 0 ? "未获取" : world.Id.ToString(CultureInfo.InvariantCulture));

    internal void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            operation?.Cancellation.Cancel();
            if (operation is not null)
                operation.Challenge = null;
            ClearSession();
        }
        directory.Dispose();
    }

    private sealed class LoginOperation(Guid id, string status)
    {
        internal Guid Id { get; } = id;
        internal string Status { get; set; } = status;
        internal CancellationTokenSource Cancellation { get; } = new();
        internal LoginChallenge? Challenge { get; set; }
        internal bool SignedOut { get; set; }
    }

    private sealed class GameSession(GameAuthorization authorization, GameArea area, string directory, CommunityAttempt community,
        CachedCatalog catalog)
    {
        internal Guid Id { get; } = Guid.NewGuid();
        internal GameAuthorization Authorization { get; } = authorization;
        internal GameArea Area { get; set; } = area;
        internal string Directory { get; } = directory;
        internal CommunityAttempt Community { get; } = community;
        internal CachedCatalog Catalog { get; set; } = catalog;
        internal int? ViewIndex { get; set; } = catalog.Choice?.Index ?? catalog.ViewIndex;
    }

    private sealed class CommunityAttempt : IDisposable
    {
        private readonly CancellationTokenSource cancellation = new();

        internal CommunityAttempt(SdoAuthenticator auth, GameAuthorization owner) =>
            Ticket = IssueAsync(auth, owner, cancellation.Token);

        internal Task<CommunityTicketResult> Ticket { get; }

        private static async Task<CommunityTicketResult> IssueAsync(
            SdoAuthenticator auth, GameAuthorization owner, CancellationToken token)
        {
            using (auth)
                return await owner.ExchangeForCommunityAsync(token);
        }

        public void Dispose()
        {
            cancellation.Cancel();
            cancellation.Dispose();
        }
    }
}

internal sealed record AreaResponse(
    [property: JsonPropertyName("areas")] AreaOption[] Areas,
    [property: JsonPropertyName("error")] string? Error = null);

internal sealed record AreaOption(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("name")] string Name);

internal sealed record GameProgress(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("number")] string? Number,
    [property: JsonPropertyName("qr")] string? Qr);

internal sealed record GameResponse(
    [property: JsonPropertyName("accounts")] GameAccount[] Accounts,
    [property: JsonPropertyName("selectedIndex")] int? SelectedIndex,
    [property: JsonPropertyName("characters")] GameCharacter[] Characters,
    [property: JsonPropertyName("error")] string? Error = null,
    [property: JsonPropertyName("notice")] string? Notice = null,
    [property: JsonPropertyName("current")] GameCharacter? Current = null,
    [property: JsonPropertyName("cached")] bool Cached = false,
    [property: JsonPropertyName("currentIndex")] int? CurrentIndex = null);

internal sealed record GameAccount(
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("name")] string Name);

internal sealed record GameCharacter(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("area")] string Area,
    [property: JsonPropertyName("homeWorld")] string HomeWorld,
    [property: JsonPropertyName("currentWorld")] string CurrentWorld,
    [property: JsonPropertyName("race")] string? Race,
    [property: JsonPropertyName("gender")] string? Gender,
    [property: JsonPropertyName("classJobId")] int? ClassJobId,
    [property: JsonPropertyName("level")] int? Level,
    [property: JsonPropertyName("detailError")] string? DetailError,
    [property: JsonPropertyName("gear")] SceneGear[] Gear,
    [property: JsonPropertyName("tribe")] string? Tribe,
    [property: JsonPropertyName("portraitRoot")] string PortraitRoot,
    [property: JsonPropertyName("revision")] string Revision,
    [property: JsonPropertyName("birthday")] string? Birthday,
    [property: JsonPropertyName("deity")] string? Deity);
