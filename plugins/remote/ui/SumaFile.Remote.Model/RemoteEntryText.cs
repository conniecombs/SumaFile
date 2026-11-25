using System.Globalization;

namespace SumaFile.Remote.Model;

/// <summary>
/// The text of a row's Size and Modified cells in the manager's local and remote lists, written the way the app's
/// own file panes write them (the plugin cannot reference the app's formatter).
/// </summary>
public static class RemoteEntryText
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

    /// <summary>A file's size; a folder shows none, as its size is not known from a listing.</summary>
    public static string Size(RemoteFileEntry entry, CultureInfo? culture = null)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry.IsDir ? "" : Size(entry.Size, culture);
    }

    /// <summary><c>512 B</c>, <c>1.5 KB</c>, <c>2.0 MB</c>: whole bytes, then one decimal in units of 1024.</summary>
    public static string Size(ulong bytes, CultureInfo? culture = null)
    {
        double size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < Units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{bytes} {Units[0]}"
            : $"{size.ToString("0.0", culture ?? CultureInfo.CurrentCulture)} {Units[unit]}";
    }

    /// <summary>
    /// A listing's RFC 3339 time as a short local date and time. Text that is not a time is shown as it is, and a
    /// missing time shows nothing.
    /// </summary>
    public static string Modified(string? value, TimeZoneInfo? zone = null, CultureInfo? culture = null)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time))
        {
            return value;
        }

        return TimeZoneInfo.ConvertTime(time, zone ?? TimeZoneInfo.Local).ToString("g", culture ?? CultureInfo.CurrentCulture);
    }
}
