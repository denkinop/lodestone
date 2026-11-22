#if WINDOWS
using Microsoft.UI.Xaml.Controls;
#endif

namespace Lodestone.App;

public sealed class LodestoneApplication : Application
{
    protected override Window CreateWindow(IActivationState? activationState)
    {
        var view = new HybridWebView();
        var commands = new ClientCommands();
        view.SetInvokeJavaScriptTarget(commands);
        view.WebResourceRequested += commands.GetGamePreviewResource;
        view.WebResourceRequested += commands.GetLoginResource;
#if WINDOWS
        view.HandlerChanged += OnHandlerChanged;
#endif

        var page = new ContentPage { Content = view };
#if ANDROID
        page.SafeAreaEdges = SafeAreaEdges.All;
#endif
        var window = new Window(page)
        {
            Title = "掌上艾欧泽亚",
#if WINDOWS
            Width = 1280,
            Height = 900,
#endif
        };
#if WINDOWS
        window.Created += (_, _) =>
        {
            var icon = Path.Combine(AppContext.BaseDirectory, "lodestone.ico");
            if (window.Handler?.PlatformView is Microsoft.UI.Xaml.Window nativeWindow && File.Exists(icon))
                nativeWindow.AppWindow.SetIcon(icon);
        };
#endif
        window.Destroying += (_, _) => commands.Dispose();
        return window;
    }

#if WINDOWS
    private static void OnHandlerChanged(object? sender, EventArgs args)
    {
        if (sender is not HybridWebView { Handler.PlatformView: WebView2 platform })
            return;

        platform.NavigationStarting += (_, navigation) =>
        {
            if (!Uri.TryCreate(navigation.Uri, UriKind.Absolute, out var uri)
                || !IsLocalWebViewUri(uri))
                navigation.Cancel = true;
        };
        platform.CoreWebView2Initialized += (_, _) =>
        {
            if (platform.CoreWebView2 is { } core)
                core.NewWindowRequested += async (_, request) =>
                {
                    request.Handled = true;
                    if (request.IsUserInitiated && Uri.TryCreate(request.Uri, UriKind.Absolute, out var uri)
                        && IsExternalLink(uri))
                        await Launcher.Default.TryOpenAsync(uri);
                };
        };
    }
#endif

    internal static bool IsExternalLink(Uri uri) => OfficialHomeClient.IsOfficialWebUri(uri)
        || (uri.Scheme == Uri.UriSchemeHttps && uri.Host == "ff14.huijiwiki.com" && uri.IsDefaultPort
            && string.IsNullOrEmpty(uri.UserInfo) && uri.AbsolutePath.StartsWith("/wiki/", StringComparison.Ordinal));

    internal static bool IsLocalWebViewUri(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps && uri.Host == "0.0.0.1" && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo);
}
