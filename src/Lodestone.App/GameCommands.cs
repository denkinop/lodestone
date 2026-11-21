using System.Globalization;
using System.Text.Json.Serialization;
using Lodestone.Core.Auth;
using Lodestone.Core.Cdn;
using Lodestone.Core.Community;
using Lodestone.Core.Lobby;

namespace Lodestone.App;

internal sealed class GameCommands(GameVersionService versions, Func<int> lobbyVersion)
{
    private const string InstallationKey = "sdo-installation";
    private static readonly string[] RaceNames = ["", "人族", "精灵族", "拉拉菲尔族", "猫魅族", "鲁加族", "敖龙族", "硌狮族", "维埃拉族"];
    private readonly GameAreaDirectory directory = new();
    private readonly GameLobbyClient lobby = new();
    private readonly object gate = new();
    private Dictionary<int, GameArea> areas = [];
    private LoginOperation? operation;
    private GameSession? session;
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

    internal async Task<GameResponse> LoginAsync(string requestId, string mode, string account, int areaId)
    {
        if (!Guid.TryParseExact(requestId, "D", out var id) || mode is not ("phone" or "scan"))
            return Failed("登录请求无效");
        LoginOperation current;
        GameArea area;
        lock (gate)
        {
            if (disposed || operation is not null)
                return Failed("已有操作正在处理");
            if (!areas.TryGetValue(areaId, out var selected))
                return Failed("请选择有效大区");
            area = selected;
            ClearSession();
            current = operation = new LoginOperation(id, "正在连接盛趣");
        }
        SessionChanged?.Invoke();

        SdoAuthenticator? auth = null;
        try
        {
            var token = current.Cancellation.Token;
            auth = new SdoAuthenticator(GetInstallation());
            void PublishChallenge(LoginChallenge challenge)
            {
                lock (gate)
                {
                    current.Challenge = challenge;
                    current.Status = challenge is PhoneConfirmation ? "请在叨鱼确认登录" : "请用叨鱼扫描二维码";
                }
            }
            var authorization = mode == "phone"
                ? await auth.ConfirmByPhoneAsync(account, PublishChallenge, token)
                : await auth.ConfirmByScanAsync(PublishChallenge, token);
            lock (gate)
            {
                current.Challenge = null;
                current.Status = "正在检查游戏版本并连接 Lobby";
            }
            var version = await versions.RefreshAsync(cancellationToken: token);
            var result = await lobby.ReadAsync(area, authorization, version.VersionInfo, (ushort)lobbyVersion(), null, token);
            lock (gate)
            {
                token.ThrowIfCancellationRequested();
                session = new GameSession(authorization, area, version.VersionInfo, new CommunityAttempt(auth, authorization));
                auth = null;
            }
            SessionChanged?.Invoke();
            return Present(result, area);
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

    internal async Task<GameResponse> SelectAsync(string requestId, int index)
    {
        if (!Guid.TryParseExact(requestId, "D", out var id) || index is < 0 or > 7)
            return Failed("服务账号选择无效");
        LoginOperation current;
        GameSession authorized;
        lock (gate)
        {
            if (disposed || operation is not null || session is null)
                return Failed("游戏会话不可用，请重新登录");
            authorized = session;
            current = operation = new LoginOperation(id, "正在读取角色列表");
        }
        try
        {
            var result = await lobby.ReadAsync(authorized.Area, authorized.Authorization, authorized.VersionInfo,
                (ushort)lobbyVersion(), index, current.Cancellation.Token);
            current.Cancellation.Token.ThrowIfCancellationRequested();
            return Present(result, authorized.Area);
        }
        catch (OperationCanceledException) { return Failed("已取消角色查询"); }
        catch (LobbyException ex) { return Failed(ex.Message); }
        finally { Finish(current); }
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

    internal bool SignOut()
    {
        lock (gate)
        {
            operation?.Cancellation.Cancel();
            if (operation is not null)
                operation.Challenge = null;
            ClearSession();
        }
        SessionChanged?.Invoke();
        return true;
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

    private static GameResponse Present(LobbyRoster roster, GameArea area) => new(
        roster.Accounts.Select(account => new GameAccount(account.Index, account.Name)).ToArray(),
        roster.SelectedIndex,
        roster.Characters.Select(character => new GameCharacter(
            character.ContentId, character.Name, area.Name, World(character.HomeWorld), World(character.CurrentWorld),
            Race(character.Appearance?.Race),
            character.Appearance?.Gender switch { 0 => "男", 1 => "女", _ => null },
            character.DetailError,
            character.Equipment.Where(item => item.Model != 0).Select(item => new GameEquipment(
                item.Slot, item.Model, item.ModelType, item.Variant, item.Stain1, item.Stain2)).ToArray())).ToArray());

    private static string? Race(int? value) => value is >= 1 and <= 8 ? RaceNames[value.Value] : null;

    private static string World(CharacterWorld world) => world.Name.Length > 0
        ? world.Name : world.Id == 0 ? "未获取" : world.Id.ToString(CultureInfo.InvariantCulture);

    internal void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            SignOut();
        }
        directory.Dispose();
    }

    private sealed class LoginOperation(Guid id, string status)
    {
        internal Guid Id { get; } = id;
        internal string Status { get; set; } = status;
        internal CancellationTokenSource Cancellation { get; } = new();
        internal LoginChallenge? Challenge { get; set; }
    }

    private sealed record GameSession(GameAuthorization Authorization, GameArea Area, string VersionInfo, CommunityAttempt Community);

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
    [property: JsonPropertyName("error")] string? Error = null);

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
    [property: JsonPropertyName("detailError")] string? DetailError,
    [property: JsonPropertyName("gear")] GameEquipment[] Gear);

internal sealed record GameEquipment(
    [property: JsonPropertyName("slot")] string Slot,
    [property: JsonPropertyName("model")] ushort Model,
    [property: JsonPropertyName("modelType")] ushort ModelType,
    [property: JsonPropertyName("variant")] ushort Variant,
    [property: JsonPropertyName("stain1")] byte Stain1,
    [property: JsonPropertyName("stain2")] byte Stain2);
