using System.Globalization;
using System.Text.RegularExpressions;
using SimpleFile.Ipc;

namespace SimpleFile.Core;

public sealed class AdvancedRenamePlan
{
    // Legacy compatibility for the original narrow WinUI helper.
    public string Find { get; set; } = "";
    public string Replace { get; set; } = "";
    public string Prefix { get; set; } = "";
    public string Suffix { get; set; } = "";
    public int StartNumber { get; set; }

    public bool AddEnabled { get; set; }
    public int AddIndex { get; set; } = 1;
    public string AddPosition { get; set; } = "prefix";
    public string AddString { get; set; } = "";
    public string ApplyPart { get; set; } = "full";
    public bool CapitalizeEnabled { get; set; }
    public string CapitalizeMode { get; set; } = "first";
    public string ExtensionCustom { get; set; } = "";
    public bool ExtensionEnabled { get; set; }
    public string ExtensionMode { get; set; } = "lower";
    public bool FilterCase { get; set; }
    public bool FilterEnabled { get; set; }
    public string FilterExtensions { get; set; } = "";
    public bool FilterInvert { get; set; }
    public bool FilterRegex { get; set; }
    public string FilterText { get; set; } = "";
    public bool NumberEnabled { get; set; }
    public int NumberPad { get; set; } = 3;
    public string NumberPosition { get; set; } = "suffix";
    public string NumberSeparator { get; set; } = "_";
    public int NumberStart { get; set; } = 1;
    public int NumberStep { get; set; } = 1;
    public bool RemoveCase { get; set; }
    public bool RemoveEnabled { get; set; }
    public bool RemoveRegex { get; set; }
    public string RemoveString { get; set; } = "";
    public bool ReplaceCase { get; set; }
    public bool ReplaceEnabled { get; set; }
    public string ReplaceFind { get; set; } = "";
    public bool ReplaceRegex { get; set; }
    public string ReplaceWith { get; set; } = "";
    public bool SanitizeEnabled { get; set; } = true;
    public string SanitizeReplacement { get; set; } = "_";
    public bool ScopeHidden { get; set; }
    public bool ScopeRecursive { get; set; }
    public bool SeparatorCollapse { get; set; } = true;
    public bool SeparatorEnabled { get; set; }
    public string SeparatorMode { get; set; } = "spaces-to-dashes";
    public bool TemplateEnabled { get; set; }
    public bool TemplateKeepExt { get; set; } = true;
    public string TemplatePattern { get; set; } = "{base}_{n}";
    public bool TrimCollapse { get; set; }
    public bool TrimEnabled { get; set; }
    public string TrimMode { get; set; } = "both";

    public static AdvancedRenamePlan CreateDefault() => new();
}

public sealed class AdvancedRenameTarget
{
    public FileEntry Entry { get; set; } = new();
    public int Index { get; set; }
    public string ParentPath { get; set; } = "";
}

public sealed class AdvancedRenamePreviewRow
{
    public bool Changed { get; set; }
    public string Detail { get; set; } = "";
    public string? Error { get; set; }
    public string NewName { get; set; } = "";
    public string OldName { get; set; } = "";
    public string ParentPath { get; set; } = "";
    public string Path { get; set; } = "";
}

public sealed class AdvancedRenamePreview
{
    public List<AdvancedRenamePreviewRow> AllRows { get; set; } = [];
    public int ExtraCount { get; set; }
    public int Limit { get; set; } = AdvancedRename.DefaultPreviewLimit;
    public string Message { get; set; } = "";
    public string Mode { get; set; } = "empty";
    public List<AdvancedRenamePreviewRow> Rows { get; set; } = [];
    public int TotalRows { get; set; }
    public int ChangedCount { get; set; }
    public int InvalidCount { get; set; }

    public bool HasErrors => InvalidCount > 0 || Mode == "error";
}

public static class AdvancedRename
{
    public const int DefaultPreviewLimit = 500;

    public static string Apply(string name, AdvancedRenamePlan plan, int index)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!UsesAdvancedOptions(plan))
        {
            return ApplyLegacy(name, plan, index);
        }

        return BuildName(new FileEntry { Name = name, Path = name }, index, plan);
    }

    public static RenameRequest[] Build(IReadOnlyList<FileEntry> entries, AdvancedRenamePlan plan)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(plan);
        return entries.Select((entry, index) => new RenameRequest
        {
            Path = entry.Path,
            NewName = Apply(entry.Name, plan, index),
        }).ToArray();
    }

    public static (string Base, string Ext) SplitFileName(string name)
    {
        var dotIndex = name.LastIndexOf('.');
        if (dotIndex <= 0)
        {
            return (name, "");
        }

        return (name[..dotIndex], name[(dotIndex + 1)..]);
    }

    public static string JoinFileName(string basename, string extension)
    {
        var cleanExtension = (extension ?? "").TrimStart('.');
        return cleanExtension.Length == 0 ? basename : $"{basename}.{cleanExtension}";
    }

    public static string BuildName(
        FileEntry entry,
        int index,
        AdvancedRenamePlan plan,
        DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(plan);
        var timestamp = now ?? DateTimeOffset.Now;
        var name = entry.Name;

        if (plan.TemplateEnabled)
        {
            var rendered = TemplateName(
                string.IsNullOrEmpty(plan.TemplatePattern) ? "{base}_{n}" : plan.TemplatePattern,
                entry,
                index,
                plan,
                timestamp);
            var keepExtension = plan.TemplateKeepExt;
            var (_, ext) = SplitFileName(entry.Name);
            name = keepExtension
                && ext.Length > 0
                && !rendered.EndsWith($".{ext}", StringComparison.OrdinalIgnoreCase)
                    ? JoinFileName(rendered, ext)
                    : rendered;
        }

        if (plan.RemoveEnabled)
        {
            name = TransformNamePart(name, plan, value => ReplaceWithOptions(
                value,
                plan.RemoveString,
                "",
                plan.RemoveRegex,
                plan.RemoveCase));
        }

        if (plan.ReplaceEnabled)
        {
            name = TransformNamePart(name, plan, value => ReplaceWithOptions(
                value,
                plan.ReplaceFind,
                plan.ReplaceWith,
                plan.ReplaceRegex,
                plan.ReplaceCase));
        }

        if (plan.TrimEnabled)
        {
            name = TransformNamePart(name, plan, value =>
            {
                var next = value;
                var mode = string.IsNullOrEmpty(plan.TrimMode) ? "both" : plan.TrimMode;
                if (mode is "start" or "both")
                {
                    next = Regex.Replace(next, @"^\s+", "");
                }

                if (mode is "end" or "both")
                {
                    next = Regex.Replace(next, @"\s+$", "");
                }

                if (plan.TrimCollapse)
                {
                    next = Regex.Replace(next, @"\s+", " ");
                }

                return next;
            });
        }

        if (plan.AddEnabled)
        {
            name = InsertValue(
                name,
                plan.AddString,
                string.IsNullOrEmpty(plan.AddPosition) ? "prefix" : plan.AddPosition,
                plan.AddIndex);
        }

        if (plan.CapitalizeEnabled)
        {
            name = TransformCapitalizedNamePart(name, plan);
        }

        if (plan.SeparatorEnabled)
        {
            name = TransformNamePart(name, plan, value =>
            {
                var next = value;
                switch (string.IsNullOrEmpty(plan.SeparatorMode) ? "spaces-to-dashes" : plan.SeparatorMode)
                {
                    case "spaces-to-dashes":
                        next = Regex.Replace(next, @"\s+", "-");
                        break;
                    case "spaces-to-underscores":
                        next = Regex.Replace(next, @"\s+", "_");
                        break;
                    case "underscores-to-spaces":
                        next = Regex.Replace(next, "_+", " ");
                        break;
                    case "dashes-to-spaces":
                        next = Regex.Replace(next, "-+", " ");
                        break;
                    case "dots-to-spaces":
                        next = Regex.Replace(next, @"\.+", " ");
                        break;
                }

                return plan.SeparatorCollapse
                    ? Regex.Replace(next, @"([ _.-])\1+", "$1")
                    : next;
            });
        }

        if (plan.NumberEnabled)
        {
            var start = plan.NumberStart;
            var step = Math.Max(1, plan.NumberStep);
            var width = Math.Max(1, plan.NumberPad);
            var numberText = (start + index * step).ToString(CultureInfo.InvariantCulture).PadLeft(width, '0');
            name = NumberedValue(
                name,
                numberText,
                string.IsNullOrEmpty(plan.NumberPosition) ? "suffix" : plan.NumberPosition,
                plan.NumberSeparator);
        }

        if (plan.ExtensionEnabled)
        {
            var (basename, ext) = SplitFileName(name);
            switch (string.IsNullOrEmpty(plan.ExtensionMode) ? "lower" : plan.ExtensionMode)
            {
                case "lower":
                    name = JoinFileName(basename, ext.ToLowerInvariant());
                    break;
                case "upper":
                    name = JoinFileName(basename, ext.ToUpperInvariant());
                    break;
                case "set":
                    name = JoinFileName(basename, plan.ExtensionCustom.TrimStart('.'));
                    break;
                case "remove":
                    name = basename;
                    break;
            }
        }

        if (plan.SanitizeEnabled)
        {
            name = SanitizeFileName(name, plan.SanitizeReplacement);
        }

        return name;
    }

    public static bool PassesFilter(FileEntry entry, AdvancedRenamePlan plan)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.FilterEnabled)
        {
            return true;
        }

        var filterText = plan.FilterText.Trim();
        var extensions = plan.FilterExtensions
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(value => value.TrimStart('.').ToLowerInvariant())
            .Where(value => value.Length > 0)
            .ToArray();

        var matchesName = true;
        if (filterText.Length > 0)
        {
            if (plan.FilterRegex)
            {
                matchesName = Regex.IsMatch(
                    entry.Name,
                    filterText,
                    plan.FilterCase ? RegexOptions.None : RegexOptions.IgnoreCase);
            }
            else if (plan.FilterCase)
            {
                matchesName = entry.Name.Contains(filterText, StringComparison.Ordinal);
            }
            else
            {
                matchesName = entry.Name.Contains(filterText, StringComparison.OrdinalIgnoreCase);
            }
        }

        if (plan.FilterInvert)
        {
            matchesName = !matchesName;
        }

        if (extensions.Length > 0)
        {
            var extension = ExtensionForPath(entry.Name);
            var normalizedArchiveExtension = extension.Replace("tar.gz", "gz", StringComparison.Ordinal);
            matchesName = matchesName
                && (extensions.Contains(extension) || extensions.Contains(normalizedArchiveExtension));
        }

        return matchesName;
    }

    public static AdvancedRenamePreview BuildPreview(
        IReadOnlyList<AdvancedRenameTarget> targets,
        AdvancedRenamePlan plan,
        int limit = DefaultPreviewLimit,
        DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(plan);

        try
        {
            var duplicateKeys = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var allPlans = targets
                .Where(target => PassesFilter(target.Entry, plan))
                .Select(target =>
                {
                    var newName = BuildName(target.Entry, target.Index, plan, now);
                    var key = TargetKey(target.ParentPath, newName);
                    duplicateKeys[key] = duplicateKeys.TryGetValue(key, out var count) ? count + 1 : 1;
                    return new AdvancedRenamePreviewRow
                    {
                        Changed = !string.Equals(newName, target.Entry.Name, StringComparison.Ordinal),
                        Detail = target.ParentPath,
                        NewName = newName,
                        OldName = target.Entry.Name,
                        ParentPath = target.ParentPath,
                        Path = target.Entry.Path,
                    };
                })
                .ToList();

            foreach (var row in allPlans)
            {
                var key = TargetKey(row.ParentPath, row.NewName);
                if (string.IsNullOrEmpty(row.NewName) || row.NewName is "." or "..")
                {
                    row.Error = "Invalid empty file name";
                }
                else if (!IsValidFileName(row.NewName))
                {
                    row.Error = "Invalid file name";
                }
                else if (duplicateKeys.TryGetValue(key, out var count) && count > 1)
                {
                    row.Error = "Duplicate target name";
                }
            }

            var cappedLimit = Math.Max(1, limit);
            var rows = allPlans.Take(cappedLimit).ToList();
            return new AdvancedRenamePreview
            {
                AllRows = allPlans,
                ChangedCount = allPlans.Count(row => row.Changed && row.Error is null),
                ExtraCount = Math.Max(0, allPlans.Count - rows.Count),
                InvalidCount = allPlans.Count(row => row.Error is not null),
                Limit = cappedLimit,
                Message = allPlans.Count == 0 ? "No matching files." : "",
                Mode = allPlans.Count == 0 ? "empty" : "rows",
                Rows = rows,
                TotalRows = allPlans.Count,
            };
        }
        catch (Exception exception)
            when (exception is ArgumentException or RegexParseException)
        {
            return new AdvancedRenamePreview
            {
                Limit = Math.Max(1, limit),
                Message = exception.Message,
                Mode = "error",
                Rows = [],
            };
        }
    }

    public static RenameRequest[] BuildRequests(IEnumerable<AdvancedRenamePreviewRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return rows
            .Where(row => row.Changed && row.Error is null)
            .Select(row => new RenameRequest
            {
                Path = row.Path,
                NewName = row.NewName,
            })
            .ToArray();
    }

    public static async Task<IReadOnlyList<AdvancedRenameTarget>> CollectTargetsAsync(
        IEnumerable<FileEntry> selectedEntries,
        string currentPath,
        AdvancedRenamePlan plan,
        Func<string, CancellationToken, Task<DirectoryListing>> listDirectoryAsync,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selectedEntries);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(listDirectoryAsync);

        var targets = new List<AdvancedRenameTarget>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        async Task AddEntryAsync(FileEntry entry)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (seen.Contains(entry.Path))
            {
                return;
            }

            if (!plan.ScopeHidden && entry.Name.StartsWith(".", StringComparison.Ordinal))
            {
                return;
            }

            seen.Add(entry.Path);
            targets.Add(new AdvancedRenameTarget
            {
                Entry = entry,
                Index = targets.Count,
                ParentPath = PathRules.GetParentPath(entry.Path) ?? currentPath,
            });

            if (!plan.ScopeRecursive || !entry.IsDir)
            {
                return;
            }

            try
            {
                var listing = await listDirectoryAsync(entry.Path, cancellationToken).ConfigureAwait(false);
                foreach (var child in listing.Entries)
                {
                    await AddEntryAsync(child).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Keep the dialog usable when one subtree cannot be read.
            }
        }

        foreach (var entry in selectedEntries)
        {
            await AddEntryAsync(entry).ConfigureAwait(false);
        }

        return targets;
    }

    public static string TransformNamePart(
        string name,
        AdvancedRenamePlan plan,
        Func<string, string> transform)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(transform);
        var (basename, ext) = SplitFileName(name);

        return plan.ApplyPart switch
        {
            "base" => JoinFileName(transform(basename), ext),
            "extension" => JoinFileName(basename, transform(ext).TrimStart('.')),
            _ => transform(name),
        };
    }

    public static string ReplaceWithOptions(
        string value,
        string find,
        string replacement,
        bool regex,
        bool caseSensitive)
    {
        if (string.IsNullOrEmpty(find))
        {
            return value;
        }

        var options = caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase;
        if (regex)
        {
            return Regex.Replace(value, find, replacement ?? "", options);
        }

        return caseSensitive
            ? value.Replace(find, replacement ?? "", StringComparison.Ordinal)
            : Regex.Replace(value, Regex.Escape(find), _ => replacement ?? "", options);
    }

    public static string CapitalizeValue(string value, string mode)
    {
        switch (mode)
        {
            case "upper":
                return value.ToUpperInvariant();
            case "lower":
                return value.ToLowerInvariant();
            case "words":
            case "title":
                return CapitalizeEachWord(value);
            case "sentence":
                var lower = value.ToLowerInvariant();
                return lower.Length == 0
                    ? lower
                    : char.ToUpperInvariant(lower[0]) + lower[1..];
            default:
                return value.Length == 0
                    ? value
                    : char.ToUpperInvariant(value[0]) + value[1..];
        }
    }

    private static string TransformCapitalizedNamePart(string name, AdvancedRenamePlan plan)
    {
        var mode = string.IsNullOrEmpty(plan.CapitalizeMode) ? "first" : plan.CapitalizeMode;
        if (ShouldPreserveExtensionForCapitalization(plan.ApplyPart, mode))
        {
            var (basename, ext) = SplitFileName(name);
            return JoinFileName(CapitalizeValue(basename, mode), ext);
        }

        return TransformNamePart(name, plan, value => CapitalizeValue(value, mode));
    }

    private static bool ShouldPreserveExtensionForCapitalization(string applyPart, string mode)
    {
        return (string.IsNullOrEmpty(applyPart) || string.Equals(applyPart, "full", StringComparison.Ordinal))
            && mode is "first" or "words" or "title" or "sentence";
    }

    private static string CapitalizeEachWord(string value)
    {
        if (value.Length == 0)
        {
            return value;
        }

        var lower = value.ToLowerInvariant();
        return Regex.Replace(
            lower,
            @"(^|[^\p{L}\p{Nd}'])(\p{L})",
            match => match.Groups[1].Value + match.Groups[2].Value.ToUpperInvariant());
    }

    public static string InsertValue(string name, string value, string position, int indexValue)
    {
        if (string.IsNullOrEmpty(value))
        {
            return name;
        }

        var (basename, ext) = SplitFileName(name);
        return position switch
        {
            "prefix" => $"{value}{name}",
            "suffix" => $"{name}{value}",
            "before-ext" => JoinFileName($"{basename}{value}", ext),
            _ => name.Insert(Math.Max(0, Math.Min(name.Length, indexValue)), value),
        };
    }

    public static string NumberedValue(string name, string numberText, string position, string separator)
    {
        var (basename, ext) = SplitFileName(name);
        var cleanSeparator = separator ?? "";
        return position switch
        {
            "replace" => JoinFileName(numberText, ext),
            "prefix" => $"{numberText}{cleanSeparator}{name}",
            "suffix" => $"{name}{cleanSeparator}{numberText}",
            _ => JoinFileName($"{basename}{cleanSeparator}{numberText}", ext),
        };
    }

    public static string SanitizeFileName(string name, string replacement)
    {
        var safeReplacement = string.IsNullOrEmpty(replacement) ? "_" : replacement;
        return Regex
            .Replace(name, "[<>:\"/\\\\|?*\u0000-\u001F]", _ => safeReplacement)
            .Trim();
    }

    public static string TemplateName(
        string pattern,
        FileEntry entry,
        int index,
        AdvancedRenamePlan plan,
        DateTimeOffset? now = null)
    {
        var timestamp = now ?? DateTimeOffset.Now;
        var (basename, ext) = SplitFileName(entry.Name);
        var parent = PathRules.Basename(PathRules.GetParentPath(entry.Path) ?? "");
        var start = plan.NumberStart;
        var step = Math.Max(1, plan.NumberStep);
        var width = Math.Max(1, plan.NumberPad);
        var number = (start + index * step).ToString(CultureInfo.InvariantCulture).PadLeft(width, '0');

        var replacements = new (string Token, string Value)[]
        {
            ("{base}", basename),
            ("{ext}", ext),
            ("{name}", entry.Name),
            ("{parent}", parent),
            ("{n}", number),
            ("{yyyy}", timestamp.Year.ToString("0000", CultureInfo.InvariantCulture)),
            ("{mm}", timestamp.Month.ToString("00", CultureInfo.InvariantCulture)),
            ("{dd}", timestamp.Day.ToString("00", CultureInfo.InvariantCulture)),
            ("{hh}", timestamp.Hour.ToString("00", CultureInfo.InvariantCulture)),
            ("{min}", timestamp.Minute.ToString("00", CultureInfo.InvariantCulture)),
            ("{ss}", timestamp.Second.ToString("00", CultureInfo.InvariantCulture)),
            ("{date}", $"{timestamp.Year:0000}-{timestamp.Month:00}-{timestamp.Day:00}"),
            ("{time}", $"{timestamp.Hour:00}{timestamp.Minute:00}{timestamp.Second:00}"),
        };

        var rendered = pattern;
        foreach (var (token, value) in replacements)
        {
            rendered = rendered.Replace(token, value, StringComparison.Ordinal);
        }

        return rendered;
    }

    public static bool IsValidFileName(string name)
    {
        var trimmed = name.Trim();
        var baseName = name.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
        return trimmed.Length > 0
            && !Regex.IsMatch(trimmed, "[<>:\"/\\\\|?*\u0000-\u001F]")
            && trimmed is not "." and not ".."
            && !Regex.IsMatch(name, "[ .]$")
            && !IsReservedWindowsDeviceName(baseName);
    }

    public static string ExtensionForPath(string path)
    {
        var name = PathRules.Basename(path).ToLowerInvariant();
        if (name.EndsWith(".tar.gz", StringComparison.Ordinal))
        {
            return "tar.gz";
        }

        var dotIndex = name.LastIndexOf('.');
        return dotIndex >= 0 ? name[(dotIndex + 1)..] : "";
    }

    private static string ApplyLegacy(string name, AdvancedRenamePlan plan, int index)
    {
        var ext = "";
        var stem = name;
        var dot = name.LastIndexOf('.');
        if (dot > 0)
        {
            ext = name[dot..];
            stem = name[..dot];
        }

        if (!string.IsNullOrEmpty(plan.Find))
        {
            stem = stem.Replace(plan.Find, plan.Replace ?? "", StringComparison.OrdinalIgnoreCase);
        }

        var numbered = plan.StartNumber > 0 ? $"{plan.StartNumber + index}" : "";
        return $"{plan.Prefix}{stem}{plan.Suffix}{numbered}{ext}";
    }

    private static bool UsesAdvancedOptions(AdvancedRenamePlan plan)
    {
        return plan.AddEnabled
            || plan.CapitalizeEnabled
            || plan.ExtensionEnabled
            || plan.FilterEnabled
            || plan.NumberEnabled
            || plan.RemoveEnabled
            || plan.ReplaceEnabled
            || plan.ScopeHidden
            || plan.ScopeRecursive
            || plan.SeparatorEnabled
            || plan.TemplateEnabled
            || plan.TrimEnabled
            || !plan.SanitizeEnabled
            || !string.Equals(plan.SanitizeReplacement, "_", StringComparison.Ordinal);
    }

    private static string TargetKey(string parentPath, string newName)
    {
        return $"{parentPath.ToLowerInvariant()}\0{newName.ToLowerInvariant()}";
    }

    private static bool IsReservedWindowsDeviceName(string baseName)
    {
        return baseName is "CON" or "PRN" or "AUX" or "NUL"
            || Regex.IsMatch(baseName, "^COM[1-9]$", RegexOptions.IgnoreCase)
            || Regex.IsMatch(baseName, "^LPT[1-9]$", RegexOptions.IgnoreCase);
    }
}
