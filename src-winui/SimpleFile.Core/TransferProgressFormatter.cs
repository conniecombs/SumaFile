using System;
using System.Globalization;
using System.IO;
using SimpleFile.Ipc;

namespace SimpleFile.Core;

public sealed record TransferProgressContext(
    bool Move,
    int ItemCount,
    string Source,
    string Destination);

public sealed record TransferProgressDisplay(
    string Title,
    string Summary,
    string Percent,
    double ProgressPercent,
    bool IsIndeterminate,
    string FileSummary,
    string FileRate,
    double FileProgressPercent,
    bool FileProgressIsIndeterminate,
    string CurrentItemName,
    string CurrentItemPath,
    string From,
    string To,
    string Speed,
    string Eta);

public static class TransferProgressFormatter
{
    public static TransferProgressDisplay Format(
        TransferProgressContext context,
        ProgressUpdate update,
        double? bytesPerSecond,
        double? averageFilesPerSecond)
    {
        var current = Math.Min(update.Current, update.Total > 0 ? update.Total : update.Current);
        var progressPercent = Percent(current, update.Total);
        var isIndeterminate = update.Total == 0 && update.Status == "running";
        var currentFiles = Math.Min(update.CurrentFiles, update.TotalFiles > 0 ? update.TotalFiles : update.CurrentFiles);
        var fileProgressPercent = Percent(currentFiles, update.TotalFiles);
        var fileProgressIsIndeterminate = update.TotalFiles == 0 && update.Status == "running";
        var currentItemName = CurrentItemName(update.CurrentItem, update.Status);
        var summary = Summary(update, current);
        var speed = Speed(update, bytesPerSecond);
        var eta = Eta(update, current, bytesPerSecond);

        return new TransferProgressDisplay(
            Title(context, update),
            summary,
            update.Total > 0 ? $"{progressPercent:0}%" : "",
            progressPercent,
            isIndeterminate,
            FileSummary(update, currentFiles),
            FileRate(update, averageFilesPerSecond),
            fileProgressPercent,
            fileProgressIsIndeterminate,
            currentItemName,
            update.CurrentItem,
            LabelValue("From", context.Source),
            LabelValue("To", context.Destination),
            speed,
            eta);
    }

    public static double Percent(ulong current, ulong total)
    {
        if (total == 0)
        {
            return 0;
        }

        return Math.Clamp((double)current / total * 100, 0, 100);
    }

    public static string FormatBytes(ulong bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{bytes} B"
            : $"{value:0.##} {units[unit]}";
    }

    private static string Title(TransferProgressContext context, ProgressUpdate update)
    {
        var noun = context.ItemCount == 1 ? "item" : "items";
        return update.Status switch
        {
            "completed" => context.Move ? "Move complete" : "Copy complete",
            "cancelled" => context.Move ? "Move cancelled" : "Copy cancelled",
            "error" => context.Move ? "Move failed" : "Copy failed",
            _ => $"{(context.Move ? "Moving" : "Copying")} {Math.Max(context.ItemCount, 1)} {noun}",
        };
    }

    private static string Summary(ProgressUpdate update, ulong current)
    {
        if (update.Status == "error" && !string.IsNullOrWhiteSpace(update.Error))
        {
            return update.Error;
        }

        if (update.Total > 0)
        {
            return $"{FormatBytes(current)} of {FormatBytes(update.Total)}";
        }

        if (update.Status == "running")
        {
            return current > 0 ? $"{FormatBytes(current)} transferred" : "Calculating size";
        }

        return $"{FormatBytes(current)} transferred";
    }

    private static string FileSummary(ProgressUpdate update, ulong currentFiles)
    {
        if (update.TotalFiles > 0)
        {
            return $"{Count(currentFiles)} of {Count(update.TotalFiles)} files";
        }

        if (update.Status == "running")
        {
            return currentFiles > 0 ? $"{Count(currentFiles)} files transferred" : "Counting files";
        }

        return $"{Count(currentFiles)} files";
    }

    private static string CurrentItemName(string currentItem, string status)
    {
        if (string.IsNullOrWhiteSpace(currentItem))
        {
            return status switch
            {
                "completed" => "Transfer complete",
                "cancelled" => "Transfer cancelled",
                "error" => "Transfer failed",
                _ => "Preparing transfer",
            };
        }

        var trimmed = currentItem.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrWhiteSpace(name) ? currentItem : name;
    }

    private static string Speed(ProgressUpdate update, double? bytesPerSecond)
    {
        if (update.Status != "running")
        {
            return update.Status switch
            {
                "completed" => "Complete",
                "cancelled" => "Cancelled",
                "error" => "Stopped",
                _ => "",
            };
        }

        return bytesPerSecond is > 0
            ? $"{FormatBytes((ulong)bytesPerSecond.Value)}/s"
            : "Calculating speed";
    }

    private static string FileRate(ProgressUpdate update, double? averageFilesPerSecond)
    {
        if (averageFilesPerSecond is > 0 && update.Status is "running" or "completed")
        {
            return $"{FormatRate(averageFilesPerSecond.Value)} files/s avg";
        }

        if (update.Status != "running")
        {
            return update.Status switch
            {
                "completed" => "Files complete",
                "cancelled" => "Files stopped",
                "error" => "Files stopped",
                _ => "",
            };
        }

        return "Calculating file rate";
    }

    private static string Eta(ProgressUpdate update, ulong current, double? bytesPerSecond)
    {
        if (update.Status != "running")
        {
            return update.Status switch
            {
                "completed" => "Done",
                "cancelled" => "Stopped",
                "error" => "Stopped",
                _ => "",
            };
        }

        if (update.Total == 0 || bytesPerSecond is not > 0 || current >= update.Total)
        {
            return "Estimating time";
        }

        var seconds = (update.Total - current) / bytesPerSecond.Value;
        return $"{FormatDuration(TimeSpan.FromSeconds(seconds))} remaining";
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalHours >= 1)
        {
            return $"{(int)duration.TotalHours}h {duration.Minutes}m";
        }

        if (duration.TotalMinutes >= 1)
        {
            return $"{(int)duration.TotalMinutes}m {duration.Seconds}s";
        }

        return $"{Math.Max(1, (int)Math.Ceiling(duration.TotalSeconds))}s";
    }

    private static string FormatRate(double value) =>
        value < 10 ? value.ToString("0.0", CultureInfo.InvariantCulture) : value.ToString("0", CultureInfo.InvariantCulture);

    private static string Count(ulong value) =>
        value.ToString("N0", CultureInfo.InvariantCulture);

    private static string LabelValue(string label, string value) =>
        string.IsNullOrWhiteSpace(value) ? $"{label}: -" : $"{label}: {value}";
}
