using Microsoft.UI.Xaml;
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

    /// <summary>
    /// Runs <paramref name="work"/> behind a modal "working" dialog with a progress ring, a
    /// status line the work can update, and a Cancel button that cancels the token. Returns the
    /// work's result; throws <see cref="OperationCanceledException"/> when the user cancels.
    /// </summary>
    public static async Task<T> RunWithProgressAsync<T>(
        XamlRoot xamlRoot,
        string title,
        string detail,
        Func<IProgress<string>, CancellationToken, Task<T>> work,
        CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var status = new TextBlock { Text = detail, TextWrapping = TextWrapping.Wrap, MaxWidth = 420 };
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    new ProgressRing { IsActive = true, Width = 32, Height = 32, HorizontalAlignment = HorizontalAlignment.Left },
                    status,
                },
            },
            CloseButtonText = "Cancel",
            XamlRoot = xamlRoot,
        };
        var progress = new Progress<string>(message => status.Text = message);
        var completed = false;
        var workTask = work(progress, cts.Token);
        dialog.Opened += (_, _) =>
        {
            if (completed)
            {
                dialog.Hide();
            }
        };
        _ = workTask.ContinueWith(
            _ => dialog.DispatcherQueue.TryEnqueue(() =>
            {
                completed = true;
                dialog.Hide();
            }),
            TaskScheduler.Default);

        if (!workTask.IsCompleted)
        {
            await ShowAsync(dialog, cancellationToken);
        }

        if (!workTask.IsCompleted)
        {
            // The user pressed Cancel (or the dialog was dismissed) before the work finished.
            cts.Cancel();
        }

        return await workTask;
    }

    public static async Task RunWithProgressAsync(
        XamlRoot xamlRoot,
        string title,
        string detail,
        Func<IProgress<string>, CancellationToken, Task> work,
        CancellationToken cancellationToken = default)
    {
        await RunWithProgressAsync<bool>(
            xamlRoot,
            title,
            detail,
            async (progress, token) =>
            {
                await work(progress, token);
                return true;
            },
            cancellationToken);
    }
}
