namespace SimpleFile.Core;

/// <summary>
/// The theme brushes a row decoration may name. A plugin's brush key is a hint, never a resource lookup the plugin
/// controls: any key outside <see cref="Allowed"/> draws with <c>SfTextMutedBrush</c>.
/// </summary>
public static class DecorationBrushKeys
{
    /// <summary>The fallback for a blank or unknown key.</summary>
    public const string Fallback = "SfTextMutedBrush";

    /// <summary>The stable decoration and text brushes (spec §5.1), compared ordinally.</summary>
    public static IReadOnlySet<string> Allowed { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "SfDecorationAddedBrush",
        "SfDecorationModifiedBrush",
        "SfDecorationUntrackedBrush",
        "SfDecorationDeletedBrush",
        "SfTextPrimaryBrush",
        "SfTextMutedBrush",
        "SfAccentBrush",
    };

    /// <summary>The key itself when it is allowed, otherwise <see cref="Fallback"/>.</summary>
    public static string Normalize(string? key) =>
        key is not null && Allowed.Contains(key) ? key : Fallback;
}
