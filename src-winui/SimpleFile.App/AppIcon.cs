using Microsoft.UI.Xaml;

namespace SimpleFile.App;

internal static class AppIcon
{
    private const string PublishedIconName = "SumaFile.ico";

    public static void ApplyTo(Window window)
    {
        try
        {
            var iconPath = Path.Combine(AppContext.BaseDirectory, PublishedIconName);
            if (File.Exists(iconPath))
            {
                window.AppWindow.SetIcon(iconPath);
            }
        }
        catch (Exception exception)
        {
            App.LogCrash("AppIcon.ApplyTo", exception);
        }
    }
}
