namespace SimpleFile.Core;

public enum OmnibarMode
{
    Navigate,
    Search,
    Filter,
    Command,
}

public enum OmnibarIntentKind
{
    Navigate,
    Search,
    Filter,
    Command,
}

public sealed record OmnibarIntent(OmnibarIntentKind Kind, string Value)
{
    public static OmnibarIntent Empty(OmnibarMode mode, string currentPath)
    {
        return mode switch
        {
            OmnibarMode.Search => new OmnibarIntent(OmnibarIntentKind.Search, ""),
            OmnibarMode.Filter => new OmnibarIntent(OmnibarIntentKind.Filter, ""),
            OmnibarMode.Command => new OmnibarIntent(OmnibarIntentKind.Command, ""),
            _ => new OmnibarIntent(OmnibarIntentKind.Navigate, currentPath),
        };
    }
}

public static class OmnibarIntentParser
{
    public static OmnibarIntent Parse(string? text, OmnibarMode mode, string? currentPath = null)
    {
        var value = (text ?? "").Trim();
        var path = currentPath ?? "";
        if (value.Length == 0)
        {
            return OmnibarIntent.Empty(mode, path);
        }

        if (value.StartsWith(">", StringComparison.Ordinal))
        {
            return new OmnibarIntent(OmnibarIntentKind.Command, value[1..].Trim());
        }

        if (value.StartsWith("?", StringComparison.Ordinal))
        {
            return new OmnibarIntent(OmnibarIntentKind.Search, value[1..].Trim());
        }

        if (value.StartsWith("~", StringComparison.Ordinal))
        {
            return new OmnibarIntent(OmnibarIntentKind.Filter, value[1..].Trim());
        }

        return mode switch
        {
            OmnibarMode.Search => new OmnibarIntent(OmnibarIntentKind.Search, value),
            OmnibarMode.Filter => new OmnibarIntent(OmnibarIntentKind.Filter, value),
            OmnibarMode.Command => new OmnibarIntent(OmnibarIntentKind.Command, value),
            _ => new OmnibarIntent(OmnibarIntentKind.Navigate, value),
        };
    }

    public static IReadOnlyList<AppCommand> FilterCommands(string? query, bool includeGit)
    {
        return AppCommandCatalog.Filter(query)
            .Where(command => includeGit || !string.Equals(command.Group, "Git", StringComparison.Ordinal))
            .ToList();
    }
}
