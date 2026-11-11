using System.Runtime.CompilerServices;
using SimpleFile.Core;
using Xunit;

namespace SimpleFile.Tests;

/// <summary>
/// A dialog that waited at DialogHost's app-wide gate while its window closed must not be shown on the dead root:
/// <see cref="ClosedRootRegistry{TRoot}"/> holds the decision, and the source-shape tests pin how DialogHost uses it.
/// </summary>
public class DialogHostTests
{
    [Fact]
    public void ClosedRootRegistry_RefusesANullOrClosedRootOnly()
    {
        var registry = new ClosedRootRegistry<object>();
        var open = new object();
        var closed = new object();

        registry.MarkClosed(closed);
        registry.MarkClosed(null);

        Assert.False(registry.CanShowOn(null));
        Assert.False(registry.CanShowOn(closed));
        Assert.True(registry.CanShowOn(open));
    }

    [Fact]
    public void ClosedRootRegistry_DoesNotKeepAClosedRootAlive()
    {
        var registry = new ClosedRootRegistry<object>();
        var weak = MarkClosedAndForget(registry);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.False(weak.TryGetTarget(out _));
    }

    [Fact]
    public void ShowAsync_ShowsNothingOnARootThatClosedWhileTheDialogWaitedAtTheGate()
    {
        var host = ReadApp("DialogHost.cs");
        var show = Body(host, "public static async Task<ContentDialogResult> ShowAsync(");
        var gate = show.IndexOf("await Gate.WaitAsync(", StringComparison.Ordinal);
        var check = show.IndexOf("ClosedRoots.CanShowOn(", StringComparison.Ordinal);
        var showing = show.IndexOf("dialog.ShowAsync()", StringComparison.Ordinal);
        Assert.True(gate >= 0 && check > gate, "ShowAsync must check the dialog's root after it acquired the gate.");
        Assert.True(showing > check, "ShowAsync must check the dialog's root before it shows the dialog.");
        Assert.Contains("return ContentDialogResult.None;", show[check..showing]);
        // The early return still releases the gate: the check sits inside the try whose finally releases it.
        var tryBlock = show.IndexOf("try", gate, StringComparison.Ordinal);
        Assert.True(tryBlock > gate && tryBlock < check, "The root check must sit inside the try that releases the gate.");
        Assert.Contains("Gate.Release()", show[show.LastIndexOf("finally", StringComparison.Ordinal)..]);

        var hide = Body(host, "internal static int HideDialogsOn(XamlRoot? root)");
        var mark = hide.IndexOf("ClosedRoots.MarkClosed(root)", StringComparison.Ordinal);
        Assert.True(mark >= 0, "HideDialogsOn must record the closed root.");
        Assert.True(mark < hide.IndexOf("lock (Shown)", StringComparison.Ordinal), "The root is recorded before the shown dialogs are read.");
        Assert.Contains("new ClosedRootRegistry<XamlRoot>()", host);
    }

    [Fact]
    public void PluginDialogs_CancellationHideIsGuarded()
    {
        // The cancellation callback can run after the plugin window closed; an unguarded Hide there reaches the
        // unhandled-exception handler.
        var dialogs = ReadApp("PluginDialogs.cs");
        var show = Body(dialogs, "private async Task<ContentDialogResult> ShowAsync(ContentDialog dialog, CancellationToken ct)");

        Assert.DoesNotContain("TryEnqueue(dialog.Hide)", show);
        Assert.Contains("dialog.XamlRoot is null", show);
        WinUiSourceShapeTests.AssertEveryHideIsGuarded(show, "PluginDialogs.ShowAsync", "dialog.Hide()", expected: 1);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<object> MarkClosedAndForget(ClosedRootRegistry<object> registry)
    {
        var root = new object();
        registry.MarkClosed(root);
        Assert.False(registry.CanShowOn(root));
        return new WeakReference<object>(root);
    }

    private static string ReadApp(string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "SimpleFile.App", name);
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate src-winui source root.");
    }

    private static string Body(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{signature}' was not found.");
        var open = source.IndexOf('{', start);
        var depth = 0;
        for (var index = open; index < source.Length; index++)
        {
            if (source[index] == '{') depth++;
            else if (source[index] == '}' && --depth == 0) return source[open..(index + 1)];
        }

        throw new InvalidOperationException($"'{signature}' has no closing brace.");
    }
}
