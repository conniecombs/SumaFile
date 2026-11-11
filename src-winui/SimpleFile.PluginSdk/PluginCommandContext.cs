namespace SimpleFile.PluginSdk;

/// <summary>What the host passes to <see cref="ICommandHandler.ExecuteAsync"/>: the command and the workspace when it ran.</summary>
/// <param name="CommandId">
/// The host's id of the command, <c>plugin:&lt;plugin id&gt;:&lt;command id&gt;</c>, where the command id is the one
/// declared in <c>contributions.commands</c>.
/// </param>
/// <param name="ActivePath">The active pane's folder, or null when there is none.</param>
/// <param name="SelectedPaths">The full paths of the active pane's selected items, possibly empty.</param>
public sealed record PluginCommandContext(string CommandId, string? ActivePath, IReadOnlyList<string> SelectedPaths);
