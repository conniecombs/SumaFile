using SimpleFile.Ipc;
using DriveInfo = SimpleFile.Ipc.DriveInfo;

namespace SimpleFile.Core;

/// <summary>
/// Drive labels for the WinUI sidebar.
/// </summary>
public static class DrivePresentation
{
    public static string Icon(DriveInfo drive)
    {
        return (drive.DriveType ?? "").ToLowerInvariant() switch
        {
            "network" => "\uE968",
            "removable" => "\uE88E",
            "cd-rom" or "optical" => "\uE958",
            "ram disk" => "\uE964",
            _ => "\uEDA2",
        };
    }

    public static string Status(DriveInfo drive)
    {
        var status = (drive.DriveStatus ?? "available").ToLowerInvariant();
        return status is "available" or "offline" or "stale" or "unknown" ? status : "unknown";
    }

    public static string Badge(DriveInfo drive)
    {
        return Status(drive) switch
        {
            "offline" => "Offline",
            "stale" => "Stale",
            "unknown" => "Unknown",
            _ => "",
        };
    }

    public static string Description(DriveInfo drive)
    {
        var status = Status(drive);
        var type = (drive.DriveType ?? "").ToLowerInvariant();
        if (status == "offline")
        {
            var detail = (drive.StatusDetail ?? "").Trim();
            if (detail.Contains("timed out", StringComparison.OrdinalIgnoreCase))
            {
                return "Timed out · Retry to reconnect";
            }

            if (detail.Contains("access was denied", StringComparison.OrdinalIgnoreCase))
            {
                return "Access denied · Check credentials";
            }

            if (detail.Contains("not ready", StringComparison.OrdinalIgnoreCase))
            {
                return "Not ready · Open to reconnect";
            }

            return string.IsNullOrEmpty(drive.RemotePath)
                ? "Offline · Retry to reconnect"
                : $"Offline · {drive.RemotePath}";
        }

        if (status == "stale")
        {
            return "Stale mapping · Remap or remove";
        }

        if (type == "network")
        {
            return drive.RemotePath ?? drive.StatusDetail ?? "Network share";
        }

        return "";
    }

    public static bool IsAvailable(DriveInfo drive)
    {
        return Status(drive) == "available";
    }

    public static bool IsNetwork(DriveInfo drive)
    {
        return PathRules.IsNetworkOrRemoteLikeDrive(drive);
    }

    public static DriveInfo? FindDriveForPath(string path, IReadOnlyList<DriveInfo> drives)
    {
        return drives.FirstOrDefault(drive => PathRules.PathContains(drive.Path, path)
            || PathRules.PathsEqual(drive.Path, path));
    }
}
