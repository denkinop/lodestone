using System.Text.Json;

namespace Lodestone.Core.Community;

public sealed record CommunityBinding(string Character, string World);
public sealed record CommunitySnapshot(string State, string Detail, IReadOnlyList<CommunityBinding> Bindings);

public sealed class RisingStonesSession(string accountId, bool reusesSession = false)
{
    public const string ServiceUrl = "https://apiff14risingstones.web.sdo.com/api/home/GHome/login";
    public const string StatusUrl = "https://apiff14risingstones.web.sdo.com/api/home/GHome/isLogin";
    public const string BindingsUrl = "https://apiff14risingstones.web.sdo.com/api/home/groupAndRole/getCharacterBindInfo?platform=2";
    private readonly object gate = new();
    private CommunitySnapshot snapshot = reusesSession
        ? new("starting", "正在通过盛趣会话验证石之家", [])
        : new("confirmation", "石之家需要单独确认，可继续查看游戏角色", []);
    private bool verified;

    public static Uri Entry { get; } = new(ServiceUrl + "?redirectUrl="
        + "https%3A%2F%2Fff14risingstones.web.sdo.com%2Fpc%2Findex.html%23%2Fpost");

    public CommunitySnapshot Snapshot { get { lock (gate) return snapshot; } }

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
    public static bool IsBindingStatus(Uri uri) =>
        IsApi(uri, "/api/home/groupAndRole/getCharacterBindInfo") && uri.Query == "?platform=2";

    public bool AllowsApiRequest(Uri uri, string method) => method switch
    {
        "GET" => IsAllowedPage(uri) || IsLoginStatus(uri) || IsBindingStatus(uri),
        "OPTIONS" => IsLoginStatus(uri) || IsBindingStatus(uri),
        _ => false
    };

    public void ObserveLogin(byte[] body, bool ticketOnlyProfile = false)
    {
        lock (gate)
        {
            if (!TryReadData(body, out var data))
            {
                Unverified(verified ? "failed" : "confirmation", "石之家尚未返回可验证的登录状态");
                return;
            }
            var state = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("isLogin", out var login)
                ? login.ValueKind : data.ValueKind;
            if (state == JsonValueKind.False)
            {
                Unverified(verified ? "expired" : "confirmation", verified ? "石之家会话已过期" : "请在石之家官方窗口登录");
                return;
            }
            if (state != JsonValueKind.True || !MatchesAccount(data, ticketOnlyProfile))
            {
                Unverified("identity", "尚未核对石之家与游戏账号的一致性");
                return;
            }
            if (!verified)
                snapshot = new("connected", "石之家会话已验证，正在检查社区角色", []);
            verified = true;
        }
    }

    public void ObserveBindings(byte[] body)
    {
        lock (gate)
        {
            if (!verified || !TryReadData(body, out var data))
                return;
            if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("characters", out var characters))
                data = characters;
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
            snapshot = bindings.Count == 0
                ? new("unbound", "石之家已登录，请在官方窗口选择社区角色", [])
                : new("connected", "石之家登录已验证", bindings.ToArray());
        }
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
        snapshot = new(state, detail, []);
    }

    private static bool IsHttps(Uri uri) =>
        uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && uri.UserInfo.Length == 0;

    private static bool IsApi(Uri uri, string path) =>
        IsHttps(uri) && uri.Host == "apiff14risingstones.web.sdo.com" && uri.AbsolutePath == path;

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

    private static bool TryReadData(byte[] body, out JsonElement data)
    {
        data = default;
        if (body.Length > 64 * 1024)
            return false;
        try
        {
            using var json = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 12 });
            if (json.RootElement.ValueKind != JsonValueKind.Object || !json.RootElement.TryGetProperty("data", out var value))
                return false;
            data = value.Clone();
            return true;
        }
        catch (JsonException) { return false; }
    }
}
