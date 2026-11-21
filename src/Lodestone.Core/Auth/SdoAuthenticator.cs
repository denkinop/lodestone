using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Lodestone.Core.Community;

namespace Lodestone.Core.Auth;

public sealed class SdoAuthException(string message) : IOException(message);
public sealed class SavedGameLoginRejectedException() : IOException("保存的盛趣登录已失效，请重新授权");

public sealed class SavedGameLogin(string accountId, string sessionKey)
{
    public string AccountId { get; } = accountId;
    [JsonIgnore]
    public string SessionKey { get; } = sessionKey;
}

public sealed class GameAuthorization
{
    private Func<CancellationToken, Task<CommunityTicketResult>>? communityExchange;

    internal GameAuthorization(string sid, string accountId, IReadOnlyList<CommunityLoginCookie> communityCookies,
        Func<CancellationToken, Task<CommunityTicketResult>> communityExchange, SavedGameLogin? savedLogin = null) =>
        (Sid, AccountId, CommunityCookies, this.communityExchange, SavedLogin) =
        (sid, accountId, communityCookies, communityExchange, savedLogin);
    internal string Sid { get; }
    public string AccountId { get; }
    [JsonIgnore]
    public SavedGameLogin? SavedLogin { get; }
    [JsonIgnore]
    public IReadOnlyList<CommunityLoginCookie> CommunityCookies { get; }

    public Task<CommunityTicketResult> ExchangeForCommunityAsync(CancellationToken cancellationToken)
    {
        var exchange = Interlocked.Exchange(ref communityExchange, null);
        return exchange is null ? Task.FromResult(new CommunityTicketResult(null)) : exchange(cancellationToken);
    }
}

public sealed class CommunityLoginCookie
{
    internal CommunityLoginCookie(Cookie cookie)
    {
        Name = cookie.Name;
        Value = cookie.Value;
        Domain = cookie.Domain;
        Path = cookie.Path;
        HttpOnly = cookie.HttpOnly;
        Expires = cookie.Expires == DateTime.MinValue ? null : cookie.Expires.ToUniversalTime();
    }

    public string Name { get; }
    [JsonIgnore]
    public string Value { get; }
    public string Domain { get; }
    public string Path { get; }
    public bool HttpOnly { get; }
    public DateTime? Expires { get; }
}

public abstract record LoginChallenge;
public sealed record PhoneConfirmation(string Number) : LoginChallenge;
public sealed record ScanConfirmation(byte[] Png) : LoginChallenge;

public sealed class SdoAuthenticator : IDisposable
{
    private static readonly Uri Cas = new("https://cas.sdo.com/authen/");
    private static readonly Uri CommunityCas = new("https://login.u.sdo.com/sdo/iframe/");
    private const string UserAgent =
        "Mozilla/4.0 (compatible; MSIE 8.0; Windows NT 5.1; Trident/4.0; " +
        "Mozilla/4.0 (compatible; MSIE 6.0; Windows NT 5.1; SV1) ; InfoPath.2; " +
        ".NET CLR 2.0.50727; MS-RTC LM 8; .NET CLR 3.0.04506.648; " +
        ".NET CLR 3.5.21022; .NET CLR 1.1.4322; .NET CLR 3.0.4506.2152; .NET CLR 3.5.30729)";
    private static readonly TimeSpan PollDelay = TimeSpan.FromSeconds(1);
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];
    private static readonly (string Key, string Value)[] Remember = [("autoLoginFlag", "1"), ("autoLoginKeepTime", "30")];
    private readonly HttpClient http;
    private readonly CookieContainer cookies;
    private readonly SdoIdentity identity;
    private readonly KeyValuePair<string, string>[] common;

    public SdoAuthenticator(Guid installation) : this(installation, new CookieContainer()) { }

    private SdoAuthenticator(Guid installation, CookieContainer cookies) : this(installation, cookies,
        new SocketsHttpHandler { CookieContainer = cookies, AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(8) }) { }

    internal SdoAuthenticator(Guid installation, CookieContainer cookies, HttpMessageHandler handler)
    {
        this.cookies = cookies;
        http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15), MaxResponseContentBufferSize = 128 * 1024 };
        identity = SdoIdentity.FromInstallation(installation);
        common = new Dictionary<string, string>
        {
            ["authenSource"] = "1", ["appId"] = "100001900", ["areaId"] = "1", ["appIdSite"] = "100001900",
            ["locale"] = "zh_CN", ["productId"] = "4", ["frameType"] = "1", ["endpointOS"] = "1",
            ["version"] = "21", ["customSecurityLevel"] = "2", ["deviceId"] = identity.Device,
            ["thirdLoginExtern"] = "0", ["macId"] = identity.Mac, ["epIp"] = "", ["epName"] = identity.Host,
            ["extendInfo"] = "", ["sdoVersion"] = "", ["runTimeId"] = "", ["productVersion"] = "1.9.7.10",
            ["tag"] = "0"
        }.ToArray();
    }

    public async Task<GameAuthorization> ConfirmByPhoneAsync(
        string account, Action<LoginChallenge> onChallenge, CancellationToken cancellationToken, bool remember = false)
    {
        if (string.IsNullOrWhiteSpace(account) || account.Length > 128 || account.Any(char.IsControl))
            throw new SdoAuthException("请输入有效的盛趣账号");
        var guid = await ReadGuidAsync(cancellationToken);
        await CancelPhoneAsync(guid, cancellationToken);
        var pushed = await ReadJsonAsync("sendPushMessage.json", cancellationToken, null, ("inputUserId", account.Trim()));
        RequireSuccess(pushed, "无法向叨鱼发送登录确认");
        var key = Field(pushed.Data, "pushMsgSessionKey");
        onChallenge(new PhoneConfirmation(Field(pushed.Data, "pushMsgSerialNum")));
        using var expiry = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        expiry.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            (string Key, string Value)[] fields = [("pushMsgSessionKey", key), ("guid", guid)];
            var result = await PollAsync("pushMessageLogin.json", -10516808, expiry.Token,
                remember ? [.. fields, .. Remember] : fields);
            var accountId = Field(result.Data, "sndaId");
            var saved = remember ? ReadSavedLogin(result.Data, accountId) : null;
            return await ExchangeAsync(ReadTgt(result.Data), accountId, guid, saved, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SdoAuthException("叨鱼确认已过期，请重新发起");
        }
        finally
        {
            if (expiry.IsCancellationRequested)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try { await CancelPhoneAsync(guid, cleanup.Token); }
                catch (Exception ex) when (ex is SdoAuthException or OperationCanceledException) { }
            }
        }
    }

    public async Task<GameAuthorization> ConfirmByScanAsync(Action<LoginChallenge> onChallenge, CancellationToken cancellationToken,
        bool remember = false)
    {
        var guid = await ReadGuidAsync(cancellationToken);
        using var request = Request("getCodeKey.json", null, [("maxsize", "89")]);
        var png = await ReadBodyAsync(request, cancellationToken);
        var key = cookies.GetCookies(request.RequestUri!)["CODEKEY"]?.Value;
        if (string.IsNullOrEmpty(key) || png.Length > 65536 || !png.AsSpan().StartsWith(PngSignature))
            throw new SdoAuthException("盛趣未返回有效登录二维码");
        onChallenge(new ScanConfirmation(png));
        using var expiry = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        expiry.CancelAfter(TimeSpan.FromMinutes(5));
        try
        {
            (string Key, string Value)[] fields = [("codeKey", key), ("guid", guid), ("maxsize", "97")];
            var result = await PollAsync("codeKeyLogin.json", -10515805, expiry.Token,
                remember ? [.. fields, .. Remember] : fields);
            if (result.Data.ValueKind != JsonValueKind.Object
                || !result.Data.TryGetProperty("nextAction", out var action)
                || !int.TryParse(action.ToString(), CultureInfo.InvariantCulture, out var nextAction)
                || nextAction != 0)
                throw new SdoAuthException("盛趣需要进一步确认，请使用官方登录页面完成");
            var accountId = Field(result.Data, "sndaId");
            var tgt = ReadTgt(result.Data);
            var saved = remember ? await RememberScanAsync(tgt, accountId, cancellationToken) : null;
            return await ExchangeAsync(saved?.Tgt ?? tgt, accountId, guid, saved?.Login, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SdoAuthException("二维码已过期，请重新获取");
        }
    }

    private async Task<(string Tgt, SavedGameLogin Login)?> RememberScanAsync(string tgt, string accountId, CancellationToken token)
    {
        try
        {
            var result = await ReadJsonAsync("accountGroupLogin", token, null,
                [("serviceUrl", "http://www.sdo.com"), ("tgt", tgt), ("sndaId", accountId), .. Remember]);
            if (result.Code != 0 || RequiresConfirmation(result.Data))
                return null;
            if (result.Data.ValueKind == JsonValueKind.Object && result.Data.TryGetProperty("sndaId", out _)
                && Field(result.Data, "sndaId") != accountId)
                return null;
            var saved = ReadSavedLogin(result.Data, accountId);
            return saved is null ? null : (ReadTgt(result.Data), saved);
        }
        catch (SdoAuthException) { return null; }
    }

    public async Task<GameAuthorization> RestoreAsync(SavedGameLogin saved, Func<SavedGameLogin, Task> onRefreshed,
        CancellationToken token)
    {
        var guid = await ReadGuidAsync(token);
        var refreshed = await ReadJsonAsync("autoLogin.json", token, null, ("autoLoginSessionKey", saved.SessionKey), ("guid", guid));
        RequireSavedAccount(refreshed, saved.AccountId);
        var rotated = ReadSavedLogin(refreshed.Data, saved.AccountId) ?? throw new SavedGameLoginRejectedException();
        var tgt = ReadTgt(refreshed.Data);
        // Persist the rotated key before any further network operations that might fail.
        await onRefreshed(rotated);
        var signedIn = await ReadJsonAsync("fastLogin.json", token, null, ("tgt", tgt), ("guid", guid));
        RequireSavedAccount(signedIn, saved.AccountId);
        return await ExchangeAsync(ReadTgt(signedIn.Data), saved.AccountId, guid, rotated, token);
    }

    private static void RequireSavedAccount(CasResult result, string accountId)
    {
        if (result.Code != 0 || RequiresConfirmation(result.Data) || ReadField(result.Data, "sndaId") != accountId)
            throw new SavedGameLoginRejectedException();
    }

    private static SavedGameLogin? ReadSavedLogin(JsonElement data, string accountId) =>
        ReadField(data, "autoLoginSessionKey") is { } key ? new(accountId, key) : null;

    private static string ReadTgt(JsonElement data)
    {
        var tgt = Field(data, "tgt");
        if (tgt.Any(value => value is <= ' ' or >= '\u007f' or ';' or ','))
            throw new SdoAuthException("盛趣响应票据格式无效");
        return tgt;
    }

    private async Task<GameAuthorization> ExchangeAsync(string tgt, string accountId, string guid,
        SavedGameLogin? saved, CancellationToken token)
    {
        var promoted = await ReadJsonAsync("getPromotionInfo.json", token, tgt, ("tgt", tgt));
        RequireSuccess(promoted, "盛趣未授权游戏会话");
        var ticket = await ReadJsonAsync("ssoLogin.json", token, tgt, ("tgt", tgt), ("guid", guid));
        RequireSuccess(ticket, "无法获取游戏会话");
        var sid = Field(ticket.Data, "ticket", 64);
        return new GameAuthorization(sid, accountId, ScopedCommunityCookies(),
            cancellationToken => ExchangeCommunityAsync(tgt, guid, sid, accountId, cancellationToken), saved);
    }

    private async Task<CommunityTicketResult> ExchangeCommunityAsync(
        string tgt, string guid, string gameSid, string accountId, CancellationToken cancellationToken)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(8));
        var token = budget.Token;
        var service = RisingStonesSession.ServiceUrl;
        try
        {
            // The official community portal uses a separate application and a service-specific callback.
            (string Key, string Value)[] audience = [
                ("appId", "6788"), ("appIdSite", "6788"), ("authenSource", "2"),
                ("productId", "2"), ("productVersion", "3.1.0"), ("serviceUrl", service),
                ("tgt", tgt), ("guid", guid)
            ];
            var promotion = await ReadJsonAsync("getPromotionInfo.json", token, tgt, audience);
            if (promotion.Code != 0 || RequiresConfirmation(promotion.Data))
                return new CommunityTicketResult(null, promotion.Code == 0 ? null : promotion.Code);

            var issued = await ReadJsonAsync("ssoLogin.json", token, tgt, audience);
            if (issued.Code != 0 || RequiresConfirmation(issued.Data))
                return new CommunityTicketResult(null, issued.Code == 0 ? null : issued.Code);

            if (issued.Data.ValueKind == JsonValueKind.Object && issued.Data.TryGetProperty("sndaId", out _)
                && !string.Equals(Field(issued.Data, "sndaId"), accountId, StringComparison.Ordinal))
                return new CommunityTicketResult(null);
            var ticket = Field(issued.Data, "ticket", 512);
            if (ticket == gameSid || ticket == tgt || ticket.Any(character => character is < '!' or > '~'))
                return new CommunityTicketResult(null);
            return new CommunityTicketResult(ticket);
        }
        catch (SdoAuthException)
        {
            return new CommunityTicketResult(null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new CommunityTicketResult(null);
        }
    }

    private static bool RequiresConfirmation(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object)
            return false;
        if (data.TryGetProperty("promotionUrl", out var url) && url.ValueKind != JsonValueKind.Null
            && (url.ValueKind != JsonValueKind.String || !string.IsNullOrWhiteSpace(url.GetString())))
            return true;
        return data.TryGetProperty("nextAction", out var action)
            && (!int.TryParse(action.ToString(), CultureInfo.InvariantCulture, out var value) || value != 0);
    }

    private IReadOnlyList<CommunityLoginCookie> ScopedCommunityCookies()
    {
        // Use the issuer's original scope. The game ticket and game-only Cookie headers stay with CAS.
        var issued = cookies.GetCookies(CommunityCas).Cast<Cookie>()
            .Where(cookie => cookie.Secure && !cookie.Expired && !string.IsNullOrEmpty(cookie.Value)
                && cookie.Name is "CASTGC" or "CAS_LOGIN_STATE").ToArray();
        return issued.Any(cookie => cookie.Name == "CASTGC")
            ? issued.Select(cookie => new CommunityLoginCookie(cookie)).ToArray()
            : [];
    }

    private async Task<string> ReadGuidAsync(CancellationToken token)
    {
        var result = await ReadJsonAsync("getGuid.json", token, null, ("generateDynamicKey", "1"));
        RequireSuccess(result, "无法开始盛趣认证");
        return Field(result.Data, "guid");
    }

    private async Task CancelPhoneAsync(string guid, CancellationToken token) =>
        await ReadJsonAsync("cancelPushMessageLogin.json", token, null, ("pushMsgSessionKey", ""), ("guid", guid));

    private async Task<CasResult> PollAsync(
        string endpoint, int pendingCode, CancellationToken token, params (string Key, string Value)[] fields)
    {
        while (true)
        {
            var result = await ReadJsonAsync(endpoint, token, null, fields);
            if (result.Code != pendingCode)
            {
                RequireSuccess(result, "盛趣未完成登录确认");
                return result;
            }
            await Task.Delay(PollDelay, token);
        }
    }

    private async Task<CasResult> ReadJsonAsync(string endpoint, CancellationToken token, string? tgt,
        params (string Key, string Value)[] fields)
    {
        using var request = Request(endpoint, tgt, fields);
        var bytes = await ReadBodyAsync(request, token);
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 12 });
            var root = document.RootElement;
            var code = Number(root.GetProperty("return_code"));
            var data = root.TryGetProperty("data", out var value) ? value.Clone() : default;
            return new CasResult(code, data);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new SdoAuthException("盛趣认证服务返回了无法识别的响应");
        }
    }

    private HttpRequestMessage Request(string endpoint, string? tgt, (string Key, string Value)[] fields)
    {
        var query = fields.Select(field => KeyValuePair.Create(field.Key, field.Value))
            .Concat(common.Where(item => !fields.Any(field => field.Key == item.Key)))
            .Select(field => Uri.EscapeDataString(field.Key) + "=" + Uri.EscapeDataString(field.Value));
        var uri = new Uri(Cas, endpoint + "?" + string.Join('&', query));
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        request.Headers.TryAddWithoutValidation("Cache-Control", "no-cache");
        // CAS assigns its own cookies as needed. The stable installation value seeds a new session.
        if (cookies.GetCookies(uri)["CASCID"] is null)
        {
            cookies.Add(Cas, new Cookie("CASCID", identity.ClientId, "/authen") { Secure = true });
            cookies.Add(Cas, new Cookie("SECURE_CASCID", identity.ClientId, "/authen") { Secure = true });
        }
        if (tgt is not null)
            request.Headers.TryAddWithoutValidation("Cookie", $"CASTGC={tgt}; CAS_LOGIN_STATE=1");
        return request;
    }

    private async Task<byte[]> ReadBodyAsync(HttpRequestMessage request, CancellationToken token)
    {
        try
        {
            using var response = await http.SendAsync(request, token);
            if (!response.IsSuccessStatusCode)
                throw new SdoAuthException($"盛趣认证服务返回 HTTP {(int)response.StatusCode}");
            return await response.Content.ReadAsByteArrayAsync(token);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException && !token.IsCancellationRequested)
        {
            throw new SdoAuthException("无法连接盛趣认证服务，请稍后重试");
        }
    }

    private static void RequireSuccess(CasResult result, string message)
    {
        if (result.Code != 0)
            throw new SdoAuthException($"{message}（{result.Code}）");
    }

    private static string Field(JsonElement data, string key, int maxLength = 2048) =>
        ReadField(data, key, maxLength) ?? throw new SdoAuthException("盛趣认证响应缺少必要字段");

    private static string? ReadField(JsonElement data, string key, int maxLength = 2048)
    {
        if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty(key, out var value))
        {
            var text = value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                _ => null
            };
            if (!string.IsNullOrWhiteSpace(text) && text.Length <= maxLength && !text.Any(char.IsControl))
                return text;
        }
        return null;
    }

    private static int Number(JsonElement value) => value.ValueKind == JsonValueKind.String
        ? int.Parse(value.GetString()!, CultureInfo.InvariantCulture) : value.GetInt32();
    private readonly record struct CasResult(int Code, JsonElement Data);

    public void Dispose() => http.Dispose();
}
