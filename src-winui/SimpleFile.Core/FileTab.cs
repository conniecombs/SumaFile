namespace SimpleFile.Core;

/// <summary>
/// Pane-local tab. Shape matches frontend/src/lib/appState.ts FileTab.
/// </summary>
public sealed class FileTab
{
    public string Id { get; set; } = "";
    public string Path { get; set; } = "";
    public string Title { get; set; } = "";
    public List<string> History { get; set; } = [];
    public int HistoryIndex { get; set; }

    public FileTab Clone()
    {
        return new FileTab
        {
            Id = Id,
            Path = Path,
            Title = Title,
            History = [.. History],
            HistoryIndex = HistoryIndex,
        };
    }
}
