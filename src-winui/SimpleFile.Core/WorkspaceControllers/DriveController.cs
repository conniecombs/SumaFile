using DriveInfo = SimpleFile.Ipc.DriveInfo;

namespace SimpleFile.Core;

internal sealed class DriveController
{
    private List<DriveInfo> _drives = [];

    public IReadOnlyList<DriveInfo> Drives => _drives;
    public DriveInfo? PendingReconnect { get; private set; }
    public PaneId PendingReconnectPane { get; private set; } = PaneId.Primary;

    public string? ApplyDrives(
        IReadOnlyList<DriveInfo> drives,
        string fallbackBasePath,
        bool quiet)
    {
        ArgumentNullException.ThrowIfNull(drives);
        if (drives.Count > 0)
        {
            _drives = [.. drives];
        }
        else
        {
            var fallback = PathRules.CreateFallbackDriveForPath(fallbackBasePath);
            _drives = fallback is null ? [] : [fallback];
        }

        if (quiet)
        {
            return null;
        }

        var offline = _drives.Count(drive =>
        {
            var status = DrivePresentation.Status(drive);
            return status is "offline" or "stale";
        });
        return offline > 0
            ? $"Drives refreshed · {offline} network mapping{(offline == 1 ? "" : "s")} need attention"
            : "Drives refreshed";
    }

    public bool TryApplyDrive(
        string path,
        IReadOnlyList<DriveInfo> drives,
        bool quiet,
        out string? statusMessage)
    {
        ArgumentNullException.ThrowIfNull(drives);
        statusMessage = null;
        var drive = drives.FirstOrDefault(candidate => PathRules.PathsEqual(candidate.Path, path))
            ?? drives.FirstOrDefault();
        if (drive is null)
        {
            return false;
        }

        var next = _drives.ToList();
        var existingIndex = next.FindIndex(candidate =>
            PathRules.PathsEqual(candidate.Path, drive.Path)
            || PathRules.PathsEqual(candidate.Path, path));
        if (existingIndex >= 0)
        {
            next[existingIndex] = drive;
        }
        else
        {
            next.Add(drive);
            next.Sort((left, right) => string.Compare(left.Path, right.Path, StringComparison.OrdinalIgnoreCase));
        }

        _drives = next;
        if (!quiet)
        {
            statusMessage = DrivePresentation.IsAvailable(drive)
                ? $"{drive.Name} refreshed"
                : drive.StatusDetail ?? $"{drive.Name} is not available";
        }

        return true;
    }

    public DriveInfo? FindDriveForPath(string path)
        => DrivePresentation.FindDriveForPath(path, _drives);

    public bool IsNetworkFsPath(string path)
        => PathRules.IsNetworkFsPath(path, _drives);

    public void BeginPendingReconnect(DriveInfo drive, PaneId pane)
    {
        PendingReconnect = drive;
        PendingReconnectPane = pane;
    }

    public (DriveInfo? Drive, PaneId Pane) TakePendingReconnect()
    {
        var pending = PendingReconnect;
        var pane = PendingReconnectPane;
        PendingReconnect = null;
        return (pending, pane);
    }

    public void ClearPendingReconnect()
    {
        PendingReconnect = null;
    }
}
