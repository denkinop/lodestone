using Android.Webkit;
using Microsoft.Maui.Handlers;
using Microsoft.Maui.Platform;
using AndroidWebView = Android.Webkit.WebView;

namespace Lodestone.App;

internal sealed class AndroidHybridWebViewHandler : HybridWebViewHandler
{
    protected override void ConnectHandler(AndroidWebView platformView)
    {
        base.ConnectHandler(platformView);
        platformView.Settings.AllowFileAccess = false;
        platformView.Settings.AllowContentAccess = false;
        platformView.Settings.SetSupportMultipleWindows(false);
        platformView.SetWebViewClient(new LocalWebViewClient(this));
    }

    private sealed class LocalWebViewClient(HybridWebViewHandler handler) : MauiHybridWebViewClient(handler)
    {
        public override bool ShouldOverrideUrlLoading(AndroidWebView? view, IWebResourceRequest? request)
        {
            if (!Uri.TryCreate(request?.Url?.ToString(), UriKind.Absolute, out var uri))
                return true;
            if (LodestoneApplication.IsLocalWebViewUri(uri))
                return false;
            if (request is { IsForMainFrame: true, HasGesture: true } && LodestoneApplication.IsExternalLink(uri))
                _ = Launcher.Default.TryOpenAsync(uri);
            return true;
        }
    }
}
