using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Lodestone.Core.Assets;
using Lodestone.Core.Auth;
using Lodestone.Core.Cdn;
using Lodestone.Core.Community;
using Lodestone.Core.Lobby;

namespace Lodestone.App;

internal sealed class GameCommands(GameVersionService versions, Func<int> lobbyVersion, ExdDataStore exd)
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
    internal CommunityAccess? CurrentCommunity
    {
        get { lock (gate) return session?.Community; }
    }

    internal async Task<AreaResponse> GetAreasAsync(bool refresh)
    {
        try
        {
            var (fetched, usedFallback) = await directory.GetWithStatusAsync(refresh: refresh);
            lock (gate)
                areas = fetched.ToDictionary(area => area.Id);
            return new AreaResponse(fetched.Select(area => new AreaOption(area.Id, area.Name)).ToArray(),
                Notice: usedFallback ? "无法更新官方大区列表，正在使用上次保存的列表。" : null);
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
        if (mode == "saved")
            return await RestoreProfileAsync(id, areaId);
        LoginOperation current;
        GameArea area;
        lock (gate)
        {
            if (disposed || operation is not null || signingOut is { IsCompleted: false })
                return Failed("已有操作正在处理");
            if (!areas.TryGetValue(areaId, out var selected))
                return Failed("请选择有效大区");
            area = selected;
            current = operation = new LoginOperation(id, "正在连接盛趣");
        }

        SdoAuthenticator? auth = null;
        try
        {
            var token = current.Cancellation.Token;
            var installation = GetInstallation();
            auth = new SdoAuthenticator(installation);
            void PublishChallenge(LoginChallenge challenge)
            {
                lock (gate)
                {
                    current.Challenge = challenge;
                    current.Status = challenge is PhoneConfirmation ? "请在叨鱼确认登录" : "请用叨鱼扫描二维码";
                }
            }
            var authorization = mode == "phone"
                ? await auth.ConfirmByPhoneAsync(account, PublishChallenge, token, remember)
                : await auth.ConfirmByScanAsync(PublishChallenge, token, remember);
            var directoryPath = cache.AccountPath(authorization.AccountId, areaId);
            var catalog = CharacterCache.Read(directoryPath);
            var firstRead = catalog is null;
            if (catalog is null)
            {
                var result = await ReadLobbyAsync(current, area, authorization, null);
                catalog = new CachedCatalog([], [], null, null).WithRoster(result);
            }
            token.ThrowIfCancellationRequested();
            if (firstRead)
                CharacterCache.Save(directoryPath, catalog);
            var community = new CommunityAccess(authorization.AccountId, areaId, false, authorization.CommunityCookies, [], null);
            var next = new GameSession(areaId, area.Name, directoryPath, community, catalog) { Authorization = authorization };
            var response = Present(next);
            Task<bool> persistence;
            lock (gate)
            {
                token.ThrowIfCancellationRequested();
                current.Status = "正在完成登录";
                // Sign-out must observe the pending write before it can clear saved credentials.
                persistence = current.Persistence = authorization.SavedLogin is { } saved
                    ? savedLogins.SaveAsync(installation, areaId, area.Name, saved, CancellationToken.None)
                    : savedLogins.ForgetAsync();
            }
            var stored = await persistence;
            var credentialStored = authorization.SavedLogin is not null && stored;
            var notice = authorization.SavedLogin is null && !stored ? "无法清除本机此前保存的登录状态"
                : remember && !credentialStored ? "已登录，本次未能保存登录状态" : null;
            response = response with { Notice = notice };
            lock (gate)
            {
                if (current.SignedOut)
                    return Failed("已退出登录");
                if (disposed)
                    return response;
                var attempt = new CommunityAttempt(auth, authorization);
                next.Community = community with { Remember = credentialStored, Ticket = attempt.Ticket };
                next.CommunityAttempt = attempt;
                ClearSession();
                session = next;
                auth = null;
            }
            SessionChanged?.Invoke();
            return response;
        }
        catch (OperationCanceledException)
        {
            return Failed("已取消登录");
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

    private async Task<GameResponse> RestoreProfileAsync(Guid id, int areaId)
    {
        LoginOperation current;
        lock (gate)
        {
            if (disposed || operation is not null || signingOut is { IsCompleted: false })
                return Failed("已有操作正在处理");
            current = operation = new LoginOperation(id, "正在读取已保存的角色信息");
        }

        try
        {
            var token = current.Cancellation.Token;
            var installation = GetInstallation();
            var saved = await savedLogins.ReadAsync(installation);
            if (saved is null || saved.AreaId != areaId)
                return Failed("此大区没有已保存的登录状态，请手动登录");
            var directoryPath = cache.AccountPath(saved.Login.AccountId, areaId);
            if (CharacterCache.Read(directoryPath) is not { } catalog)
                return Failed("本机没有已保存的角色信息，请手动登录并选择角色");
            var areaName = saved.AreaName;
            if (string.IsNullOrWhiteSpace(areaName))
                areaName = (await directory.ReadCachedAsync(token))?.FirstOrDefault(entry => entry.Id == areaId)?.Name ?? $"大区 {areaId}";
            var cookies = await savedLogins.ReadCommunityAsync(installation, saved.Login.AccountId);
            var community = new CommunityAccess(saved.Login.AccountId, areaId, true, [], cookies, null);
            GameResponse response;
            lock (gate)
            {
                token.ThrowIfCancellationRequested();
                var next = new GameSession(areaId, areaName, directoryPath, community, catalog);
                response = Present(next);
                ClearSession();
                session = next;
            }
            SessionChanged?.Invoke();
            return response;
        }
        catch (OperationCanceledException) { return Failed("已取消打开角色信息"); }
        finally { Finish(current); }
    }

    private Task<bool> SaveLoginAsync(LoginOperation current, Guid installation, int areaId, string areaName, SavedGameLogin login,
        CancellationToken token)
    {
        lock (gate)
            return current.SignedOut ? Task.FromResult(false) : savedLogins.SaveAsync(installation, areaId, areaName, login, token);
    }

    internal Task SaveCommunityAsync(CommunityAccess owner, SavedCommunityCookie[] cookies)
    {
        lock (gate)
            return owner.Remember && ReferenceEquals(session?.Community, owner)
                ? savedLogins.SaveCommunityAsync(GetInstallation(), owner.AreaId, owner.AccountId, cookies) : Task.CompletedTask;
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
        GameArea? knownArea;
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
            knownArea = areas.GetValueOrDefault(authorized.AreaId);
            current = operation = new LoginOperation(id, "正在读取角色列表");
        }
        try
        {
            var entries = await directory.GetAsync(current.Cancellation.Token, refresh: true);
            var area = entries.FirstOrDefault(entry => entry.Id == authorized.AreaId) ?? knownArea;
            if (area is null)
                return Failed("当前大区不在官方大区列表中");
            var authorization = authorized.Authorization ?? await AuthorizeLobbyAsync(current, authorized, area);
            var result = await ReadLobbyAsync(current, area, authorization, index == -1 ? null : index);
            var catalog = authorized.Catalog.WithRoster(result);
            lock (gate)
            {
                current.Cancellation.Token.ThrowIfCancellationRequested();
                CharacterCache.Save(authorized.Directory, catalog);
                areas = entries.ToDictionary(entry => entry.Id);
                authorized.AreaName = area.Name;
                authorized.Authorization = authorization;
                authorized.Catalog = catalog;
                authorized.ViewIndex = catalog.ViewIndex;
                return Present(authorized);
            }
        }
        catch (OperationCanceledException) { return Failed("已取消角色查询"); }
        catch (LobbyException ex) { return Failed(ex.Message); }
        catch (OfficialCdnException ex) { return Failed(ex.Message); }
        catch (SavedGameLoginRejectedException ex) { return Failed(ex.Message); }
        catch (SdoAuthException ex) { return Failed(ex.Message); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return Failed("无法写入角色缓存，已保留上次数据"); }
        finally { Finish(current); }
    }

    private async Task<GameAuthorization> AuthorizeLobbyAsync(LoginOperation current, GameSession session, GameArea area)
    {
        var installation = GetInstallation();
        var saved = await savedLogins.ReadAsync(installation);
        if (saved is null || saved.AreaId != area.Id || saved.Login.AccountId != session.Community.AccountId)
            throw new SavedGameLoginRejectedException();
        using var auth = new SdoAuthenticator(installation);
        return await auth.RestoreAsync(saved.Login, async renewed =>
        {
            await SaveLoginAsync(current, installation, area.Id, area.Name, renewed, CancellationToken.None);
        }, current.Cancellation.Token);
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

    internal (CommunityAccess Owner, CommunityCharacter Character)? FindCommunityCharacter(string contentId)
    {
        lock (gate)
        {
            if (session is not { } active || active.Catalog.Choice?.Id != contentId)
                return null;
            var entry = Preview(active, contentId, exd.Current.Character);
            var world = entry is null ? null : KnownWorld(entry.Character.HomeWorld, entry.Data);
            return entry is not null && world is not null
                ? (active.Community, new CommunityCharacter(entry.Character.Name, active.AreaName, world)) : null;
        }
    }

    internal PreviewCharacter? FindPreviewCharacter(string contentId)
    {
        lock (gate)
            return session is { } active ? Preview(active, contentId, exd.Current.Character) : null;
    }

    internal PreviewCharacter? FindSelectedPreviewCharacter(string contentId)
    {
        lock (gate)
            return session is { } active && active.Catalog.Choice?.Id == contentId ? Preview(active, contentId, exd.Current.Character) : null;
    }

    internal GameResponse? GetCurrent()
    {
        lock (gate)
            return session is { } active ? Present(active) : null;
    }

    private static PreviewCharacter? Preview(GameSession active, string id, CharacterData data)
    {
        if (!ulong.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            return null;
        foreach (var entry in active.Catalog.Rosters)
            if (entry.Value.Characters.FirstOrDefault(character => character.ContentId == id) is { } character)
                return new(character, active.Id, PreviewRevision(character.Equipment, entry.Value.Revision, data),
                    Path.Combine(active.Directory, id), data);
        return null;
    }

    private static Guid PreviewRevision(IReadOnlyList<CharacterEquipment> equipment, Guid revision, CharacterData data)
    {
        var bundled = ExdCatalog.Bundled.Character;
        if (ReferenceEquals(data, bundled))
            return revision;

        // Scene and portrait caches depend on dye colors. Metadata-only updates can reuse them.
        var ids = equipment.SelectMany(item => new[] { item.Stain1, item.Stain2 }).Where(id => id > 0).Distinct().Order();
        var changed = new StringBuilder();
        foreach (var id in ids)
            if (data.StainColor(id) != bundled.StainColor(id))
                changed.Append(FormattableString.Invariant($"/{id}:{data.StainColor(id)}"));

        return changed.Length == 0 ? revision
            : new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(revision.ToString("D") + changed)).AsSpan(0, 16));
    }

    internal GameProgress? GetProgress(string requestId)
    {
        lock (gate)
        {
            if (!Matches(requestId))
                return null;
            return new GameProgress(operation!.Status, (operation.Challenge as PhoneConfirmation)?.Number,
                operation.Challenge is ScanConfirmation ? "/_login/" + operation.Id.ToString("D") + ".png" : null,
                CanCancel: operation.Persistence is null);
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
            if (!Matches(requestId) || operation!.Persistence is not null)
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
            forget = signingOut = ForgetAfterLoginAsync(operation?.Persistence);
        }
        SessionChanged?.Invoke();
        return forget;
    }

    private async Task<bool> ForgetAfterLoginAsync(Task<bool>? persistence)
    {
        bool cleared;
        try
        {
            if (persistence is not null)
                await persistence;
        }
        finally { cleared = await savedLogins.ForgetAsync(); }
        return cleared;
    }

    private void ClearSession()
    {
        session?.CommunityAttempt?.Dispose();
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

    private GameResponse Present(GameSession session)
    {
        var catalog = session.Catalog;
        var data = exd.Current.Character;
        var current = catalog.Choice is { } choice ? Preview(session, choice.Id, data) : null;
        var index = session.ViewIndex;
        var roster = index is { } selected ? catalog.Rosters.GetValueOrDefault(selected) : null;
        return new(catalog.Accounts.Select(account => new GameAccount(account.Index, account.Name)).ToArray(), index,
            roster?.Characters.Select(character => Present(Preview(session, character.ContentId, data)!, session.AreaName)).ToArray() ?? [],
            Current: current is null ? null : Present(current, session.AreaName), Cached: roster is not null,
            CurrentIndex: catalog.Choice?.Index);
    }

    private static GameCharacter Present(PreviewCharacter entry, string areaName)
    {
        var character = entry.Character;
        return new(character.ContentId, character.Name, areaName, World(character.HomeWorld, entry.Data), World(character.CurrentWorld, entry.Data),
            Race(character.Appearance?.Race), character.Appearance?.Gender switch { 0 => "男", 1 => "女", _ => null },
            character.ClassJobId, character.Level, character.DetailError,
            CharacterScene.DescribeGear(character.Equipment, null, entry.Data), CharacterData.GetTribeName(character.Appearance?.Tribe),
            entry.PortraitRoot, entry.Revision.ToString("D"), FormatBirthday(character.Birthday),
            entry.Data.GuardianName(character.GuardianDeityId), character.CurrentWorld.Id);
    }

    private static string? FormatBirthday(CharacterBirthday? birthday) => birthday is { Month: >= 1 and <= 12, Day: >= 1 and <= 32 }
        ? $"{(birthday.Month % 2 == 1 ? '星' : '灵')}{"一二三四五六"[(birthday.Month - 1) / 2]}月{birthday.Day}日" : null;

    private static string? Race(int? value) => value is >= 1 and <= 8 ? RaceNames[value.Value] : null;

    private static string? KnownWorld(CharacterWorld world, CharacterData data) => world.Name.Length > 0 ? world.Name : data.WorldName(world.Id);
    private static string World(CharacterWorld world, CharacterData data) => KnownWorld(world, data)
        ?? (world.Id == 0 ? "未获取" : world.Id.ToString(CultureInfo.InvariantCulture));

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
        internal Task<bool>? Persistence { get; set; }
    }

    private sealed class GameSession(int areaId, string areaName, string directory, CommunityAccess community,
        CachedCatalog catalog)
    {
        internal Guid Id { get; } = Guid.NewGuid();
        internal GameAuthorization? Authorization { get; set; }
        internal int AreaId { get; } = areaId;
        internal string AreaName { get; set; } = areaName;
        internal string Directory { get; } = directory;
        internal CommunityAccess Community { get; set; } = community;
        internal CommunityAttempt? CommunityAttempt { get; set; }
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

internal sealed record CommunityAccess(string AccountId, int AreaId, bool Remember, IReadOnlyList<CommunityLoginCookie> FederatedCookies,
    IReadOnlyList<SavedCommunityCookie> SavedCookies, Task<CommunityTicketResult>? Ticket);

internal sealed record AreaResponse(
    [property: JsonPropertyName("areas")] AreaOption[] Areas,
    [property: JsonPropertyName("error")] string? Error = null,
    [property: JsonPropertyName("notice")] string? Notice = null);

internal sealed record AreaOption(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("name")] string Name);

internal sealed record GameProgress(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("number")] string? Number,
    [property: JsonPropertyName("qr")] string? Qr,
    [property: JsonPropertyName("canCancel")] bool CanCancel);

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
    [property: JsonPropertyName("deity")] string? Deity,
    [property: JsonPropertyName("currentWorldId")] int CurrentWorldId);
