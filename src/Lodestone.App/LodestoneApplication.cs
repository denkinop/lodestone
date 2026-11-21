using Microsoft.UI.Xaml.Controls;

namespace Lodestone.App;

public sealed class LodestoneApplication : Application
{
    protected override Window CreateWindow(IActivationState? activationState)
    {
        var view = new HybridWebView();
        view.SetInvokeJavaScriptTarget(new PreviewCommands());
        view.WebResourceRequested += PreviewResources.Respond;
        view.HandlerChanged += OnHandlerChanged;

        return new Window(new ContentPage { Content = view })
        {
            Title = "Lodestone · 艾欧泽亚档案",
            Width = 1280,
            Height = 900
        };
    }

    private static void OnHandlerChanged(object? sender, EventArgs args)
    {
        if (sender is not HybridWebView { Handler.PlatformView: WebView2 platform })
            return;

        platform.NavigationStarting += (_, navigation) =>
        {
            if (!Uri.TryCreate(navigation.Uri, UriKind.Absolute, out var uri)
                || !PreviewResources.IsLocal(uri))
                navigation.Cancel = true;
        };
        platform.CoreWebView2Initialized += (_, _) =>
        {
            if (platform.CoreWebView2 is { } core)
                core.NewWindowRequested += (_, request) => request.Handled = true;
        };
    }
}
