using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using SimpleFile.Core;
using SimpleFile.Ipc;
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

public class Sprint2ShellTests
{
    // S1: clicking the omnibar's own Run button moved focus, which reset the typed text.
    [Fact]
    public void OmnibarRunButton_DoesNotTakeFocusFromTheInput()
    {
        var xaml = File.ReadAllText(Path.Combine(AppRoot(), "PrimaryToolbarView.xaml"));
        var element = ElementFor(xaml, "OmnibarExecuteButton");

        Assert.Contains("AllowFocusOnInteraction=\"False\"", element);
    }

    // 1.0.4: the mode toggles are keyboard focusable; a click hands focus back to the text box
    // and the typed text survives the toggle taking focus.
    [Theory]
    [InlineData("OmnibarPathModeButton")]
    [InlineData("OmnibarSearchModeButton")]
    [InlineData("OmnibarFilterModeButton")]
    [InlineData("OmnibarCommandModeButton")]
    public void OmnibarModeButtons_AreFocusableAndReturnFocusToTheInput(string name)
    {
        var xaml = File.ReadAllText(Path.Combine(AppRoot(), "PrimaryToolbarView.xaml"));
        var element = ElementFor(xaml, name);
        Assert.DoesNotContain("AllowFocusOnInteraction", element);

        var code = File.ReadAllText(Path.Combine(AppRoot(), "PrimaryToolbarView.xaml.cs"));
        var request = code[code.IndexOf("private void RequestOmnibarMode(", StringComparison.Ordinal)..];
        Assert.Contains("OmnibarBox.Focus(FocusState.Programmatic)", request);

        var omnibar = File.ReadAllText(Path.Combine(AppRoot(), "MainWindow.Omnibar.cs"));
        Assert.Contains("IsOmnibarModeButtonFocused()", omnibar);
        Assert.Contains("_omnibarKeepTypedText", omnibar);
    }

    [Fact]
    public void OmnibarModeSwitch_KeepsTextTheUserTyped()
    {
        var omnibar = File.ReadAllText(Path.Combine(AppRoot(), "MainWindow.Omnibar.cs"));
        var setMode = omnibar[omnibar.IndexOf("private void SetOmnibarMode(", StringComparison.Ordinal)..];

        Assert.Contains("PreserveTypedOmnibarText", setMode);
    }

    // S2: the suggestion flyout took focus (ending the edit) and suggestions raced per keystroke.
    [Fact]
    public void PathSuggestions_AreTransientAndDebounced()
    {
        var path = File.ReadAllText(Path.Combine(AppRoot(), "MainWindow.Path.cs"));

        Assert.Contains("ShowMode = FlyoutShowMode.Transient", path);
        Assert.Contains("_pathSuggestToken", path);
        Assert.Contains("Task.Delay(", path);
    }

    // F4: every workspace sync collapsed a Details multi-selection and scrolled to it.
    [Fact]
    public void DetailsSelectPath_PreservesMultiSelectionAndOnlyScrollsOnChange()
    {
        var details = File.ReadAllText(Path.Combine(AppRoot(), "DetailsFileListView.cs"));
        var selectPath = details[details.IndexOf("public void SelectPath(string? path)", StringComparison.Ordinal)..];
        selectPath = selectPath[..selectPath.IndexOf("public bool ContainsSelection", StringComparison.Ordinal)];

        Assert.Contains("_selectedRows.Count > 1", selectPath);
        Assert.Contains("ContainsSelection(row)", selectPath);
        var chrome = File.ReadAllText(Path.Combine(AppRoot(), "MainWindow.Chrome.cs"));
        Assert.DoesNotContain("row.Path == path", chrome);
    }

    internal static string ElementFor(string xaml, string name)
    {
        var at = xaml.IndexOf($"x:Name=\"{name}\"", StringComparison.Ordinal);
        Assert.True(at >= 0, $"{name} not found");
        var start = xaml.LastIndexOf('<', at);
        var end = xaml.IndexOf('>', at);
        return xaml[start..end];
    }

    internal static string AppRoot() => Path.Combine(FindRepoRoot(), "SimpleFile.App");

    internal static string FindRepoRoot()
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

public class Sprint2FileListTests
{
    // F2: thumbnail loads continued on the thread pool and built XAML images there.
    [Fact]
    public void ThumbnailLoads_StayOnTheUiContext()
    {
        var host = File.ReadAllText(Path.Combine(Sprint2ShellTests.AppRoot(), "FileListThumbnailHost.cs"));
        Assert.DoesNotContain("ConfigureAwait(false)", host);
    }

    // F3: real exe/lnk icons were built off-thread and never pushed to realized rows.
    [Fact]
    public void RealShellIcons_AreBuiltOnTheUiThreadAndAnnounced()
    {
        var loader = File.ReadAllText(Path.Combine(Sprint2ShellTests.AppRoot(), "ShellIconLoader.cs"));
        var rowView = File.ReadAllText(Path.Combine(Sprint2ShellTests.AppRoot(), "FileRowView.xaml.cs"));

        Assert.Contains("public static event EventHandler? Changed;", loader);
        var background = loader[loader.IndexOf("Task.Run(", StringComparison.Ordinal)..];
        background = background[..background.IndexOf("return icon;", StringComparison.Ordinal)];
        Assert.DoesNotContain("new BitmapImage", background);
        Assert.Contains("TryEnqueue(", background);
        Assert.Contains("Changed?.Invoke", background);
        Assert.Contains("ShellIconLoader.Changed += OnIconsChanged;", rowView);
        Assert.Contains("ShellIconLoader.Changed -= OnIconsChanged;", rowView);
    }

    // F6: cut/copy never re-rendered rows, so the cut marker appeared late or stayed stale.
    [Fact]
    public void CopyAndCut_RefreshTheView()
    {
        var ops = File.ReadAllText(Path.Combine(Sprint2ShellTests.AppRoot(), "MainWindow.FileOperations.cs"));
        var copy = Slice(ops, "private async Task CopyToClipboard()", "private async Task CutToClipboard()");
        var cut = Slice(ops, "private async Task CutToClipboard()", "private async Task PasteFromClipboard(");

        Assert.Contains("RefreshView();", copy);
        Assert.Contains("RefreshView();", cut);
    }

    // F7: List/Tiles opened the first selected row while Details opened the last; two overloads.
    [Fact]
    public void OpenSelected_UsesOneAnchorRuleForBothViews()
    {
        var events = File.ReadAllText(Path.Combine(Sprint2ShellTests.AppRoot(), "MainWindow.FileListEvents.cs"));
        var commands = File.ReadAllText(Path.Combine(Sprint2ShellTests.AppRoot(), "MainWindow.Commands.cs"));
        var anchor = Slice(commands, "private FileRow? SelectedRowForPane(PaneId pane)", "private ContextMenuRequest BuildContextMenuRequest(");

        Assert.DoesNotContain("OpenSelectedFile(ListView list", events);
        Assert.Single(Regex.Matches(events, @"private async Task HandleFileKey\("));
        Assert.Contains("LastOrDefault() ?? list.SelectedItem as FileRow", anchor);
    }

    // F8: double-clicking blank space opened the selected item.
    [Fact]
    public void DoubleTap_RequiresARowUnderThePointer()
    {
        var details = File.ReadAllText(Path.Combine(Sprint2ShellTests.AppRoot(), "DetailsFileListView.cs"));
        var doubleTap = Slice(details, "private void OnListDoubleTapped(", "private void OnListRightTapped(");
        var events = File.ReadAllText(Path.Combine(Sprint2ShellTests.AppRoot(), "MainWindow.FileListEvents.cs"));

        Assert.DoesNotContain("?? SelectedRow", doubleTap);
        Assert.Contains("DoubleTapHitsRow(e.OriginalSource)", events);
    }

    // Right-clicking blank pane space did nothing in Details and kept the stale selection elsewhere.
    [Fact]
    public void DetailsRightTap_OnBlankSpaceClearsSelectionAndRaisesBackgroundContext()
    {
        var details = File.ReadAllText(Path.Combine(Sprint2ShellTests.AppRoot(), "DetailsFileListView.cs"));
        var rightTap = Slice(details, "private void OnListRightTapped(", "private void OnListContextRequested(");
        var contextRequested = Slice(details, "private void OnListContextRequested(", "private void OnListDragItemsStarting(");

        Assert.Contains("public event EventHandler<DetailsFileBackgroundContextEventArgs>? BackgroundContextRequested;", details);
        Assert.Contains("RaiseBackgroundContext(", rightTap);
        Assert.Contains("RaiseBackgroundContext(", contextRequested);
        Assert.DoesNotContain("if (row is null)\n        {\n            return;", rightTap.Replace("\r\n", "\n"));

        var raise = Slice(details, "private void RaiseBackgroundContext(", "private FileRow? RowFromSource(");
        Assert.Contains("ClearSelection();", raise);
        Assert.Contains("BackgroundContextRequested?.Invoke(", raise);
    }

    [Fact]
    public void PaneView_ForwardsDetailsBackgroundContextToTheWindow()
    {
        var appRoot = Sprint2ShellTests.AppRoot();
        var paneView = File.ReadAllText(Path.Combine(appRoot, "PaneView.xaml.cs"));
        var window = string.Join(
            Environment.NewLine,
            Directory.EnumerateFiles(appRoot, "MainWindow*.cs").OrderBy(Path.GetFileName).Select(File.ReadAllText));

        Assert.Contains("PartDetailsFileList.BackgroundContextRequested += (_, e) => DetailsBackgroundContextRequested?.Invoke(this, e);", paneView);
        Assert.Contains("PrimaryPane.DetailsBackgroundContextRequested += OnPrimaryDetailsBackgroundContextRequested;", window);
        Assert.Contains("SecondaryPane.DetailsBackgroundContextRequested += OnSecondaryDetailsBackgroundContextRequested;", window);

        var show = Slice(window, "private void ShowDetailsBackgroundContextMenu(", "private static T? FindAncestor<T>(");
        Assert.Contains("PopulateFileListContextFlyout(flyout, pane);", show);
        Assert.Contains("flyout.ShowAt(list, new FlyoutShowOptions { Position = position });", show);
    }

    [Fact]
    public void FolderProperties_ShowsTheCurrentFolderThroughTheSharedPropertiesDialog()
    {
        var appRoot = Sprint2ShellTests.AppRoot();
        var routing = File.ReadAllText(Path.Combine(appRoot, "MainWindow.CommandRouting.cs"));
        var dialogs = File.ReadAllText(Path.Combine(appRoot, "MainWindow.InspectionDialogs.cs"));

        Assert.Contains("[\"folder-properties\"] = ShowCurrentFolderPropertiesAsync,", routing);

        var folder = Slice(dialogs, "private async Task ShowCurrentFolderPropertiesAsync(", "private async Task ShowPropertiesForRowAsync(");
        Assert.Contains("GetEntryInfoAsync(workspace.Active.Path", folder);
        Assert.Contains("FileRow.From(", folder);
        Assert.Contains("await ShowPropertiesForRowAsync(", folder);

        var selected = Slice(dialogs, "private async Task ShowPropertiesAsync(", "private async Task ShowCurrentFolderPropertiesAsync(");
        Assert.Contains("ActiveSelectedRow", selected);
        Assert.Contains("await ShowPropertiesForRowAsync(", selected);
    }

    [Fact]
    public void PropertiesDialog_SkipsChecksumsForFoldersAndLoadsFolderMetrics()
    {
        var dialogs = File.ReadAllText(Path.Combine(Sprint2ShellTests.AppRoot(), "MainWindow.InspectionDialogs.cs"));
        var shared = Slice(dialogs, "private async Task ShowPropertiesForRowAsync(", "private async Task ShowClipboardHistoryAsync(");

        Assert.Contains("if (!row.IsDir)", shared);
        Assert.Contains("LoadFolderMetricsIntoAsync(fileOps, row.Path", shared);
        Assert.Contains("GetFolderMetricsAsync(path, cts.Token)", shared);
        Assert.Contains("InspectionDetails.FolderMetricRows(", shared);
        Assert.Contains("dialog.Closed +=", shared);
        Assert.Contains("await DialogHost.ShowAsync(dialog);", shared);
    }

    [Fact]
    public void CreateShortcutForSelection_PrefillsTheTarget()
    {
        var appRoot = Sprint2ShellTests.AppRoot();
        var routing = File.ReadAllText(Path.Combine(appRoot, "MainWindow.CommandRouting.cs"));
        var fileOperations = File.ReadAllText(Path.Combine(appRoot, "MainWindow.FileOperations.cs"));
        var dialogService = File.ReadAllText(Path.Combine(appRoot, "FileOperationDialogService.cs"));

        Assert.Contains("[\"shortcut-for-selected\"] = PromptAndCreateShortcutForSelection,", routing);

        var handler = Slice(fileOperations, "private async Task PromptAndCreateShortcutForSelection(", "private async Task CreateNewItem(");
        Assert.Contains("ActiveSelectedRow", handler);
        Assert.Contains("PromptAndCreateShortcutAsync(", handler);

        Assert.Contains("public async Task PromptAndCreateShortcutAsync(PaneId pane, string? initialTarget = null)", dialogService);
        Assert.Contains("ApplyPickedTarget(initialTarget);", dialogService);
    }

    [Fact]
    public void ToolbarNewFlyout_UsesTheLinkGlyphForShortcuts()
    {
        var toolbar = File.ReadAllText(Path.Combine(Sprint2ShellTests.AppRoot(), "PrimaryToolbarView.xaml"));
        var shortcutItem = Slice(toolbar, "Tag=\"new:shortcut\"", "</MenuFlyoutItem>");

        Assert.Contains("Glyph=\"&#xE71B;\"", shortcutItem);
    }

    [Fact]
    public void ListViewRightTap_OnBlankSpaceClearsSelection()
    {
        var events = File.ReadAllText(Path.Combine(Sprint2ShellTests.AppRoot(), "MainWindow.FileListEvents.cs"));
        var rightTap = Slice(events, "private void SelectRightTapped(", "private void OnPrimarySelectionChanged(");

        Assert.Contains("if (row is null)", rightTap);
        Assert.Contains("list.SelectedItems.Clear();", Slice(rightTap, "if (row is null)", "if (list.SelectedItems.OfType<FileRow>().Any("));
    }

    // F9: type-ahead ignored digits, fired on Ctrl/Alt chords and never forwarded Backspace.
    [Theory]
    [InlineData("A", false, 'a')]
    [InlineData("Number5", false, '5')]
    [InlineData("NumberPad7", false, '7')]
    [InlineData("Back", false, '\b')]
    public void TypeAhead_MapsLettersDigitsAndBackspace(string key, bool modifier, char expected)
    {
        Assert.True(TypeAhead.TryMapKey(key, modifier, out var character));
        Assert.Equal(expected, character);
    }

    [Theory]
    [InlineData("A", true)]
    [InlineData("F5", false)]
    [InlineData("Space", false)]
    [InlineData("Enter", false)]
    [InlineData("", false)]
    public void TypeAhead_IgnoresChordsAndNonTextKeys(string key, bool modifier)
    {
        Assert.False(TypeAhead.TryMapKey(key, modifier, out _));
    }

    [Fact]
    public void FileListKeys_UseTheSharedTypeAheadMapping()
    {
        var events = File.ReadAllText(Path.Combine(Sprint2ShellTests.AppRoot(), "MainWindow.FileListEvents.cs"));
        Assert.Contains("TypeAhead.TryMapKey(", events);
        Assert.DoesNotContain("char.IsLetterOrDigit(letter[0])", events);
    }

    private static string Slice(string text, string start, string end)
    {
        var startIndex = text.IndexOf(start, StringComparison.Ordinal);
        Assert.True(startIndex >= 0, $"Anchor '{start}' not found.");
        var endIndex = text.IndexOf(end, startIndex, StringComparison.Ordinal);
        Assert.True(endIndex > startIndex, $"Anchor '{end}' not found after '{start}'.");
        return text[startIndex..endIndex];
    }
}

public class Sprint2WindowTests
{
    // P2: hiding the preview pane left audio/video playing and the file locked.
    [Fact]
    public void PreviewPane_ReleasesMediaWhenHiddenAndReloadsWhenShown()
    {
        var commands = File.ReadAllText(Path.Combine(Sprint2ShellTests.AppRoot(), "MainWindow.Commands.cs"));
        var apply = Slice(commands, "private void ApplyPreviewVisibility()", "private void ApplyTheme(");
        var media = File.ReadAllText(Path.Combine(Sprint2ShellTests.AppRoot(), "PreviewPresenter.Media.cs"));

        Assert.Contains("_previewPresenter.ReleasePathBackedPreviews();", apply);
        Assert.Contains("RequeueCurrentPreview();", apply);
        Assert.Contains("_mediaSource?.Dispose();", media);
        Assert.Contains(".Pause();", media);
    }

    // T2: the Transfers window re-activated itself on every start/retry/dequeue.
    [Fact]
    public void TransferWindow_ActivatesOnlyWhenCreatedOrExplicitlyOpened()
    {
        var window = File.ReadAllText(Path.Combine(Sprint2ShellTests.AppRoot(), "TransferProgressWindow.xaml.cs"));
        var start = Slice(window, "public void Start(", "private async void OnDefaultsClicked(");
        var ops = File.ReadAllText(Path.Combine(Sprint2ShellTests.AppRoot(), "MainWindow.FileOperations.cs"));

        Assert.DoesNotContain("Activate();", start);
        Assert.Contains("EnsureTransferProgressWindow(out var created)", ops);
        Assert.Contains("if (created || activate)", ops);
    }

    // T4: skipped conflicts were reported as full success.
    [Theory]
    [InlineData(false, 6, 4, "Copied 6 of 10 items to D:\\x (4 skipped)")]
    [InlineData(true, 1, 1, "Moved 1 of 2 items to D:\\x (1 skipped)")]
    [InlineData(false, 6, 0, "Copied 6 items to D:\\x")]
    public void CompletionReceipt_ReportsSkippedItems(bool move, int done, int skipped, string expected)
    {
        Assert.Equal(expected, TransferProgressFormatter.CompletionReceipt(move, done, "D:\\x", skipped));
    }

    // T4: partial failure + retry pushed one undo entry per pass.
    [Fact]
    public void TransferRun_PushesASingleUndoEntryPerOperation()
    {
        var transfer = File.ReadAllText(Path.Combine(Sprint2ShellTests.AppRoot(), "MainWindow.Transfer.cs"));
        var run = Slice(transfer, "private async Task<TransferOperationStatus> RunTransferOperationAsync(", "private static TransferRunStatus TransferStatusToRunStatus(");

        Assert.Single(Regex.Matches(run, @"Undo\.PushMove\("));
        Assert.Single(Regex.Matches(run, @"Undo\.PushCopy\("));
    }

    // T5: undo of a multi-item move that failed midway could never complete afterwards.
    [Fact]
    public async Task PushMove_UndoResumesWithTheRemainingItemsAfterAFailure()
    {
        var log = new List<string>();
        var failOnce = true;
        var ipc = new ConfigurableIpc
        {
            MoveEntryResolvedHandler = (source, destination, action, ct) =>
            {
                var name = Path.GetFileName(source);
                if (name == "b" && failOnce)
                {
                    failOnce = false;
                    log.Add("b:fail");
                    throw new IOException("locked");
                }

                log.Add(name);
                return Task.FromResult(Path.Combine(destination, name));
            },
        };
        var stack = new UndoStack();
        stack.PushMove(
            [
                new TransferResult { Source = @"C:\src\a", Destination = @"D:\dst\a" },
                new TransferResult { Source = @"C:\src\b", Destination = @"D:\dst\b" },
                new TransferResult { Source = @"C:\src\c", Destination = @"D:\dst\c" },
            ],
            new FileOperationService(ipc));

        await Assert.ThrowsAsync<IOException>(() => stack.UndoAsync());
        Assert.True(stack.CanUndo);
        await stack.UndoAsync();

        Assert.Equal(["c", "b:fail", "b", "a"], log);
        Assert.False(stack.CanUndo);
    }

    // C7/S12/P5 + X1: every window sized itself in physical pixels and ignored the app theme.
    [Fact]
    public void Windows_UseSharedDpiAwareChrome()
    {
        var appRoot = Sprint2ShellTests.AppRoot();
        var chrome = File.ReadAllText(Path.Combine(appRoot, "SecondaryWindowChrome.cs"));
        Assert.Contains("GetDpiForWindow", chrome);
        Assert.Contains("RequestedTheme =", chrome);

        foreach (var file in Directory.EnumerateFiles(appRoot, "*.cs"))
        {
            var name = Path.GetFileName(file);
            if (name is "SecondaryWindowChrome.cs" or "MainWindow.UiSmoke.cs")
            {
                continue;
            }

            Assert.False(
                File.ReadAllText(file).Contains("AppWindow.Resize(new SizeInt32(", StringComparison.Ordinal),
                $"{name} sizes its window in physical pixels.");
        }

        foreach (var window in new[] { "SettingsWindow.xaml.cs", "RemoteManagerWindow.xaml.cs", "GitWorkbenchWindow.xaml.cs", "AdvancedRenameDialog.xaml.cs", "DuplicateCheckerDialog.xaml.cs", "TransferProgressWindow.xaml.cs" })
        {
            Assert.Contains("SecondaryWindowChrome.Apply(", File.ReadAllText(Path.Combine(appRoot, window)));
        }

        var commands = File.ReadAllText(Path.Combine(appRoot, "MainWindow.Commands.cs"));
        Assert.Contains("SecondaryWindowChrome.ApplyTheme(next);", commands);
    }

    // S7: every workspace sync closed the InfoBar, including messages it did not open.
    [Fact]
    public void InfoBar_AutoClosesOnlyMessagesTheSyncOpened()
    {
        var mainWindow = File.ReadAllText(Path.Combine(Sprint2ShellTests.AppRoot(), "MainWindow.xaml.cs"));
        Assert.Contains("_messageBarOwnedBySync", mainWindow);
    }

    // S8: the badge always read "Status" because the folder path counted as a status message.
    [Fact]
    public void StatusCenter_ReportsCompletedTransfersBeforeGenericStatus()
    {
        var snapshot = StatusCenterFormatter.Format(0, 0, hasCompletedTransfers: true, listingInProgress: false, workspaceStatus: @"C:\Users\me", operationHistoryCount: 0);
        Assert.Equal("Done", snapshot.BadgeText);

        var statusCenter = File.ReadAllText(Path.Combine(Sprint2ShellTests.AppRoot(), "MainWindow.StatusCenter.cs"));
        Assert.DoesNotContain("_toolbar?.StatusText", statusCenter);
    }

    private static string Slice(string text, string start, string end)
    {
        var startIndex = text.IndexOf(start, StringComparison.Ordinal);
        Assert.True(startIndex >= 0, $"Anchor '{start}' not found.");
        var endIndex = text.IndexOf(end, startIndex, StringComparison.Ordinal);
        Assert.True(endIndex > startIndex, $"Anchor '{end}' not found after '{start}'.");
        return text[startIndex..endIndex];
    }
}
