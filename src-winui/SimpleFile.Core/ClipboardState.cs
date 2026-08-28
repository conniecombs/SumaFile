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

    public void SetCopy(string[] paths)
    {
        Operation = ClipboardOperation.Copy;
        SourcePaths = paths;
    }

    public void SetCut(string[] paths)
    {
        Operation = ClipboardOperation.Cut;
        SourcePaths = paths;
    }

    public void Clear()
    {
        SourcePaths = [];
    }
}
