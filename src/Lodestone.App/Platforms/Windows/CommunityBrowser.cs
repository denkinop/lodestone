using System.Runtime.InteropServices;
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
                if (!Uri.TryCreate(args.Request.Uri, UriKind.Absolute, out var uri)
                    || !current.AllowsApiRequest(uri, args.Request.Method))
                    args.Response = core.Environment.CreateWebResourceResponse(null, 403, "Forbidden", "");
            };
            var statusRequested = false;
            var bindingsRequested = false;
            core.NavigationCompleted += async (_, args) =>
            {
                if (!args.IsSuccess || statusRequested || generation != revision || !InPortal(core))
                    return;
                statusRequested = true;
                await ReadOfficialEndpointAsync(core, RisingStonesSession.StatusUrl);
            };
            core.WebResourceResponseReceived += async (_, args) =>
            {
                if (generation != revision || args.Request.Method != "GET"
                    || !Uri.TryCreate(args.Request.Uri, UriKind.Absolute, out var uri))
                    return;
                var login = RisingStonesSession.IsLoginStatus(uri);
                var currentRole = RisingStonesSession.IsCurrentRole(uri);
                if (args.Response.StatusCode != 200 || !login && !currentRole && !RisingStonesSession.IsBindingStatus(uri))
                    return;

                try
                {
                    using var content = await args.Response.GetContentAsync();
                    if (content is null || generation != revision)
                        return;
                    using var stream = content.AsStreamForRead();
                    var bytes = new byte[64 * 1024 + 1];
                    var count = await stream.ReadAtLeastAsync(bytes, bytes.Length, false);
                    if (count == bytes.Length || generation != revision)
                        return;
                    if (login)
                    {
                        current.ObserveLogin(bytes[..count], ticketOnlyProfile && InPortal(core));
                        if (!bindingsRequested && InPortal(core)
                            && current.Snapshot.State is "connected" or "unbound")
                        {
                            bindingsRequested = true;
                            await ReadOfficialEndpointAsync(core, RisingStonesSession.BindingsUrl);
                        }
                    }
                    else if (currentRole)
                        current.ObserveCurrentRole(bytes[..count]);
                    else
                        current.ObserveBindings(bytes[..count]);
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
        browser?.Close();
        shell?.Close();
    }
}
