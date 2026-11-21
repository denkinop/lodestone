using System.Globalization;
using System.Text.Json;
using System.Web;

namespace Lodestone.Core.Community;

public sealed record CommunityBinding(string Character, string World);
public sealed record CommunityCharacter(string Character, string Area, string World);
public sealed record CommunityJobProfile(string Character, string Area, string World, IReadOnlyDictionary<string, int> Levels);
public sealed record CommunitySnapshot(string State, string Detail, IReadOnlyList<CommunityBinding> Bindings)
{
    public CommunityJobProfile? JobProfile { get; init; }
    public string JobsStatus { get; init; } = "idle";
}

public sealed class RisingStonesSession(string accountId, bool reusesSession = false)
{
    public const string ServiceUrl = "https://apiff14risingstones.web.sdo.com/api/home/GHome/login";
    public const string StatusUrl = "https://apiff14risingstones.web.sdo.com/api/home/GHome/isLogin";
    public const string BindingsUrl = "https://apiff14risingstones.web.sdo.com/api/home/groupAndRole/getCharacterBindInfo?platform=2";
    public const string ProfileUrl = "https://apiff14risingstones.web.sdo.com/api/home/userInfo/getUserInfo";
    public const string AreasUrl = "https://apiff14risingstones.web.sdo.com/api/home/groupAndRole/getAreaAndGroupList";
    public const string CharactersUrl = "https://apiff14risingstones.web.sdo.com/api/home/groupAndRole/getFF14Characters";
    public const string SelectRoleUrl = "https://apiff14risingstones.web.sdo.com/api/home/groupAndRole/bindCharacterInfo";
    public const int MaxProfileBytes = 512 * 1024;
    private readonly object gate = new();
    private CommunitySnapshot snapshot = reusesSession
        ? new("starting", "正在通过盛趣会话验证石之家", [])
        : new("confirmation", "石之家需要单独确认，可继续查看游戏角色", []);
    private bool verified;
    private CommunityBinding[]? bindingSummary;
    private CommunityBinding? currentRole;
    private CommunityCharacter? selectedRole;

    public static Uri Portal { get; } = new("https://ff14risingstones.web.sdo.com/pc/index.html#/post");
    public static Uri Entry { get; } = new(ServiceUrl + "?redirectUrl=" + Uri.EscapeDataString(Portal.AbsoluteUri));

    public CommunitySnapshot Snapshot { get { lock (gate) return snapshot; } }
    public bool CanSelectRole { get { lock (gate) return verified; } }

    public bool HasSelectedRole(CommunityCharacter role)
    {
        lock (gate)
            return verified && (selectedRole == role || snapshot.JobProfile is { } profile
                && (profile.Character, profile.Area, profile.World) == (role.Character, role.Area, role.World));
    }

    public static int? FindAreaId(byte[] body, string area)
    {
        if (!TryReadResponse(body, out var data, out var code, MaxProfileBytes) || !IsSuccess(code)
            || data.ValueKind != JsonValueKind.Array)
            return null;
        int? match = null;
        foreach (var row in data.EnumerateArray())
        {
            if (Text(row, "AreaName") != area)
                continue;
            if (match is not null || !int.TryParse(Text(row, "AreaID"), NumberStyles.None, CultureInfo.InvariantCulture, out var id)
                || id <= 0)
                return null;
            match = id;
        }
        return match;
    }

    public static string? FindCharacterId(byte[] body, CommunityCharacter character)
    {
        if (!TryReadResponse(body, out var data, out var code, MaxProfileBytes) || !IsSuccess(code)
            || data.ValueKind != JsonValueKind.Array)
            return null;
        string? match = null;
        foreach (var row in data.EnumerateArray())
        {
            if (Text(row, "character_name") != character.Character || Text(row, "group_name") != character.World)
                continue;
            var id = Text(row, "character_id");
            if (match is not null || !ulong.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value == 0)
                return null;
            match = id;
        }
        return match;
    }

    public bool ObserveRoleChange(byte[] body, CommunityCharacter role)
    {
        lock (gate)
        {
            if (!verified || !TryReadResponse(body, out _, out var code) || !IsSuccess(code))
                return false;
            selectedRole = role;
            currentRole = null;
            bindingSummary = [new CommunityBinding(role.Character, role.World)];
            snapshot = snapshot with { JobProfile = null, JobsStatus = "idle" };
            PresentRoles();
            return true;
        }
    }

    public static bool IsAllowedPage(Uri uri) => IsHttps(uri) && (uri.Host switch
    {
        "apiff14risingstones.web.sdo.com" => uri.AbsolutePath == "/api/home/GHome/login",
        "ff14risingstones.web.sdo.com" => uri.AbsolutePath.StartsWith("/pc/", StringComparison.Ordinal),
        "login.u.sdo.com" => uri.AbsolutePath.StartsWith("/sdo/", StringComparison.Ordinal),
        _ => false
    });

    public static bool IsPortal(Uri uri) => IsHttps(uri) && uri.Host == "ff14risingstones.web.sdo.com"
        && uri.AbsolutePath.StartsWith("/pc/", StringComparison.Ordinal);

    public static bool IsPassportPage(Uri uri) => IsHttps(uri) && uri.Host == "login.u.sdo.com"
        && uri.AbsolutePath.StartsWith("/sdo/", StringComparison.Ordinal);

    public static bool IsLoginStatus(Uri uri) => IsApi(uri, "/api/home/GHome/isLogin");
    public static bool IsBindingStatus(Uri uri)
    {
        if (!IsApi(uri, "/api/home/groupAndRole/getCharacterBindInfo"))
            return false;
        var query = HttpUtility.ParseQueryString(uri.Query);
        return query.GetValues("platform") is ["1" or "2"] && query.AllKeys.All(key => key is "platform" or "tempsuid");
    }

    public static bool IsCurrentRole(Uri uri) =>
        IsApi(uri, "/api/home/userInfo/getUserInfo")
        && HttpUtility.ParseQueryString(uri.Query).AllKeys.All(key => key == "tempsuid");

    private static bool IsRoleList(Uri uri)
    {
        if (!IsApi(uri, "/api/home/groupAndRole/getFF14Characters"))
            return false;
        var query = HttpUtility.ParseQueryString(uri.Query);
        return query.GetValues("AreaID") is [var area]
            && int.TryParse(area, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0
            && query.AllKeys.All(key => key is "AreaID" or "tempsuid");
    }

    private static bool IsReadEndpoint(Uri uri) =>
        IsLoginStatus(uri) || IsBindingStatus(uri) || IsCurrentRole(uri) || IsRoleList(uri)
        || IsApi(uri, "/api/home/groupAndRole/getAreaAndGroupList");

    public bool AllowsApiRequest(Uri uri, string method, bool allowSignIn = true) => method switch
    {
        "GET" => IsReadEndpoint(uri) || (allowSignIn && IsAllowedPage(uri)),
        "OPTIONS" => IsReadEndpoint(uri),
        _ => false
    };

    public void ObserveLogin(byte[] body, bool ticketOnlyProfile = false)
    {
        lock (gate)
        {
            if (!TryReadResponse(body, out var data, out var code))
            {
                Unverified(verified ? "failed" : "confirmation", "石之家尚未返回可验证的登录状态");
                return;
            }
            var state = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("isLogin", out var login)
                ? login.ValueKind : data.ValueKind;
            var requiresRole = IsRoleRequired(code);
            var signedIn = code switch
            {
                null => state == JsonValueKind.True,
                10000 or 10002 => state is JsonValueKind.True or JsonValueKind.Object,
                _ => requiresRole && state != JsonValueKind.False
            };
            if (!signedIn)
            {
                Unverified(verified ? "expired" : "confirmation", verified ? "石之家会话已过期" : "请在石之家官方窗口登录");
                return;
            }
            if (!MatchesAccount(data, ticketOnlyProfile))
            {
                Unverified("identity", "尚未核对石之家与游戏账号的一致性");
                return;
            }
            if (!verified)
                snapshot = new("connected", "石之家会话已验证，正在检查社区角色", []);
            verified = true;
            if (requiresRole && selectedRole is null)
                RequireCommunityRole();
        }
    }

    public void ObserveBindings(byte[] body)
    {
        lock (gate)
        {
            if (!verified || selectedRole is not null || !TryReadResponse(body, out var data, out var code))
                return;
            if (IsRoleRequired(code))
            {
                RequireCommunityRole();
                return;
            }
            if (code is not null && !IsSuccess(code))
                return;
            if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("characters", out var characters))
                data = characters;
            if (data.ValueKind == JsonValueKind.Object)
            {
                var character = Text(data, "character_name");
                var world = Text(data, "group_name");
                if (character is not null && world is null)
                    return;
                bindingSummary = character is not null && world is not null ? [new CommunityBinding(character, world)] : [];
                PresentRoles();
                return;
            }
            if (data.ValueKind != JsonValueKind.Array)
                return;

            var bindings = new List<CommunityBinding>();
            foreach (var entry in data.EnumerateArray().Take(32))
            {
                var character = Text(entry, "characterName");
                var world = Text(entry, "serverName");
                if (character is null || world is null)
                    return;
                bindings.Add(new CommunityBinding(character, world));
            }
            bindingSummary = bindings.ToArray();
            PresentRoles();
        }
    }

    public void ObserveCurrentRole(byte[] body)
    {
        lock (gate)
        {
            if (!verified)
                return;
            if (!TryReadResponse(body, out var data, out var code, MaxProfileBytes))
            {
                JobReadFailed();
                return;
            }
            if (IsRoleRequired(code))
            {
                RequireCommunityRole();
                return;
            }
            if (code == 10403)
            {
                Unverified("expired", "石之家会话已过期");
                return;
            }
            var character = Text(data, "character_name");
            var world = Text(data, "group_name");
            if (!IsSuccess(code) || character is null || world is null)
            {
                JobReadFailed();
                return;
            }
            var role = new CommunityBinding(character, world);
            var area = Text(data, "area_name");
            if (selectedRole is not null && selectedRole != new CommunityCharacter(character, area ?? "", world)
                || bindingSummary is { Length: > 0 } && !bindingSummary.Contains(role))
            {
                JobReadFailed();
                return;
            }
            currentRole = role;
            PresentRoles();
            var levels = ReadCareerLevels(data);
            snapshot = snapshot with
            {
                JobProfile = area is not null && levels.Count > 0 ? new(character, area, world, levels) : null,
                JobsStatus = area is not null && levels.Count > 0 ? "ready" : "unavailable"
            };
        }
    }

    public bool BeginJobRead()
    {
        lock (gate)
        {
            if (!verified || snapshot.State != "connected" || snapshot.JobsStatus == "loading")
                return false;
            snapshot = snapshot with { JobProfile = null, JobsStatus = "loading" };
            return true;
        }
    }

    public void JobReadFailed()
    {
        lock (gate)
        {
            if (snapshot.JobsStatus == "loading")
                snapshot = snapshot with { JobProfile = null, JobsStatus = "unavailable" };
        }
    }

    private static Dictionary<string, int> ReadCareerLevels(JsonElement data)
    {
        var levels = new Dictionary<string, int>(StringComparer.Ordinal);
        if (data.TryGetProperty("careerLevel", out var rows) && rows.ValueKind == JsonValueKind.Array)
        {
            foreach (var row in rows.EnumerateArray().Take(64))
            {
                var career = Text(row, "career");
                if (career is not null && byte.TryParse(Text(row, "character_level"), NumberStyles.None,
                    CultureInfo.InvariantCulture, out var level))
                    levels.TryAdd(career, level);
            }
        }
        return levels;
    }

    private void RequireCommunityRole()
    {
        bindingSummary = [];
        currentRole = null;
        selectedRole = null;
        PresentRoles();
    }

    private void PresentRoles()
    {
        if (bindingSummary is { Length: > 0 })
        {
            if (currentRole is not null && !bindingSummary.Contains(currentRole))
            {
                currentRole = null;
                snapshot = snapshot with { JobProfile = null, JobsStatus = "idle" };
            }
            snapshot = snapshot with { State = "connected", Detail = "石之家登录已验证", Bindings = bindingSummary };
        }
        else if (currentRole is not null)
            snapshot = snapshot with { State = "connected", Detail = "石之家登录已验证", Bindings = [currentRole] };
        else if (bindingSummary is not null)
            snapshot = new("unbound", "石之家已登录，请从客户端角色列表选择要同步的角色", []);
    }

    private bool MatchesAccount(JsonElement data, bool ticketOnlyProfile)
    {
        if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("sndaId", out _))
            return string.Equals(Text(data, "sndaId"), accountId, StringComparison.Ordinal);
        return ticketOnlyProfile;
    }

    public void RequireConfirmation(int? code = null)
    {
        lock (gate)
        {
            if (!verified)
                Unverified("confirmation", code is { } value
                    ? $"石之家需要额外确认（盛趣返回 {value}）" : "石之家需要额外确认，可继续查看游戏角色");
        }
    }

    public void Closed()
    {
        lock (gate)
            Unverified(verified ? "expired" : "confirmation", verified ? "石之家登录窗口已关闭" : "石之家仍需确认");
    }

    public void Failed()
    {
        lock (gate)
            Unverified("failed", "无法打开石之家官方确认窗口");
    }

    private void Unverified(string state, string detail)
    {
        verified = false;
        bindingSummary = null;
        currentRole = null;
        selectedRole = null;
        snapshot = new(state, detail, []);
    }

    private static bool IsHttps(Uri uri) =>
        uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && uri.UserInfo.Length == 0;

    private static bool IsApi(Uri uri, string path) =>
        IsHttps(uri) && uri.Host == "apiff14risingstones.web.sdo.com" && uri.AbsolutePath == path;

    private static bool IsSuccess(int? code) => code is 10000 or 10002;

    private static bool IsRoleRequired(int? code) => code is 10103 or 10104;

    private static string? Text(JsonElement data, string name)
    {
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty(name, out var value))
            return null;
        var text = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };
        return text is { Length: > 0 and <= 128 } && !text.Any(char.IsControl) ? text : null;
    }

    private static bool TryReadResponse(byte[] body, out JsonElement data, out int? code, int maxBytes = 64 * 1024)
    {
        data = default;
        code = null;
        if (body.Length > maxBytes)
            return false;
        try
        {
            using var json = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 12 });
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return false;
            if (root.TryGetProperty("code", out var responseCode))
            {
                if (!int.TryParse(responseCode.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var actual))
                    return false;
                code = actual;
            }
            if (root.TryGetProperty("data", out var value))
                data = value.Clone();
            return true;
        }
        catch (JsonException) { return false; }
    }
}
