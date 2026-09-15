using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace SimpleFile.App;

internal static class ThemeResourceLookup
{
    public static Brush Brush(FrameworkElement owner, string key)
    {
        return Resource<Brush>(owner, key) ?? new SolidColorBrush(Colors.Transparent);
    }

    public static T? Resource<T>(FrameworkElement owner, string key)
        where T : class
    {
        var appResources = Application.Current.Resources;
        var themeKey = owner.ActualTheme switch
        {
            ElementTheme.Light => "Light",
            ElementTheme.Dark => "Dark",
            _ => Application.Current.RequestedTheme == ApplicationTheme.Light ? "Light" : "Dark",
        };

        if (appResources.ThemeDictionaries.TryGetValue(themeKey, out var themeResources)
            && themeResources is ResourceDictionary themeDictionary
            && themeDictionary.TryGetValue(key, out var themed)
            && themed is T themedResource)
        {
            return themedResource;
        }

        return appResources.TryGetValue(key, out var value) && value is T resource
            ? resource
            : null;
    }
}
