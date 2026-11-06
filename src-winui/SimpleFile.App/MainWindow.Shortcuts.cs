using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using SimpleFile.Core;
using Windows.System;

namespace SimpleFile.App;

public sealed partial class MainWindow
{
    private void ApplyKeyboardShortcuts()
    {
        RootGrid.KeyboardAccelerators.Clear();

        var assignments = KeyboardShortcutMap.EffectiveShortcuts(_workspace?.Settings.ShortcutOverrides);
        var registered = new HashSet<(string Command, VirtualKey Key, VirtualKeyModifiers Modifiers)>();
        foreach (var assignment in assignments.Where(item => item.IsEditable))
        {
            foreach (var shortcut in assignment.Shortcuts)
            {
                if (assignment.Id == "file.open"
                    && !assignment.IsModified
                    && string.Equals(shortcut, "Enter", StringComparison.Ordinal))
                {
                    continue;
                }

                if (TryCreateKeyboardAccelerator(assignment.Id, shortcut, out var accelerator)
                    && registered.Add((assignment.Id, accelerator.Key, accelerator.Modifiers)))
                {
                    RootGrid.KeyboardAccelerators.Add(accelerator);
                }

                // "/" on the main keyboard (Oem2) also answers on the numpad (Divide), e.g. Ctrl+/ for help.
                if (KeyboardShortcutMap.TryParseShortcut(shortcut, out var gesture, out _)
                    && gesture is not null
                    && string.Equals(gesture.Key, "Oem2", StringComparison.Ordinal))
                {
                    var modifiers = ToVirtualKeyModifiers(gesture);
                    if (registered.Add((assignment.Id, VirtualKey.Divide, modifiers)))
                    {
                        RootGrid.KeyboardAccelerators.Add(CreateCommandAccelerator(assignment.Id, VirtualKey.Divide, modifiers));
                    }
                }
            }
        }

        AddFixedTabJumpAccelerators();
        ApplyShortcutHints();
    }

    /// <summary>Toolbar tooltips and menu chords follow the effective map, including remaps.</summary>
    private void ApplyShortcutHints()
    {
        ToolTipService.SetToolTip(PrimarySettingsButton, WithShortcutSuffix("Settings", "settings"));
        ToolTipService.SetToolTip(DualPaneButton, WithShortcutSuffix("Open second pane", "dual-pane"));
        NewFolderMenuItem.KeyboardAcceleratorTextOverride = ShortcutLabelFor("new-folder") ?? "";
        NewTextMenuItem.KeyboardAcceleratorTextOverride = ShortcutLabelFor("new-file") ?? "";
    }

    private string WithShortcutSuffix(string label, string commandId) =>
        ShortcutLabelFor(commandId) is { Length: > 0 } keys ? $"{label} ({keys})" : label;

    /// <summary>
    /// The chord to display for a palette/toolbar/context-menu id, resolved through the alias
    /// table and the effective shortcut map. Null when the command has no shortcut binding.
    /// </summary>
    private string? ShortcutLabelFor(string commandOrMenuId)
    {
        var commandId = CommandAliasCatalog.Normalize(commandOrMenuId);
        var shortcutId = CommandShortcutMap.ShortcutIdFor(commandId);
        if (shortcutId is null)
        {
            return null;
        }

        var assignment = KeyboardShortcutMap.EffectiveShortcuts(_workspace?.Settings.ShortcutOverrides)
            .FirstOrDefault(candidate => string.Equals(candidate.Id, shortcutId, StringComparison.Ordinal));
        return assignment is { Shortcuts.Count: > 0 } ? assignment.Keys : "";
    }

    private bool TryCreateKeyboardAccelerator(
        string commandId,
        string shortcut,
        out KeyboardAccelerator accelerator)
    {
        accelerator = new KeyboardAccelerator();
        if (!KeyboardShortcutMap.TryParseShortcut(shortcut, out var gesture, out _) || gesture is null)
        {
            return false;
        }

        if (!TryMapVirtualKey(gesture.Key, out var key))
        {
            return false;
        }

        accelerator = CreateCommandAccelerator(commandId, key, ToVirtualKeyModifiers(gesture));
        return true;
    }

    private KeyboardAccelerator CreateCommandAccelerator(string commandId, VirtualKey key, VirtualKeyModifiers modifiers)
    {
        var accelerator = new KeyboardAccelerator
        {
            Key = key,
            Modifiers = modifiers,
        };
        accelerator.Invoked += async (_, args) =>
        {
            if (!CanRunShortcutNow(commandId, modifiers))
            {
                // Leave the key to the focused control (text editing, focus traversal).
                return;
            }

            args.Handled = true;
            await RunKeyboardShortcutAsync(commandId);
        };
        return accelerator;
    }

    /// <summary>
    /// Decided synchronously before the accelerator claims the key: editing and destructive
    /// commands yield to a focused text input, and pane switching yields when there is one pane.
    /// </summary>
    private bool CanRunShortcutNow(string commandId, VirtualKeyModifiers modifiers)
    {
        if (DialogHost.IsDialogOpen)
        {
            return false;
        }

        // A file-list-scoped command's bare key is kept out of text boxes by the scope check
        // below; its modified chords (Alt+Left for Back) work everywhere, text boxes included.
        var bareKey = (modifiers & ~VirtualKeyModifiers.Shift) == VirtualKeyModifiers.None;
        var scopedChord = !bareKey && KeyboardShortcutMap.IsFileListScopedKey(commandId);
        if (KeyboardShortcutMap.IsSuppressedWhileEditingText(commandId)
            && !scopedChord
            && (IsEditingPath || IsTextInputFocused()))
        {
            return false;
        }

        if (commandId == "pane.switch" && _workspace?.DualPaneEnabled != true)
        {
            return false;
        }

        // Bare Tab, Space and Backspace (Shift alone still counts as bare) belong to the file
        // list; elsewhere they keep their normal meaning (focus traversal, pressing a button,
        // editing text). Ctrl/Alt/Win chords (Alt+Left for Back) stay window-wide.
        if (bareKey
            && KeyboardShortcutMap.IsFileListScopedKey(commandId)
            && !IsFileListFocused())
        {
            return false;
        }

        return true;
    }

    /// <summary>True when keyboard focus is inside one of the panes' file lists (details or list views).</summary>
    private bool IsFileListFocused()
    {
        if (Content?.XamlRoot is not { } root)
        {
            return false;
        }

        var lists = new DependencyObject[]
        {
            PrimaryDetailsFileList,
            SecondaryDetailsFileList,
            PrimaryFileList,
            SecondaryFileList,
        };
        for (var current = FocusManager.GetFocusedElement(root) as DependencyObject;
            current is not null;
            current = VisualTreeHelper.GetParent(current))
        {
            if (current is TextBox or PasswordBox or RichEditBox)
            {
                return false;
            }

            foreach (var list in lists)
            {
                if (ReferenceEquals(current, list))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private void AddFixedTabJumpAccelerators()
    {
        for (var index = 1; index <= 9; index++)
        {
            var tabIndex = index;
            var accelerator = new KeyboardAccelerator
            {
                Key = index switch
                {
                    1 => VirtualKey.Number1,
                    2 => VirtualKey.Number2,
                    3 => VirtualKey.Number3,
                    4 => VirtualKey.Number4,
                    5 => VirtualKey.Number5,
                    6 => VirtualKey.Number6,
                    7 => VirtualKey.Number7,
                    8 => VirtualKey.Number8,
                    _ => VirtualKey.Number9,
                },
                Modifiers = VirtualKeyModifiers.Control,
            };
            accelerator.Invoked += async (_, args) =>
            {
                args.Handled = true;
                if (_workspace is not null && !IsEditingPath)
                {
                    await RunUiActionAsync("Tab", () => _workspace.SwitchToTabAtAsync(tabIndex));
                }
            };
            RootGrid.KeyboardAccelerators.Add(accelerator);
        }
    }

    private async Task RunKeyboardShortcutAsync(string commandId)
    {
        switch (commandId)
        {
            case "commandPalette.open":
                if (!IsEditingPath && !IsTextInputFocused())
                {
                    OpenCommandPalette();
                }

                break;
            case "path.focus":
                FocusOmnibar(OmnibarMode.Navigate);
                break;
            case "search.focus":
                FocusSearchUi();
                break;
            case "selection.all":
                if (!IsEditingPath && !IsTextInputFocused())
                {
                    SelectAllActiveFileList();
                }

                break;
            case "file.copyPath":
                await RunUiActionAsync("Copy path", () => RunAppCommandAsync("copy-path"));
                break;
            case "history.undo":
                await RunUiActionAsync("Undo", UndoLastAsync);
                break;
            case "history.redo":
                await RunUiActionAsync("Redo", RedoLastAsync);
                break;
            case "help.keyboard":
                await RunUiActionAsync("Keyboard shortcuts", ShowKeyboardHelpAsync);
                break;
            case "pane.copyToOther":
                await RunUiActionAsync("Copy to other pane", () => RunAppCommandAsync("copy-to-pane"));
                break;
            case "pane.moveToOther":
                await RunUiActionAsync("Move to other pane", () => RunAppCommandAsync("move-to-pane"));
                break;
            case "file.openTab":
                await RunUiActionAsync("Open in new tab", () => RunAppCommandAsync("open-selected-tab"));
                break;
            case "view.toggleHidden":
                await RunUiActionAsync("Hidden files", () => RunAppCommandAsync("toggle-hidden"));
                break;
            case "preview.toggle":
                await RunUiActionAsync("Preview pane", TogglePreviewPaneAsync);
                break;
            case "places.bookmark":
                await RunUiActionAsync("Bookmark", () => RunAppCommandAsync("bookmark-folder"));
                break;
            case "file.properties":
                await RunUiActionAsync("Properties", () => RunAppCommandAsync("properties"));
                break;
            case "directory.properties":
                await RunUiActionAsync("Folder properties", () => RunAppCommandAsync("folder-properties"));
                break;
            case "directory.refresh":
                if (_workspace is not null && !IsEditingPath)
                {
                    await RunUiActionAsync("Refresh", () => _workspace.RefreshAsync());
                }

                break;
            case "pane.toggleDual":
                await RunUiActionAsync("Dual pane", ToggleDualPaneFromUiAsync);
                break;
            case "nav.back":
                if (_workspace is not null && !IsEditingPath)
                {
                    await RunUiActionAsync("Navigation", () => _workspace.GoBackAsync());
                }

                break;
            case "nav.forward":
                if (_workspace is not null && !IsEditingPath)
                {
                    await RunUiActionAsync("Navigation", () => _workspace.GoForwardAsync());
                }

                break;
            case "nav.home":
                await RunUiActionAsync("Navigation", () => RunAppCommandAsync("go-home"));
                break;
            case "clipboard.history":
                await RunUiActionAsync("Clipboard history", () => RunAppCommandAsync("clipboard-history"));
                break;
            case "nav.parent":
                if (_workspace is not null && !IsEditingPath)
                {
                    await RunUiActionAsync("Navigation", () => _workspace.GoUpAsync());
                }

                break;
            case "pane.focusPrimary":
                _workspace?.ActivatePane(PaneId.Primary);
                break;
            case "pane.focusSecondary":
                if (_workspace is not null)
                {
                    await RunUiActionAsync("Focus pane", () => _workspace.FocusSecondaryAsync());
                }

                break;
            case "pane.switch":
                if (_workspace?.DualPaneEnabled == true && !IsEditingPath && !IsTextInputFocused())
                {
                    _workspace.SwitchActivePane();
                }

                break;
            case "tabs.new":
                if (_workspace is not null && !IsEditingPath)
                {
                    await RunUiActionAsync("Tab", () => _workspace.OpenNewTabAsync());
                }

                break;
            case "tabs.close":
                await CloseActiveTabFromShortcutAsync();
                break;
            case "tabs.reopen":
                if (_workspace is not null && !IsEditingPath)
                {
                    await RunUiActionAsync("Tab", () => _workspace.ReopenClosedTabAsync());
                }

                break;
            case "tabs.next":
                if (_workspace is not null && !IsEditingPath)
                {
                    await RunUiActionAsync("Tab", () => _workspace.SwitchTabByAsync(1));
                }

                break;
            case "tabs.previous":
                if (_workspace is not null && !IsEditingPath)
                {
                    await RunUiActionAsync("Tab", () => _workspace.SwitchTabByAsync(-1));
                }

                break;
            case "file.open":
                if (!IsEditingPath && !IsTextInputFocused())
                {
                    await RunUiActionAsync("Open", () => OpenSelectedFile(_workspace?.ActivePane ?? PaneId.Primary));
                }

                break;
            case "file.rename":
                // Details view renames in place; other views and multi-selection use the dialog.
                if (!TryBeginInlineRename())
                {
                    await RunUiActionAsync("Rename", PromptAndRename);
                }

                break;
            case "file.delete.permanent":
                await RunUiActionAsync("Delete", DeleteSelected);
                break;
            case "file.delete.trash":
                await RunUiActionAsync("Trash", TrashSelected);
                break;
            case "file.copy":
                if (!IsEditingPath && !IsTextInputFocused())
                {
                    await RunUiActionAsync("Copy", CopyToClipboard);
                }

                break;
            case "file.cut":
                if (!IsEditingPath && !IsTextInputFocused())
                {
                    await RunUiActionAsync("Cut", CutToClipboard);
                }

                break;
            case "file.paste":
                if (!IsEditingPath && !IsTextInputFocused())
                {
                    await RunUiActionAsync("Paste", () => PasteFromClipboard());
                }

                break;
            case "file.newFolder":
                await RunUiActionAsync(
                    "New Folder",
                    () => CreateNewItem(_workspace?.ActivePane ?? PaneId.Primary, NewItemTemplate.Folder));
                break;
            case "file.newShortcut":
                await RunUiActionAsync(
                    "New Shortcut",
                    () => PromptAndCreateShortcut(_workspace?.ActivePane ?? PaneId.Primary));
                break;
            case "file.newFile":
                await RunUiActionAsync(
                    "New Text Document",
                    () => CreateNewItem(_workspace?.ActivePane ?? PaneId.Primary, NewItemTemplate.TextFile));
                break;
            case "quickLook.toggle":
                if (!IsEditingPath && !IsTextInputFocused())
                {
                    await RunUiActionAsync("Quick Look", ShowQuickLookAsync);
                }

                break;
            case "terminal.open":
                await RunUiActionAsync("Terminal", OpenTerminalInActivePathAsync);
                break;
            case "settings.open":
                await RunUiActionAsync("Settings", ShowSettingsAsync);
                break;
        }
    }

    private async Task CloseActiveTabFromShortcutAsync()
    {
        if (_workspace is null || IsEditingPath)
        {
            return;
        }

        var id = _workspace.Active.ActiveTabId;
        if (id is not null)
        {
            await RunUiActionAsync("Tab", () => _workspace.CloseTabAsync(id, _workspace.ActivePane));
        }
    }

    private async Task TogglePreviewPaneAsync()
    {
        var workspace = _workspace;
        if (workspace is null)
        {
            return;
        }

        workspace.Settings.PreviewVisible = !workspace.Settings.PreviewVisible;
        ApplyPreviewVisibility();
        await workspace.SaveUiSettingsAsync();
    }

    private static VirtualKeyModifiers ToVirtualKeyModifiers(KeyboardShortcutGesture gesture)
    {
        var modifiers = VirtualKeyModifiers.None;
        if (gesture.Control)
        {
            modifiers |= VirtualKeyModifiers.Control;
        }

        if (gesture.Alt)
        {
            modifiers |= VirtualKeyModifiers.Menu;
        }

        if (gesture.Shift)
        {
            modifiers |= VirtualKeyModifiers.Shift;
        }

        if (gesture.Windows)
        {
            modifiers |= VirtualKeyModifiers.Windows;
        }

        return modifiers;
    }

    private static bool TryMapVirtualKey(string key, out VirtualKey virtualKey)
    {
        virtualKey = key switch
        {
            "0" => VirtualKey.Number0,
            "1" => VirtualKey.Number1,
            "2" => VirtualKey.Number2,
            "3" => VirtualKey.Number3,
            "4" => VirtualKey.Number4,
            "5" => VirtualKey.Number5,
            "6" => VirtualKey.Number6,
            "7" => VirtualKey.Number7,
            "8" => VirtualKey.Number8,
            "9" => VirtualKey.Number9,
            "Backspace" => VirtualKey.Back,
            "Plus" => VirtualKey.Add,
            "Minus" => VirtualKey.Subtract,
            "Comma" => VirtualKey.Separator,
            "Period" => VirtualKey.Decimal,
            "Slash" => VirtualKey.Divide,
            "Backslash" => VirtualKey.Divide,
            // OEM keys on the main keyboard: ` (terminal), , (settings), / (help).
            "Oem3" or "Backquote" or "Backtick" or "Grave" => (VirtualKey)192,
            "OemComma" => (VirtualKey)188,
            "Oem2" or "OemQuestion" => (VirtualKey)191,
            _ => Enum.TryParse<VirtualKey>(key, out var parsed) ? parsed : default,
        };

        return virtualKey != default;
    }
}
