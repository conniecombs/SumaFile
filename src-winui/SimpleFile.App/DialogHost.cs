using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SimpleFile.Core;

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
    private static readonly List<ShownDialog> Shown = [];

    // The roots HideDialogsOn was called for: their windows are closing. A dialog that waited at the gate meanwhile is
    // never shown on one (DialogHostTests).
    private static readonly ClosedRootRegistry<XamlRoot> ClosedRoots = new ClosedRootRegistry<XamlRoot>();
    private static int _openCount;

    /// <summary>True while any dialog shown through the host is open or waiting to open.</summary>
    public static bool IsDialogOpen => Volatile.Read(ref _openCount) > 0;

    /// <summary>
    /// Shows <paramref name="dialog"/> once no other dialog shown through the host is open. Returns
    /// <see cref="ContentDialogResult.None"/> without showing it when, by the time it gets the gate, its root is gone or
    /// its window closed (<see cref="HideDialogsOn"/> recorded the root), so a dialog queued for a closed window never
    /// opens on the dead root and never holds the gate.
    /// </summary>
    public static async Task<ContentDialogResult> ShowAsync(
        ContentDialog dialog,
        CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken);
        Interlocked.Increment(ref _openCount);
        var shown = new ShownDialog(dialog);
        lock (Shown)
        {
            Shown.Add(shown);
        }

        try
        {
            if (!ClosedRoots.CanShowOn(RootOf(dialog)))
            {
                return ContentDialogResult.None;
            }

            var showing = dialog.ShowAsync().AsTask();
            // A dialog on a window that closes may never complete its ShowAsync. HideDialogsOn completes Hidden, so the
            // gate is released either way.
            if (await Task.WhenAny(showing, shown.Hidden.Task) == showing)
            {
                return await showing;
            }

            _ = showing.ContinueWith(
                task => _ = task.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return ContentDialogResult.None;
        }
        finally
        {
            lock (Shown)
            {
                Shown.Remove(shown);
            }

            Interlocked.Decrement(ref _openCount);
            Gate.Release();
        }
    }

    /// <summary>
    /// Hides every dialog shown through the host on <paramref name="root"/>, and releases its <see cref="ShowAsync"/>
    /// even if WinUI never completes the dialog's own <c>ShowAsync</c> (its window is closing). Runs on the UI thread;
    /// returns how many dialogs it hid. The plugin window host calls it before it closes a plugin window. It also
    /// records <paramref name="root"/> as closed, so a dialog still waiting at the gate for that window is not shown.
    /// </summary>
    internal static int HideDialogsOn(XamlRoot? root)
    {
        if (root is null)
        {
            return 0;
        }

        ClosedRoots.MarkClosed(root);
        ShownDialog[] candidates;
        lock (Shown)
        {
            candidates = [.. Shown];
        }

        var hidden = 0;
        foreach (var shown in candidates)
        {
            if (!IsOn(shown.Dialog, root))
            {
                continue;
            }

            hidden++;
            try
            {
                shown.Dialog.Hide();
            }
            catch (Exception)
            {
                // The window is already going: the dialog is not showing any more, and Hidden releases its ShowAsync.
            }

            shown.Hidden.TrySetResult();
        }

        return hidden;
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
        // Both hides may run after the dialog's window closed; Hide on a dialog whose XamlRoot is gone throws, and from
        // these callbacks nothing would catch it.
        dialog.Opened += (_, _) =>
        {
            if (!completed || dialog.XamlRoot is null)
            {
                return;
            }

            try
            {
                dialog.Hide();
            }
            catch (Exception)
            {
                // Its window is going: the dialog is not showing any more.
            }
        };
        _ = workTask.ContinueWith(
            _ => dialog.DispatcherQueue.TryEnqueue(() =>
            {
                completed = true;
                if (dialog.XamlRoot is null)
                {
                    return;
                }

                try
                {
                    dialog.Hide();
                }
                catch (Exception)
                {
                    // Its window is going: the dialog is not showing any more.
                }
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

    /// <summary>True when <paramref name="dialog"/> shows on <paramref name="root"/>; false when its root cannot be read.</summary>
    private static bool IsOn(ContentDialog dialog, XamlRoot root) => ReferenceEquals(RootOf(dialog), root);

    /// <summary>The dialog's root, or null when it has none or it cannot be read (its window is going).</summary>
    private static XamlRoot? RootOf(ContentDialog dialog)
    {
        try
        {
            return dialog.XamlRoot;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>A dialog inside <see cref="ShowAsync"/>; <see cref="HideDialogsOn"/> completes <see cref="Hidden"/>.</summary>
    private sealed class ShownDialog(ContentDialog dialog)
    {
        public ContentDialog Dialog { get; } = dialog;

        public TaskCompletionSource Hidden { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
