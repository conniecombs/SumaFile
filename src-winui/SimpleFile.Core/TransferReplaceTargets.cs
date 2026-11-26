namespace SimpleFile.Core;

/// <summary>
/// The destinations the Replace answers of one round of conflicts already cover. A second Replace for the same
/// destination would replace the file the first one had just put there, so it is not offered: the second
/// conflict can only be answered with Keep both or Skip.
/// </summary>
public sealed class TransferReplaceTargets
{
    private readonly HashSet<string> _destinations = new(StringComparer.Ordinal);

    public bool Contains(string? destination) =>
        !string.IsNullOrWhiteSpace(destination)
        && _destinations.Contains(PathRules.NormalizeComparablePath(destination));

    public void Add(string? destination)
    {
        if (!string.IsNullOrWhiteSpace(destination))
        {
            _destinations.Add(PathRules.NormalizeComparablePath(destination));
        }
    }
}
