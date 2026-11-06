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

public sealed record OmnibarIntent(OmnibarIntentKind Kind, string Value, string? StatusMessage = null)
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
    public static OmnibarIntent Parse(string? text, OmnibarMode mode, string? currentPath = null,
        Func<string, bool>? directoryExists = null)
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

        // "~name" filters; "~\folder" and "~/folder" are home-relative paths.
        if (value.StartsWith("~", StringComparison.Ordinal)
            && !(value.Length > 1 && value[1] is '\\' or '/'))
        {
            return new OmnibarIntent(OmnibarIntentKind.Filter, value[1..].Trim());
        }

        return mode switch
        {
            OmnibarMode.Search => new OmnibarIntent(OmnibarIntentKind.Search, value),
            OmnibarMode.Filter => new OmnibarIntent(OmnibarIntentKind.Filter, value),
            OmnibarMode.Command => new OmnibarIntent(OmnibarIntentKind.Command, value),
            _ => ParseNavigate(value, path, directoryExists),
        };
    }

    private static OmnibarIntent ParseNavigate(string value, string currentPath, Func<string, bool>? directoryExists)
    {
        // A bare drive ("C:") would resolve against that drive's working directory; mean its root.
        if (value.Length == 2 && char.IsAsciiLetter(value[0]) && value[1] == ':')
        {
            return new OmnibarIntent(OmnibarIntentKind.Navigate, char.ToUpperInvariant(value[0]) + @":\");
        }

        if (directoryExists is null || IsRootedOrExplicit(value) || currentPath.Length == 0)
        {
            return new OmnibarIntent(OmnibarIntentKind.Navigate, value);
        }

        var candidate = Path.Combine(currentPath, value);
        if (directoryExists(candidate))
        {
            return new OmnibarIntent(OmnibarIntentKind.Navigate, candidate);
        }

        return new OmnibarIntent(
            OmnibarIntentKind.Filter,
            value,
            $"No folder named '{value}' here. Filtering instead.");
    }

    private static bool IsRootedOrExplicit(string value)
    {
        return value is "." or ".."
            || value.StartsWith(@".\", StringComparison.Ordinal)
            || value.StartsWith("./", StringComparison.Ordinal)
            || value.StartsWith(@"..\", StringComparison.Ordinal)
            || value.StartsWith("../", StringComparison.Ordinal)
            || value[0] is '\\' or '/' or '~'
            || (value.Length >= 2 && char.IsAsciiLetter(value[0]) && value[1] == ':');
    }

    public static IReadOnlyList<AppCommand> FilterCommands(string? query, bool includeGit)
    {
        return AppCommandCatalog.Filter(query)
            .Where(command => includeGit || !string.Equals(command.Group, "Git", StringComparison.Ordinal))
            .ToList();
    }
}
