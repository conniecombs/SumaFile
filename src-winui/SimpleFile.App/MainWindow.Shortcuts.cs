using Microsoft.UI.Xaml.Input;
using SimpleFile.Core;
using Windows.System;

namespace SimpleFile.App;

public sealed partial class MainWindow
{
    private void ApplyKeyboardShortcuts()
    {
        RootGrid.KeyboardAccelerators.Clear();

        var assignments = KeyboardShortcutMap.EffectiveShortcuts(_workspace?.Settings.ShortcutOverrides);
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

                if (TryCreateKeyboardAccelerator(assignment.Id, shortcut, out var accelerator))
                {
                    RootGrid.KeyboardAccelerators.Add(accelerator);
                }
            }
        }

        AddFixedTabJumpAccelerators();
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
            args.Handled = true;
            await RunKeyboardShortcutAsync(commandId);
        };
        return accelerator;
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
                BeginPathEdit(_workspace?.ActivePane ?? PaneId.Primary);
                break;
            case "search.focus":
                FocusSearchUi();
                break;
            case "selection.all":
                if (!IsEditingPath && !IsTextInputFocused())
                {
                    ActiveFileList.SelectAll();
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
                    await RunUiActionAsync("Open", () => OpenSelectedFile(ActiveFileList, _workspace?.ActivePane ?? PaneId.Primary));
                }

                break;
            case "file.rename":
                await RunUiActionAsync("Rename", PromptAndRename);
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
            _ => Enum.TryParse<VirtualKey>(key, out var parsed) ? parsed : default,
        };

        return virtualKey != default;
    }
}
