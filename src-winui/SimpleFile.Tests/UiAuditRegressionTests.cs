using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using SimpleFile.Core;
using Xunit;

namespace SimpleFile.Tests;

/// <summary>
/// Regression guards for the findings in docs/UI_AUDIT_2026-11-05.md.
/// WinUI code-behind cannot be instantiated here, so App-side guards assert on source shape.
/// </summary>
public class UiAuditRegressionTests
{
    // F1: the IsHidden opacity used to be overwritten by the IsCut opacity two lines later.
    [Fact]
    public void FileRowView_ComputesRowOpacityOnce()
    {
        var fileRowView = File.ReadAllText(Path.Combine(AppRoot(), "FileRowView.xaml.cs"));

        Assert.Contains("Opacity = EntryPresentation.RowOpacity(Row.IsCut, Row.IsHidden);", fileRowView);
        Assert.DoesNotContain("Opacity = Row.IsHidden", fileRowView);
        Assert.DoesNotContain("Opacity = Row.IsCut", fileRowView);
    }

    // C1: "FTP/SFTP Manager..." and "Operation history..." were emitted as raw ids that the
    // context router never forwarded. Every non-ctx id the pane More menu emits must be an alias
    // that normalizes to a catalog command.
    [Fact]
    public void PaneMoreMenu_EmitsOnlyAliasedOrContextIds()
    {
        var entries = Flatten(ContextMenuBuilder.BuildPaneMoreMenu(new ContextMenuRequest()))
            .Where(entry => entry.Kind == ContextMenuKind.Item)
            .Select(entry => entry.Id)
            .ToList();

        Assert.NotEmpty(entries);
        foreach (var id in entries)
        {
            if (id.StartsWith("ctx-", StringComparison.Ordinal)
                || id.StartsWith("new:", StringComparison.Ordinal)
                || id.StartsWith("view:", StringComparison.Ordinal)
                || id.StartsWith("icon:", StringComparison.Ordinal)
                || id.StartsWith("profile:", StringComparison.Ordinal)
                || id.StartsWith("pane:", StringComparison.Ordinal))
            {
                continue;
            }

            var normalized = CommandAliasCatalog.Normalize(id);
            Assert.True(normalized != id, $"'{id}' is neither a ctx- id nor an alias of an app command.");
            Assert.NotNull(AppCommandCatalog.Find(normalized));
        }
    }

    // C1: the router must also fall through to the app command table for any id it does not
    // recognise, instead of silently doing nothing.
    [Fact]
    public void ContextCommandRouter_FallsThroughToAppCommandHandlers()
    {
        var commands = File.ReadAllText(Path.Combine(AppRoot(), "MainWindow.Commands.cs"));
        var router = Slice(commands, "private async Task RunContextCommandAsync(", "private async void OnRootPreviewKeyDown(");

        Assert.Contains("AppCommandHandlers.ContainsKey(commandId)", router);
    }

    // P4: a failing non-silent git status refresh left the workbench busy forever.
    [Fact]
    public void GitRefresh_ClearsBusyStateWhenStatusFails()
    {
        var git = File.ReadAllText(Path.Combine(AppRoot(), "MainWindow.Git.cs"));
        var refresh = Slice(git, "_gitCts?.Cancel();", "private void RefreshGitUiVisibility()");

        Assert.DoesNotContain("catch (Exception exception) when (silent)", refresh);
        Assert.Contains("_gitWorkbenchModel.SetUnavailable(exception.Message);", refresh);
        Assert.Matches(new Regex(@"if \(!silent\)\s*\{\s*throw;"), refresh);
    }

    // F5: the internal clipboard shadowed the Windows clipboard; the app must observe
    // Clipboard.ContentChanged and reconcile against the stamp it exported.
    [Fact]
    public void Clipboard_ReconcilesInternalStateWithWindowsClipboard()
    {
        var mainWindow = ReadMainWindowSource(AppRoot());

        Assert.Contains("Clipboard.ContentChanged +=", mainWindow);
        Assert.Contains("ClipboardStampFormat", mainWindow);
        Assert.Contains("Clipboard.ReconcileWith(", mainWindow);
    }

    private static System.Collections.Generic.IEnumerable<ContextMenuEntry> Flatten(
        System.Collections.Generic.IEnumerable<ContextMenuEntry> entries)
    {
        foreach (var entry in entries)
        {
            yield return entry;
            if (entry.Children is { Count: > 0 } children)
            {
                foreach (var child in Flatten(children))
                {
                    yield return child;
                }
            }
        }
    }

    private static string Slice(string text, string start, string end)
    {
        var startIndex = text.IndexOf(start, StringComparison.Ordinal);
        Assert.True(startIndex >= 0, $"Anchor '{start}' not found.");
        var endIndex = text.IndexOf(end, startIndex, StringComparison.Ordinal);
        Assert.True(endIndex > startIndex, $"Anchor '{end}' not found after '{start}'.");
        return text[startIndex..endIndex];
    }

    private static string AppRoot() => Path.Combine(FindRepoRoot(), "SimpleFile.App");

    private static string ReadMainWindowSource(string appRoot)
    {
        return string.Join(
            Environment.NewLine,
            Directory.EnumerateFiles(appRoot, "MainWindow*.cs")
                .OrderBy(Path.GetFileName)
                .Select(File.ReadAllText));
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "SimpleFile.App", "MainWindow.xaml.cs");
            if (File.Exists(candidate))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate src-winui source root.");
    }
}

public class RowOpacityTests
{
    [Fact]
    public void HiddenRows_AreDimmed()
    {
        Assert.Equal(0.55, EntryPresentation.RowOpacity(isCut: false, isHidden: true));
    }

    [Fact]
    public void CutRows_AreDimmedMoreThanHiddenRows()
    {
        Assert.Equal(0.45, EntryPresentation.RowOpacity(isCut: true, isHidden: false));
        Assert.Equal(0.45, EntryPresentation.RowOpacity(isCut: true, isHidden: true));
    }

    [Fact]
    public void NormalRows_AreOpaque()
    {
        Assert.Equal(1.0, EntryPresentation.RowOpacity(isCut: false, isHidden: false));
    }
}

public class ClipboardStampTests
{
    [Fact]
    public void SetCopy_AssignsAFreshStamp()
    {
        var state = new ClipboardState();
        state.SetCopy([@"C:\a.txt"]);
        var first = state.Stamp;
        state.SetCut([@"C:\b.txt"]);

        Assert.False(string.IsNullOrEmpty(first));
        Assert.NotEqual(first, state.Stamp);
    }

    [Fact]
    public void ReconcileWith_OwnStamp_KeepsItems()
    {
        var state = new ClipboardState();
        state.SetCopy([@"C:\a.txt"]);

        state.ReconcileWith(state.Stamp);

        Assert.True(state.HasItems);
    }

    [Fact]
    public void ReconcileWith_ForeignOrMissingStamp_ClearsItems()
    {
        var state = new ClipboardState();
        state.SetCopy([@"C:\a.txt"]);
        state.ReconcileWith("someone-else");
        Assert.False(state.HasItems);

        state.SetCut([@"C:\b.txt"]);
        state.ReconcileWith(null);
        Assert.False(state.HasItems);
    }
}

public class ShortcutTextFocusGuardTests
{
    // S4: destructive/editing shortcuts fired from inside text boxes.
    [Theory]
    [InlineData("file.delete.trash")]
    [InlineData("file.delete.permanent")]
    [InlineData("file.rename")]
    [InlineData("history.undo")]
    [InlineData("history.redo")]
    [InlineData("pane.focusPrimary")]
    [InlineData("pane.focusSecondary")]
    [InlineData("pane.switch")]
    [InlineData("quickLook.toggle")]
    [InlineData("nav.parent")]
    [InlineData("file.open")]
    [InlineData("file.copy")]
    [InlineData("file.paste")]
    [InlineData("selection.all")]
    public void EditingAndDestructiveCommands_AreSuppressedWhileEditingText(string id)
    {
        Assert.True(KeyboardShortcutMap.IsSuppressedWhileEditingText(id));
    }

    [Theory]
    [InlineData("directory.refresh")]
    [InlineData("settings.open")]
    [InlineData("tabs.new")]
    [InlineData("preview.toggle")]
    public void NavigationAndChromeCommands_StillWorkWhileEditingText(string id)
    {
        Assert.False(KeyboardShortcutMap.IsSuppressedWhileEditingText(id));
    }

    // S3/C6: any default bound to a key without Ctrl/Alt/Win (Tab, Space, Delete, Backspace, F2,
    // Enter, Shift+Delete) collides with text editing and must be in the suppression set.
    [Fact]
    public void EveryBareKeyDefault_IsSuppressedWhileEditingText()
    {
        foreach (var shortcut in KeyboardShortcutMap.Defaults.Where(item => item.IsEditable))
        {
            foreach (var chord in shortcut.DefaultShortcuts)
            {
                if (!KeyboardShortcutMap.TryParseShortcut(chord, out var gesture, out _) || gesture is null)
                {
                    continue;
                }

                if (gesture.Control || gesture.Alt || gesture.Windows)
                {
                    continue;
                }

                // Function keys do not edit text; only the editing keys collide.
                if (Regex.IsMatch(gesture.Key, "^F[0-9]{1,2}$", RegexOptions.IgnoreCase))
                {
                    continue;
                }

                Assert.True(
                    KeyboardShortcutMap.IsSuppressedWhileEditingText(shortcut.Id),
                    $"'{shortcut.Id}' is bound to bare key '{chord}' but is not suppressed while editing text.");
            }
        }
    }

    // S3: the accelerator used to mark the key handled before deciding whether to run.
    [Fact]
    public void Accelerators_MarkKeyHandledOnlyWhenTheCommandRuns()
    {
        var root = Path.Combine(FindRepoRoot(), "SimpleFile.App");
        var shortcuts = File.ReadAllText(Path.Combine(root, "MainWindow.Shortcuts.cs"));
        var accelerator = Slice(shortcuts, "private KeyboardAccelerator CreateCommandAccelerator(", "private void AddFixedTabJumpAccelerators()");

        Assert.Contains("if (!CanRunShortcutNow(commandId))", accelerator);
        Assert.True(
            accelerator.IndexOf("CanRunShortcutNow", StringComparison.Ordinal)
                < accelerator.IndexOf("args.Handled = true;", StringComparison.Ordinal),
            "Handled must be decided after the guard.");
        Assert.Contains("KeyboardShortcutMap.IsSuppressedWhileEditingText(commandId)", shortcuts);
    }

    private static string Slice(string text, string start, string end)
    {
        var startIndex = text.IndexOf(start, StringComparison.Ordinal);
        Assert.True(startIndex >= 0, $"Anchor '{start}' not found.");
        var endIndex = text.IndexOf(end, startIndex, StringComparison.Ordinal);
        Assert.True(endIndex > startIndex, $"Anchor '{end}' not found after '{start}'.");
        return text[startIndex..endIndex];
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "SimpleFile.App", "MainWindow.xaml.cs")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate src-winui source root.");
    }
}

public class DialogGateTests
{
    // C2/T3: WinUI throws when a second ContentDialog opens on the same XamlRoot; every dialog
    // must go through the DialogHost gate so a second request waits instead of throwing.
    [Fact]
    public void AllContentDialogs_AreShownThroughTheDialogHost()
    {
        var appRoot = Path.Combine(FindRepoRoot(), "SimpleFile.App");
        var host = File.ReadAllText(Path.Combine(appRoot, "DialogHost.cs"));
        Assert.Contains("SemaphoreSlim", host);
        Assert.Contains("public static bool IsDialogOpen", host);

        var direct = new Regex(@"\b(dialog|confirm|conflict)\.ShowAsync\(\)");
        foreach (var file in Directory.EnumerateFiles(appRoot, "*.cs"))
        {
            var name = Path.GetFileName(file);
            if (name is "DialogHost.cs" or "MainWindow.UiPolishSmoke.cs")
            {
                continue;
            }

            var source = File.ReadAllText(file);
            if (!source.Contains("ContentDialog", StringComparison.Ordinal))
            {
                continue;
            }

            Assert.False(direct.IsMatch(source), $"{name} shows a ContentDialog without DialogHost.");
        }
    }

    // C2: the profile manager opened nested dialogs from its own buttons, which crashed the app.
    [Fact]
    public void ProfileManager_HidesItselfBeforeOpeningSubPrompts()
    {
        var appRoot = Path.Combine(FindRepoRoot(), "SimpleFile.App");
        var profiles = File.ReadAllText(Path.Combine(appRoot, "MainWindow.WorkspaceProfiles.cs"));
        var manager = profiles[profiles.IndexOf("ShowWorkspaceProfileManagerAsync()", StringComparison.Ordinal)..];

        Assert.Contains("dialog.Hide();", manager);
        Assert.DoesNotContain("Click += async", manager);
    }

    // C15: global accelerators stayed live while a dialog was open.
    [Fact]
    public void Shortcuts_YieldWhileADialogIsOpen()
    {
        var appRoot = Path.Combine(FindRepoRoot(), "SimpleFile.App");
        var shortcuts = File.ReadAllText(Path.Combine(appRoot, "MainWindow.Shortcuts.cs"));
        var guard = shortcuts[shortcuts.IndexOf("private bool CanRunShortcutNow(", StringComparison.Ordinal)..];

        Assert.Contains("DialogHost.IsDialogOpen", guard);
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "SimpleFile.App", "MainWindow.xaml.cs")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate src-winui source root.");
    }
}

public class UtilityOperationRegistryTests
{
    // C22: one shared CancellationTokenSource used to be cancelled by any unrelated utility action.
    [Fact]
    public void Begin_DifferentKinds_DoNotCancelEachOther()
    {
        using var registry = new UtilityOperationRegistry();
        var settings = registry.Begin("settings");
        var quickLook = registry.Begin("quickLook");

        Assert.False(settings.IsCancellationRequested);
        Assert.False(quickLook.IsCancellationRequested);
    }

    [Fact]
    public void Begin_SameKind_CancelsThePreviousOperation()
    {
        using var registry = new UtilityOperationRegistry();
        var first = registry.Begin("quickLook");
        var second = registry.Begin("quickLook");

        Assert.True(first.IsCancellationRequested);
        Assert.False(second.IsCancellationRequested);
    }

    [Fact]
    public void Finish_ReleasesWithoutCancellingOthers()
    {
        using var registry = new UtilityOperationRegistry();
        var first = registry.Begin("git");
        var other = registry.Begin("scan");
        registry.Finish(first);
        var again = registry.Begin("git");

        Assert.False(other.IsCancellationRequested);
        Assert.False(again.IsCancellationRequested);
    }

    [Fact]
    public void CancelAll_CancelsEveryActiveOperation()
    {
        using var registry = new UtilityOperationRegistry();
        var a = registry.Begin("a");
        var b = registry.Begin("b");
        registry.CancelAll();

        Assert.True(a.IsCancellationRequested);
        Assert.True(b.IsCancellationRequested);
    }
}

public class UtilityOperationWiringTests
{
    // C3/T1/P3: every utility operation must name its kind so only a repeat of the same action
    // cancels it; the single `_utilityCts` slot must be gone.
    [Fact]
    public void UtilityOperations_AreKeyedByKind()
    {
        var appRoot = Path.Combine(FindRepoRoot(), "SimpleFile.App");
        var sources = Directory.EnumerateFiles(appRoot, "*.cs")
            .Where(file => Path.GetFileName(file) is not "DialogHost.cs")
            .Select(file => (Name: Path.GetFileName(file), Text: File.ReadAllText(file)))
            .ToList();

        Assert.Contains(sources, s => s.Text.Contains("UtilityOperationRegistry", StringComparison.Ordinal));
        foreach (var (name, text) in sources)
        {
            Assert.False(text.Contains("_utilityCts", StringComparison.Ordinal), $"{name} still uses the shared _utilityCts slot.");
            Assert.False(
                Regex.IsMatch(text, @"BeginUtilityOperation\(\)|_beginUtilityOperation\(\)"),
                $"{name} begins a utility operation without naming its kind.");
        }
    }

    // C3: Settings is a modeless window; opening it twice created two instances.
    [Fact]
    public void SettingsWindow_IsSingleInstanceAndClosedWithTheMainWindow()
    {
        var appRoot = Path.Combine(FindRepoRoot(), "SimpleFile.App");
        var dialogs = File.ReadAllText(Path.Combine(appRoot, "FileOperationDialogService.cs"));
        var settings = File.ReadAllText(Path.Combine(appRoot, "SettingsWindow.xaml.cs"));
        var mainWindow = string.Join("\n", Directory.EnumerateFiles(appRoot, "MainWindow*.cs").Select(File.ReadAllText));

        Assert.Contains("public bool IsClosed", settings);
        Assert.Contains("_settingsWindow is { IsClosed: false }", dialogs);
        Assert.Contains("public void CloseSettingsWindow()", dialogs);
        Assert.Contains("_fileOperationDialogs.CloseSettingsWindow();", mainWindow);
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "SimpleFile.App", "MainWindow.xaml.cs")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate src-winui source root.");
    }
}

public class PreviewHtmlSanitizerTests
{
    private static string RenderBody(string html)
    {
        var full = PreviewHtmlRenderer.RenderDocument(
            @"C:\docs\page.html",
            new SimpleFile.Ipc.FilePreview { FileType = "text", MimeType = "text/html", Content = html });
        return full[full.IndexOf("<body>", StringComparison.Ordinal)..];
    }

    // P1: the regex blocklist let event handlers without leading whitespace, non-http schemes,
    // forms, base, inline styles, images and layout tags through. The allowlist must drop them.
    [Theory]
    [InlineData("<svg/onload=alert(1)><p>x</p>", "onload")]
    [InlineData("<p\tonmouseover=x>y</p>", "onmouseover")]
    [InlineData("<a href=\"vbscript:msgbox(1)\">x</a>", "vbscript:")]
    [InlineData("<a href=\"file:///C:/Windows\">x</a>", "file:")]
    [InlineData("<a href=\"data:text/html;base64,AAA\">x</a>", "data:")]
    [InlineData("<a href=\" javascript:alert(1)\">x</a>", "javascript:")]
    [InlineData("<form action=\"https://evil.example\"><input name=q></form>", "<form")]
    [InlineData("<base href=\"https://evil.example/\">", "<base")]
    [InlineData("<p style=\"color:red\">x</p>", "style=")]
    [InlineData("<img src=\"file:///C:/secret.png\">", "<img")]
    [InlineData("<div onclick=\"x()\">y</div>", "<div")]
    [InlineData("<SCRIPT>alert(1)</SCRIPT>", "alert(1)")]
    [InlineData("<iframe src=\"https://evil\"></iframe>", "<iframe")]
    [InlineData("<object data=x></object>", "<object")]
    [InlineData("<style>body{}</style>", "<style")]
    [InlineData("<meta http-equiv=refresh content=\"0;url=https://evil\">", "<meta")]
    [InlineData("<link rel=stylesheet href=x>", "<link")]
    public void UnsafeHtml_IsStripped(string html, string mustNotContain)
    {
        var body = RenderBody(html);
        Assert.DoesNotContain(mustNotContain, body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SafeMarkup_IsKeptWithItsAllowedAttributes()
    {
        var body = RenderBody(
            "<h1>T</h1><p>a <a href=\"https://example.com/x\" title=\"t\">l</a> <a href=\"mailto:a@b.c\">m</a>"
            + " <a href=\"#top\">f</a> <em>e</em> <strong>s</strong> <del>d</del> <code>c</code></p>"
            + "<ul><li>i</li></ul><ol start=\"3\"><li>j</li></ol><pre><code>pre</code></pre>"
            + "<blockquote>q</blockquote><hr><br><table><thead><tr><th>h</th></tr></thead><tbody><tr><td>d</td></tr></tbody></table>");

        foreach (var tag in new[] { "<h1>", "<p>", "<em>", "<strong>", "<del>", "<code>", "<ul>", "<li>", "<ol start=\"3\">", "<pre>", "<blockquote>", "<hr>", "<br>", "<table>", "<thead>", "<tbody>", "<tr>", "<th>", "<td>" })
        {
            Assert.Contains(tag, body);
        }

        Assert.Contains("href=\"https://example.com/x\"", body);
        Assert.Contains("title=\"t\"", body);
        Assert.Contains("href=\"mailto:a@b.c\"", body);
        Assert.Contains("href=\"#top\"", body);
    }

    [Fact]
    public void RemovedLayoutTags_KeepTheirText()
    {
        var body = RenderBody("<div class=\"x\"><span>kept</span></div>");

        Assert.Contains("kept", body);
        Assert.DoesNotContain("<div", body);
        Assert.DoesNotContain("<span", body);
    }

    [Fact]
    public void DroppedScriptLikeElements_LoseTheirContent()
    {
        var body = RenderBody("<p>a</p><script>document.title='x'</script><noscript>n</noscript><template>t</template><p>b</p>");

        Assert.DoesNotContain("document.title", body);
        Assert.DoesNotContain(">n<", body);
        Assert.DoesNotContain(">t<", body);
        Assert.Contains("<p>a</p>", body);
        Assert.Contains("<p>b</p>", body);
    }
}

public class PreviewWebViewLockdownTests
{
    // P1: the embedded WebView2 had default settings and followed any navigation.
    [Fact]
    public void PreviewWebView_IsConfiguredAndBlocksNavigation()
    {
        var appRoot = Path.Combine(FindRepoRoot(), "SimpleFile.App");
        var presenter = string.Join("\n", Directory.EnumerateFiles(appRoot, "PreviewPresenter*.cs").Select(File.ReadAllText));

        Assert.Contains("AreDevToolsEnabled = false", presenter);
        Assert.Contains("AreDefaultContextMenusEnabled = false", presenter);
        Assert.Contains("AreHostObjectsAllowed = false", presenter);
        Assert.Contains("IsWebMessageEnabled = false", presenter);
        Assert.Contains("IsScriptEnabled =", presenter);
        Assert.Contains("NavigationStarting +=", presenter);
        Assert.Contains("NewWindowRequested +=", presenter);
        Assert.Contains("args.Cancel = true;", presenter);
        Assert.Contains("args.Handled = true;", presenter);
        // Both the HTML and the PDF paths must initialise through the same lockdown.
        Assert.Equal(2, Regex.Matches(presenter, @"await EnsurePreviewWebViewAsync\(").Count);
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "SimpleFile.App", "MainWindow.xaml.cs")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate src-winui source root.");
    }
}
