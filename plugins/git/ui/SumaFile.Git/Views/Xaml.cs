using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;

namespace SumaFile.Git.Views;

/// <summary>
/// A thin wrapper over <see cref="XamlReader.Load(string)"/> for the plugin's theme-aware fragments (spec §5.3: a
/// plugin folder cannot hold compiled XAML). Fragments use <c>{ThemeResource …}</c> with the host keys of spec §5.1,
/// so they follow the app theme; they never use bindings, which the code sets from <c>PropertyChanged</c> instead.
/// </summary>
internal static class Xaml
{
    private const string Namespaces =
        "xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" "
        + "xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"";

    /// <summary>Parses <paramref name="fragment"/>, adding the XAML namespaces to its root element when it has none.</summary>
    public static object Load(string fragment)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fragment);
        return XamlReader.Load(WithNamespaces(fragment.Trim()));
    }

    public static T Load<T>(string fragment) where T : class =>
        Load(fragment) as T ?? throw new InvalidOperationException($"The fragment's root is not a {typeof(T).Name}.");

    /// <summary>The element named <paramref name="name"/> (<c>x:Name</c>) in <paramref name="root"/>'s namescope.</summary>
    public static T Find<T>(FrameworkElement root, string name) where T : class =>
        root.FindName(name) as T ?? throw new InvalidOperationException($"The fragment has no {typeof(T).Name} named {name}.");

    /// <summary>Finds a named element and gives it an automation id.</summary>
    public static T Find<T>(FrameworkElement root, string name, string automationId) where T : DependencyObject
    {
        var element = root.FindName(name) as T
            ?? throw new InvalidOperationException($"The fragment has no {typeof(T).Name} named {name}.");
        AutomationProperties.SetAutomationId(element, automationId);
        return element;
    }

    /// <summary>A host resource (spec §5.1 keys), or null when the host does not define it.</summary>
    public static T? Resource<T>(string key) where T : class =>
        Application.Current?.Resources is { } resources && resources.TryGetValue(key, out var value) ? value as T : null;

    /// <summary>A host brush, or the fallback key's when the first is missing.</summary>
    public static Brush? Brush(string key, string? fallbackKey = null) =>
        Resource<Brush>(key) ?? (fallbackKey is null ? null : Resource<Brush>(fallbackKey));

    /// <summary>Escapes text for an attribute value inside a fragment.</summary>
    public static string Attribute(string text) => System.Security.SecurityElement.Escape(text) ?? "";

    private static string WithNamespaces(string fragment)
    {
        if (!fragment.StartsWith('<'))
        {
            throw new ArgumentException("A XAML fragment starts with its root element.", nameof(fragment));
        }

        // The root element's start tag: its name ends at the first blank, '/' or '>'.
        var close = fragment.IndexOf('>', StringComparison.Ordinal);
        var head = close < 0 ? fragment : fragment[..close];
        var nameEnd = fragment.IndexOfAny([' ', '\r', '\n', '\t', '>', '/'], 1);
        if (nameEnd < 0 || head.Contains("xmlns=", StringComparison.Ordinal))
        {
            return fragment;
        }

        return fragment[..nameEnd] + " " + Namespaces + fragment[nameEnd..];
    }
}
