using Microsoft.UI.Xaml.Controls;

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
        view.HandlerChanged += OnHandlerChanged;

        var window = new Window(new ContentPage { Content = view })
        {
            Title = "Lodestone · 艾欧泽亚档案",
            Width = 1280,
            Height = 900
        };
        window.Destroying += (_, _) => commands.Dispose();
        return window;
    }

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
                        && uri.Scheme == Uri.UriSchemeHttps && uri.Host == "ff14.huijiwiki.com" && uri.IsDefaultPort
                        && uri.AbsolutePath.StartsWith("/wiki/", StringComparison.Ordinal))
                        await Launcher.Default.TryOpenAsync(uri);
                };
        };
    }

    internal static bool IsLocalWebViewUri(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps && uri.Host == "0.0.0.1" && uri.IsDefaultPort;
}
