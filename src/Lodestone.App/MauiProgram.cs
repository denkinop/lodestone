namespace Lodestone.App;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder().UseMauiApp<LodestoneApplication>();
#if ANDROID
        builder.ConfigureMauiHandlers(handlers => handlers.AddHandler<HybridWebView, AndroidHybridWebViewHandler>());
#endif
        return builder.Build();
    }
}
