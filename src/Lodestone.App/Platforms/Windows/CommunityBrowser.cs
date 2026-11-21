using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Lodestone.Core.Auth;
using Lodestone.Core.Community;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using WinWindow = Microsoft.UI.Xaml.Window;

namespace Lodestone.App;

internal sealed class CommunityBrowser
{
    private WinWindow? window;
    private WebView2? webView;
    private RisingStonesSession? session;
    private IReadOnlyList<CommunityLoginCookie> federatedCookies = [];
    private bool needsInteractiveRetry;
    private int generation;
    private Uri? jobReadUri;
    private Uri? allowedRoleChange;
    private bool selectingRole;

    internal void SetSession(RisingStonesSession? replacement, IReadOnlyList<CommunityLoginCookie> cookies)
    {
        Close();
        session = replacement;
        federatedCookies = cookies;
    }

    internal Task<bool> OpenAsync()
    {
        if (needsInteractiveRetry)
            Close();
        return OpenAsync(RisingStonesSession.Entry, interactive: true, ticketOnlyProfile: false);
    }

    internal async Task ResumeAsync(Uri? callback, int? code)
    {
        if (session is not { } current || window is not null)
            return;
        if (callback is null && federatedCookies.Count == 0)
        {
            current.RequireConfirmation(code);
            return;
        }
        if (!await OpenAsync(callback ?? RisingStonesSession.Entry, interactive: false, ticketOnlyProfile: callback is not null)
            && ReferenceEquals(session, current) && window is null)
            current.RequireConfirmation(code);
    }

    internal async Task<bool> ReadJobsAsync()
    {
        if (webView?.CoreWebView2 is not { } core || session is not { } current || !InPortal(core) || !current.BeginJobRead())
            return false;
        var revision = generation;
        var request = jobReadUri = OfficialRequest(RisingStonesSession.ProfileUrl);
        var response = await FetchOfficialAsync(core, request);
        if (generation == revision && jobReadUri == request)
        {
            if (response is null)
                current.JobReadFailed();
            else
                current.ObserveCurrentRole(response);
        }
        return true;
    }

    internal async Task<string?> SelectRoleAsync(CommunityCharacter target, Func<bool> isCurrentCharacter)
    {
        if (webView?.CoreWebView2 is not { } core || session is not { CanSelectRole: true } current || !InPortal(core))
            return "石之家登录验证后才能切换角色。";
        if (selectingRole)
            return "石之家正在切换角色。";
        if (current.HasSelectedRole(target))
        {
            if (current.Snapshot.JobsStatus != "ready")
                await ReadJobsAsync();
            return null;
        }
        var revision = generation;
        bool CanContinue() => generation == revision && current.CanSelectRole && InPortal(core) && isCurrentCharacter();
        selectingRole = true;
        try
        {
            var areaReply = await FetchOfficialAsync(core, OfficialRequest(RisingStonesSession.AreasUrl));
            if (!CanContinue())
                return "游戏或石之家会话已变化，请重新选择角色。";
            if (areaReply is null || RisingStonesSession.FindAreaId(areaReply, target.Area) is not { } areaId)
                return "无法在石之家核对所选角色的大区。";

            var rolesUrl = RisingStonesSession.CharactersUrl + "?AreaID=" + areaId.ToString(CultureInfo.InvariantCulture);
            var rolesReply = await FetchOfficialAsync(core, OfficialRequest(rolesUrl));
            if (!CanContinue())
                return "游戏或石之家会话已变化，请重新选择角色。";
            if (rolesReply is null || RisingStonesSession.FindCharacterId(rolesReply, target) is not { } characterId)
                return "石之家角色列表中未找到同一大区和服务器的角色。";

            var changeUri = OfficialRequest(RisingStonesSession.SelectRoleUrl);
            var body = new Dictionary<string, string>
            {
                ["character_id"] = characterId,
                ["platform"] = "2",
                ["tempsuid"] = Guid.NewGuid().ToString("D")
            };
            allowedRoleChange = changeUri;
            var changeReply = await FetchOfficialAsync(core, changeUri, body);
            if (!CanContinue())
                return "石之家会话已变化，请重新读取角色档案。";
            if (changeReply is null || !current.ObserveRoleChange(changeReply, target))
                return "未能确认石之家角色切换，请在官方窗口查看绑定状态。";

            await ReadJobsAsync();
            return null;
        }
        finally
        {
            if (generation == revision)
            {
                allowedRoleChange = null;
                selectingRole = false;
            }
        }
    }

    private async Task<bool> OpenAsync(Uri entry, bool interactive, bool ticketOnlyProfile)
    {
        if (session is not { } current)
            return false;
        if (window is not null)
        {
            if (interactive)
            {
                window.AppWindow.Show();
                window.Activate();
            }
            return true;
        }
        var revision = ++generation;
        var browser = new WebView2();
        var shell = new WinWindow { Title = "石之家 · 盛趣官方登录", Content = browser };
        window = shell;
        webView = browser;
        bool AllowsPage(Uri uri) => RisingStonesSession.IsAllowedPage(uri)
            && (!ticketOnlyProfile || uri.Host != "login.u.sdo.com");
        shell.AppWindow.Closing += (_, args) =>
        {
            if (window == shell)
            {
                args.Cancel = true;
                shell.AppWindow.Hide();
            }
        };
        shell.Closed += (_, _) =>
        {
            if (window != shell)
                return;
            window = null;
            webView = null;
            generation++;
            browser.Close();
            current.Closed();
        };
        shell.Activate();
        if (!interactive)
        {
            shell.AppWindow.Hide();
            _ = WaitForSessionAsync(revision, current);
        }

        try
        {
            var environment = await CoreWebView2Environment.CreateWithOptionsAsync(null,
                Path.Combine(FileSystem.CacheDirectory, "community-webview"), null);
            if (generation != revision)
                return false;
            var controller = environment.CreateCoreWebView2ControllerOptions();
            controller.IsInPrivateModeEnabled = true;
            controller.ProfileName = "LodestoneCommunity-" + Guid.NewGuid().ToString("N");
            await browser.EnsureCoreWebView2Async(environment, controller);
            if (generation != revision)
                return false;

            var core = browser.CoreWebView2;
            core.Settings.IsWebMessageEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            foreach (var cookie in ticketOnlyProfile ? [] : federatedCookies)
            {
                if (cookie.Expires is { } expires && expires <= DateTime.UtcNow)
                    continue;
                var scoped = core.CookieManager.CreateCookie(cookie.Name, cookie.Value, cookie.Domain, cookie.Path);
                scoped.IsSecure = true;
                scoped.IsHttpOnly = cookie.HttpOnly;
                scoped.SameSite = CoreWebView2CookieSameSiteKind.Strict;
                if (cookie.Expires is { } expiration)
                    scoped.Expires = new DateTimeOffset(expiration).ToUnixTimeSeconds();
                core.CookieManager.AddOrUpdateCookie(scoped);
            }
            core.NewWindowRequested += (_, args) =>
            {
                args.Handled = true;
                if (Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri) && AllowsPage(uri))
                    core.Navigate(uri.AbsoluteUri);
            };
            core.NavigationStarting += (_, args) =>
            {
                args.Cancel = !Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri) || !AllowsPage(uri);
                if (args.Cancel && ticketOnlyProfile && uri is not null && RisingStonesSession.IsPassportPage(uri))
                {
                    needsInteractiveRetry = true;
                    current.RequireConfirmation();
                }
            };
            if (ticketOnlyProfile)
            {
                core.FrameNavigationStarting += (_, args) =>
                {
                    args.Cancel = !Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri) || !AllowsPage(uri);
                };
            }
            core.AddWebResourceRequestedFilter("https://apiff14risingstones.web.sdo.com/api/home/*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += (_, args) =>
            {
                if (Uri.TryCreate(args.Request.Uri, UriKind.Absolute, out var uri))
                {
                    if (generation == revision && current.CanSelectRole && InPortal(core) && allowedRoleChange == uri
                        && args.Request.Method is "POST" or "OPTIONS")
                    {
                        if (args.Request.Method == "POST")
                            allowedRoleChange = null;
                        return;
                    }
                    if (current.AllowsApiRequest(uri, args.Request.Method))
                        return;
                }
                args.Response = core.Environment.CreateWebResourceResponse(null, 403, "Forbidden", "");
            };
            var statusRequested = false;
            var bindingsRequested = false;
            var jobsRequested = false;
            core.NavigationCompleted += async (_, args) =>
            {
                if (!args.IsSuccess || statusRequested || generation != revision || !InPortal(core))
                    return;
                statusRequested = true;
                await ReadOfficialEndpointAsync(core, RisingStonesSession.StatusUrl);
            };
            core.WebResourceResponseReceived += async (_, args) =>
            {
                if (generation != revision || !InPortal(core) || args.Request.Method != "GET"
                    || !Uri.TryCreate(args.Request.Uri, UriKind.Absolute, out var uri))
                    return;
                var login = RisingStonesSession.IsLoginStatus(uri);
                if (args.Response.StatusCode != 200 || !login && !RisingStonesSession.IsBindingStatus(uri))
                    return;

                try
                {
                    var bytes = await ReadResponseAsync(args.Response, 64 * 1024);
                    if (bytes is null || generation != revision || !InPortal(core))
                        return;
                    if (login)
                    {
                        current.ObserveLogin(bytes, ticketOnlyProfile);
                        if (!bindingsRequested && current.Snapshot.State is "connected" or "unbound")
                        {
                            bindingsRequested = true;
                            await ReadOfficialEndpointAsync(core, RisingStonesSession.BindingsUrl);
                        }
                    }
                    else
                        current.ObserveBindings(bytes);

                    if (!jobsRequested && generation == revision && current.Snapshot is { State: "connected", JobsStatus: "idle" })
                    {
                        jobsRequested = true;
                        await ReadJobsAsync();
                    }
                }
                catch (Exception ex) when (ex is COMException or IOException or InvalidOperationException)
                {
                    // A navigation or window close can invalidate the observed response stream.
                }
            };
            core.Navigate(entry.AbsoluteUri);
            return true;
        }
        catch (Exception ex) when (ex is COMException or IOException or InvalidOperationException or ArgumentException)
        {
            if (generation == revision)
            {
                Close();
                if (interactive)
                    current.Failed();
            }
            return false;
        }
    }

    private async Task WaitForSessionAsync(int revision, RisingStonesSession current)
    {
        await Task.Delay(TimeSpan.FromSeconds(18));
        if (generation == revision && current.Snapshot.State == "starting")
            current.RequireConfirmation();
    }

    private static bool InPortal(CoreWebView2 core) =>
        Uri.TryCreate(core.Source, UriKind.Absolute, out var source) && RisingStonesSession.IsPortal(source);

    private static Uri OfficialRequest(string url) => new(url + (url.Contains('?') ? '&' : '?')
        + "tempsuid=" + Guid.NewGuid().ToString("D"));

    private static async Task<byte[]?> FetchOfficialAsync(CoreWebView2 core, Uri request, Dictionary<string, string>? form = null)
    {
        var method = form is null ? "GET" : "POST";
        var response = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
        async void Receive(CoreWebView2 sender, CoreWebView2WebResourceResponseReceivedEventArgs args)
        {
            if (args.Request.Method != method || !Uri.TryCreate(args.Request.Uri, UriKind.Absolute, out var uri) || uri != request)
                return;
            try
            {
                response.TrySetResult(args.Response.StatusCode == 200 && InPortal(sender)
                    ? await ReadResponseAsync(args.Response, RisingStonesSession.MaxProfileBytes) : null);
            }
            catch (Exception ex) when (ex is COMException or IOException or InvalidOperationException)
            {
                response.TrySetResult(null);
            }
        }

        core.WebResourceResponseReceived += Receive;
        try
        {
            var address = JsonSerializer.Serialize(request.AbsoluteUri);
            var body = form is null ? "" : ", body: new URLSearchParams(" + JsonSerializer.Serialize(form) + ")";
            await core.ExecuteScriptAsync($$"""
                void fetch({{address}}, { method: '{{method}}', credentials: 'include', cache: 'no-store',
                  signal: AbortSignal.timeout(15000){{body}} }).catch(() => {})
                """);
            return await response.Task.WaitAsync(TimeSpan.FromSeconds(16));
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or TimeoutException)
        {
            return null;
        }
        finally { core.WebResourceResponseReceived -= Receive; }
    }

    private static async Task<byte[]?> ReadResponseAsync(CoreWebView2WebResourceResponseView response, int maxLength)
    {
        using var content = await response.GetContentAsync();
        if (content is null)
            return null;
        using var stream = content.AsStreamForRead();
        var bytes = new byte[maxLength + 1];
        var count = await stream.ReadAtLeastAsync(bytes, bytes.Length, false);
        return count == bytes.Length ? null : bytes[..count];
    }

    private static async Task ReadOfficialEndpointAsync(CoreWebView2 core, string address)
    {
        try
        {
            await core.ExecuteScriptAsync($$"""
                void fetch('{{address}}', { credentials: 'include', cache: 'no-store' }).catch(() => {})
                """);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            // The window may already have been closed or navigated away.
        }
    }

    private void Close()
    {
        generation++;
        var shell = window;
        var browser = webView;
        window = null;
        webView = null;
        needsInteractiveRetry = false;
        jobReadUri = null;
        allowedRoleChange = null;
        selectingRole = false;
        browser?.Close();
        shell?.Close();
    }
}
