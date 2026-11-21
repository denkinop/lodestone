using System.Globalization;
using System.Net;
using System.Text.Json;

namespace Lodestone.Core.Auth;

public sealed class SdoAuthException(string message) : IOException(message);

public sealed class GameAuthorization
{
    internal GameAuthorization(string sid, string accountId, IReadOnlyList<CommunityLoginCookie> communityCookies) =>
        (Sid, AccountId, CommunityCookies) = (sid, accountId, communityCookies);
    internal string Sid { get; }
    public string AccountId { get; }
    public IReadOnlyList<CommunityLoginCookie> CommunityCookies { get; }
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
        string account, Action<LoginChallenge> onChallenge, CancellationToken cancellationToken)
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
            var result = await PollAsync("pushMessageLogin.json", -10516808, expiry.Token,
                ("pushMsgSessionKey", key), ("guid", guid));
            return await ExchangeAsync(result, guid, cancellationToken);
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

    public async Task<GameAuthorization> ConfirmByScanAsync(Action<LoginChallenge> onChallenge, CancellationToken cancellationToken)
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
            var result = await PollAsync("codeKeyLogin.json", -10515805, expiry.Token,
                ("codeKey", key), ("guid", guid), ("maxsize", "97"));
            if (result.Data.ValueKind != JsonValueKind.Object
                || !result.Data.TryGetProperty("nextAction", out var action)
                || !int.TryParse(action.ToString(), CultureInfo.InvariantCulture, out var nextAction)
                || nextAction != 0)
                throw new SdoAuthException("盛趣需要进一步确认，请使用官方登录页面完成");
            return await ExchangeAsync(result, guid, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SdoAuthException("二维码已过期，请重新获取");
        }
    }

    private async Task<GameAuthorization> ExchangeAsync(CasResult result, string guid, CancellationToken token)
    {
        var tgt = Field(result.Data, "tgt");
        if (tgt.Any(value => value is <= ' ' or >= '\u007f' or ';' or ','))
            throw new SdoAuthException("盛趣响应票据格式无效");
        var promoted = await ReadJsonAsync("getPromotionInfo.json", token, tgt, ("tgt", tgt));
        RequireSuccess(promoted, "盛趣未授权游戏会话");
        var ticket = await ReadJsonAsync("ssoLogin.json", token, tgt, ("tgt", tgt), ("guid", guid));
        RequireSuccess(ticket, "无法获取游戏会话");
        return new GameAuthorization(Field(ticket.Data, "ticket", 64), Field(result.Data, "sndaId"),
            ScopedCommunityCookies());
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
        var query = fields.Select(field => KeyValuePair.Create(field.Key, field.Value)).Concat(common)
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

    private static string Field(JsonElement data, string key, int maxLength = 2048)
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
        throw new SdoAuthException("盛趣认证响应缺少必要字段");
    }

    private static int Number(JsonElement value) => value.ValueKind == JsonValueKind.String
        ? int.Parse(value.GetString()!, CultureInfo.InvariantCulture) : value.GetInt32();
    private readonly record struct CasResult(int Code, JsonElement Data);

    public void Dispose() => http.Dispose();
}
