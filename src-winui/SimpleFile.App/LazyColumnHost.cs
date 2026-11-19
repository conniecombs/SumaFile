using SimpleFile.Core;

namespace SimpleFile.App;

/// <summary>Where a row's "…" cell sends its click: the window wires it to <see cref="ExplorerWorkspace.RequestFact"/>.</summary>
internal static class LazyColumnHost
{
    private static Action<PaneId, string, string>? _requestFact;

    public static void Configure(Action<PaneId, string, string>? requestFact) => _requestFact = requestFact;

    /// <summary>Reads one lazy column value for one row now.</summary>
    public static void RequestFact(PaneId pane, string path, string kind) => _requestFact?.Invoke(pane, path, kind);
}
