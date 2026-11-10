using System.Runtime.CompilerServices;

namespace SimpleFile.Core;

/// <summary>
/// Remembers the roots (a window's <c>XamlRoot</c>) whose window closed, without keeping them alive, so a dialog that
/// waited at the app-wide gate while its window closed is never shown on the dead root. Thread-safe.
/// </summary>
/// <typeparam name="TRoot">The root type; compared by reference.</typeparam>
public sealed class ClosedRootRegistry<TRoot>
    where TRoot : class
{
    private static readonly object Marker = new();
    private readonly ConditionalWeakTable<TRoot, object> _closed = new();

    /// <summary>Records <paramref name="root"/> as closed. A null root is ignored.</summary>
    public void MarkClosed(TRoot? root)
    {
        if (root is not null)
        {
            _closed.AddOrUpdate(root, Marker);
        }
    }

    /// <summary>
    /// False when a dialog must not be shown on <paramref name="root"/>: the root is null (its window is gone) or was
    /// recorded closed.
    /// </summary>
    public bool CanShowOn(TRoot? root) => root is not null && !_closed.TryGetValue(root, out _);
}
