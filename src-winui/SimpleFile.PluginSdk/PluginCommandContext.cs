namespace SimpleFile.PluginSdk;

public sealed record PluginCommandContext(string CommandId, string? ActivePath, IReadOnlyList<string> SelectedPaths);
