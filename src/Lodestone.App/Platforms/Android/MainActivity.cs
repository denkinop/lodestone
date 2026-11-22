using Android.App;
using Android.Content.PM;
using Android.Views;

namespace Lodestone.App;

[Activity(Theme = "@style/Maui.MainTheme.NoActionBar", MainLauncher = true, Exported = true, LaunchMode = LaunchMode.SingleTop,
    WindowSoftInputMode = SoftInput.AdjustResize,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode
        | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public sealed class MainActivity : MauiAppCompatActivity;
