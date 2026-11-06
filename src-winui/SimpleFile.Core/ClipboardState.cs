namespace SimpleFile.Core;

public enum ClipboardOperation
{
    Copy,
    Cut,
}

public sealed class ClipboardState
{
    public ClipboardOperation Operation { get; private set; }
    public string[] SourcePaths { get; private set; } = [];
    public bool HasItems => SourcePaths.Length > 0;

    /// <summary>
    /// Identifies the export of this state to the system clipboard. When the system clipboard
    /// no longer carries this stamp, another application has replaced its contents and the
    /// internal state must not shadow it.
    /// </summary>
    public string Stamp { get; private set; } = "";

    public void SetCopy(string[] paths)
    {
        Operation = ClipboardOperation.Copy;
        SourcePaths = paths;
        Stamp = Guid.NewGuid().ToString("N");
    }

    public void SetCut(string[] paths)
    {
        Operation = ClipboardOperation.Cut;
        SourcePaths = paths;
        Stamp = Guid.NewGuid().ToString("N");
    }

    /// <summary>
    /// Drops the internal items when the system clipboard stamp is missing or belongs to a
    /// different export, so a later copy in another application wins.
    /// </summary>
    public void ReconcileWith(string? systemClipboardStamp)
    {
        if (!HasItems)
        {
            return;
        }

        if (!string.Equals(systemClipboardStamp, Stamp, StringComparison.Ordinal))
        {
            Clear();
        }
    }

    public void Clear()
    {
        SourcePaths = [];
    }
}
