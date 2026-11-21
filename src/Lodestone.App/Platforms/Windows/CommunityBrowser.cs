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
    private int generation;

    internal void SetSession(RisingStonesSession? replacement, IReadOnlyList<CommunityLoginCookie> cookies)
    {
        Close();
        session = replacement;
        federatedCookies = cookies;
    }

    internal async Task<bool> OpenAsync()
    {
        if (session is not { } current)
            return false;
        if (window is not null)
        {
            window.AppWindow.Show();
            window.Activate();
            return true;
        }
        var revision = ++generation;
        var browser = new WebView2();
        var shell = new WinWindow { Title = "石之家 · 盛趣官方登录", Content = browser };
        window = shell;
        webView = browser;
        shell.AppWindow.Closing += (_, args) =>
        {
            if (window == shell && current.Snapshot.State is "connected" or "unbound")
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

        try
        {
            var environment = await CoreWebView2Environment.CreateWithOptionsAsync(null,
                Path.Combine(FileSystem.CacheDirectory, "community-webview"), null);
            if (generation != revision)
                return false;
            var controller = environment.CreateCoreWebView2ControllerOptions();
            controller.IsInPrivateModeEnabled = true;
            controller.ProfileName = "LodestoneCommunity";
            await browser.EnsureCoreWebView2Async(environment, controller);
            if (generation != revision)
                return false;

            var core = browser.CoreWebView2;
            core.Settings.IsWebMessageEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            foreach (var cookie in federatedCookies)
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
                if (Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri) && RisingStonesSession.IsAllowedPage(uri))
                    core.Navigate(uri.AbsoluteUri);
            };
            core.NavigationStarting += (_, args) =>
            {
                args.Cancel = !Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri) || !RisingStonesSession.IsAllowedPage(uri);
            };
            core.AddWebResourceRequestedFilter("https://apiff14risingstones.web.sdo.com/api/home/*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += (_, args) =>
            {
                if (!Uri.TryCreate(args.Request.Uri, UriKind.Absolute, out var uri)
                    || !current.AllowsApiRequest(uri, args.Request.Method))
                    args.Response = core.Environment.CreateWebResourceResponse(null, 403, "Forbidden", "");
            };
            core.WebResourceResponseReceived += async (_, args) =>
            {
                if (generation != revision || args.Request.Method != "GET"
                    || !Uri.TryCreate(args.Request.Uri, UriKind.Absolute, out var uri))
                    return;
                var login = RisingStonesSession.IsLoginStatus(uri);
                if (args.Response.StatusCode != 200 || !login && !RisingStonesSession.IsBindingStatus(uri))
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
                        current.ObserveLogin(bytes[..count]);
                    else
                        current.ObserveBindings(bytes[..count]);
                }
                catch (Exception ex) when (ex is COMException or IOException or InvalidOperationException)
                {
                    // A navigation or window close can invalidate the observed response stream.
                }
            };
            core.Navigate(RisingStonesSession.Entry.AbsoluteUri);
            return true;
        }
        catch (Exception ex) when (ex is COMException or IOException or InvalidOperationException or ArgumentException)
        {
            if (generation == revision)
            {
                Close();
                current.Failed();
            }
            return false;
        }
    }

    private void Close()
    {
        generation++;
        var shell = window;
        var browser = webView;
        window = null;
        webView = null;
        browser?.Close();
        shell?.Close();
    }
}
