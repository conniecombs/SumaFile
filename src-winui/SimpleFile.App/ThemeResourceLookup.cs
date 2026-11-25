using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace SimpleFile.App;

internal static class ThemeResourceLookup
{
    private static long _highContrastCheckedAt = long.MinValue;
    private static bool _highContrast;

    /// <summary>
    /// The theme dictionary a brush is read from. Markup reads <c>{ThemeResource}</c> and follows Windows into
    /// High Contrast by itself; a brush set from code is looked up here, and used to be looked up as Light or Dark
    /// only, so in High Contrast the parts drawn from code kept the colours of the theme underneath.
    /// </summary>
    internal static string ThemeKey(bool highContrast, bool light) =>
        highContrast ? "HighContrast" : light ? "Light" : "Dark";

    /// <summary>Whether Windows is in a High Contrast theme. Asked of Windows at most once a second.</summary>
    internal static bool IsHighContrast()
    {
        var now = Environment.TickCount64;
        if (now - _highContrastCheckedAt > 1000 || now < _highContrastCheckedAt)
        {
            _highContrastCheckedAt = now;
            _highContrast = ReadHighContrast();
        }

        return _highContrast;
    }

    private static bool ReadHighContrast()
    {
        const uint SPI_GETHIGHCONTRAST = 0x0042;
        const uint HCF_HIGHCONTRASTON = 0x00000001;
        var info = new HighContrastInfo { Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<HighContrastInfo>() };
        try
        {
            return SystemParametersInfoW(SPI_GETHIGHCONTRAST, info.Size, ref info, 0) && (info.Flags & HCF_HIGHCONTRASTON) != 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct HighContrastInfo
    {
        public uint Size;
        public uint Flags;
        public nint DefaultScheme;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SystemParametersInfoW(uint action, uint param, ref HighContrastInfo info, uint update);

    public static Brush Brush(FrameworkElement owner, string key)
    {
        return Resource<Brush>(owner, key) ?? new SolidColorBrush(Colors.Transparent);
    }

    public static T? Resource<T>(FrameworkElement owner, string key)
        where T : class
    {
        var appResources = Application.Current.Resources;
        var themeKey = ThemeKey(IsHighContrast(), owner.ActualTheme switch
        {
            ElementTheme.Light => true,
            ElementTheme.Dark => false,
            _ => Application.Current.RequestedTheme == ApplicationTheme.Light,
        });

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
