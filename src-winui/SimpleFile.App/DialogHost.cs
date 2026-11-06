using Microsoft.UI.Xaml.Controls;

namespace SimpleFile.App;

/// <summary>
/// Serializes <see cref="ContentDialog"/> presentation. WinUI allows one open ContentDialog per
/// XamlRoot and throws on a second <c>ShowAsync</c>; routing every dialog through this gate makes a
/// second request wait for the first to close instead of failing (or, from an unguarded handler,
/// terminating the process).
/// </summary>
internal static class DialogHost
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static int _openCount;

    /// <summary>True while any dialog shown through the host is open or waiting to open.</summary>
    public static bool IsDialogOpen => Volatile.Read(ref _openCount) > 0;

    public static async Task<ContentDialogResult> ShowAsync(
        ContentDialog dialog,
        CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken);
        Interlocked.Increment(ref _openCount);
        try
        {
            return await dialog.ShowAsync();
        }
        finally
        {
            Interlocked.Decrement(ref _openCount);
            Gate.Release();
        }
    }
}
