using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using SimpleFile.Core;
using Xunit;

namespace SimpleFile.Tests;

/// <summary>S6 / C4 / C5: every displayed shortcut derives from the effective keyboard map.</summary>
public class ShortcutLabelTests
{
    [Theory]
    [InlineData("rename", "file.rename")]
    [InlineData("delete", "file.delete.trash")]
    [InlineData("go-home", "nav.home")]
    [InlineData("clipboard-history", "clipboard.history")]
    [InlineData("switch-pane", "pane.switch")]
    [InlineData("settings", "settings.open")]
    public void CommandShortcutMap_LinksCommandsToShortcutIds(string commandId, string shortcutId)
    {
        Assert.Equal(shortcutId, CommandShortcutMap.ShortcutIdFor(commandId));
    }

    [Fact]
    public void EveryCatalogCommandWithAShortcut_HasABinding()
    {
        foreach (var command in AppCommandCatalog.All.Where(command => !string.IsNullOrEmpty(command.Shortcut)))
        {
            var shortcutId = CommandShortcutMap.ShortcutIdFor(command.Id);
            Assert.True(shortcutId is not null, $"'{command.Id}' advertises '{command.Shortcut}' but maps to no shortcut id.");
            Assert.True(KeyboardShortcutMap.Find(shortcutId!) is not null, $"'{command.Id}' maps to unknown shortcut '{shortcutId}'.");
        }
    }

    [Fact]
    public void WithEffectiveShortcuts_ReflectsOverridesAndClearedBindings()
    {
        var overrides = new Dictionary<string, List<string>>(StringComparer.Ordinal)
        {
            ["file.rename"] = ["Ctrl+R"],
            ["file.delete.trash"] = [],
        };

        var commands = AppCommandCatalog.WithEffectiveShortcuts(AppCommandCatalog.All, overrides);

        Assert.Equal("Ctrl+R", commands.Single(command => command.Id == "rename").Shortcut);
        Assert.True(string.IsNullOrEmpty(commands.Single(command => command.Id == "delete").Shortcut));
        Assert.Equal("Alt+Up, Backspace", commands.Single(command => command.Id == "go-up").Shortcut);
        Assert.Null(commands.Single(command => command.Id == "transfers").Shortcut);
    }

    [Fact]
    public void Defaults_BindGoHomeAndClipboardHistory()
    {
        Assert.Contains("Alt+Home", KeyboardShortcutMap.Find("nav.home")!.DefaultShortcuts);
        Assert.Contains("Ctrl+Shift+V", KeyboardShortcutMap.Find("clipboard.history")!.DefaultShortcuts);
    }

    [Fact]
    public void EmptyPaneHints_UseTheEffectiveShortcutOrNone()
    {
        var empty = EmptyPaneState.Resolve(0, 0, false, false, null, null, true, @"C:\x", newFolderShortcut: "Ctrl+Alt+N");
        Assert.Contains("Ctrl+Alt+N", empty.Hint);

        var unbound = EmptyPaneState.Resolve(0, 0, false, false, null, null, true, @"C:\x", newFolderShortcut: "");
        Assert.DoesNotContain("press", unbound.Hint);

        var hidden = EmptyPaneState.Resolve(0, 3, false, false, null, null, false, @"C:\x", toggleHiddenShortcut: "Ctrl+Alt+H");
        Assert.Contains("Ctrl+Alt+H", hidden.Hint);
    }

    [Fact]
    public void Shell_DerivesDisplayedShortcutsFromTheEffectiveMap()
    {
        var shortcuts = UiAuditSprint3Tests.Read("MainWindow.Shortcuts.cs");
        Assert.Contains("case \"nav.home\":", shortcuts);
        Assert.Contains("case \"clipboard.history\":", shortcuts);

        var commands = UiAuditSprint3Tests.Read("MainWindow.Commands.cs");
        Assert.Contains("WithEffectiveShortcuts(", commands);
        Assert.Contains("ShortcutLabelFor(entry.Id)", commands);
        Assert.Contains("ShortcutLabelFor(action.Id)", UiAuditSprint3Tests.Read("MainWindow.CommandSurface.cs"));

        var toolbar = UiAuditSprint3Tests.Read("PrimaryToolbarView.xaml");
        Assert.DoesNotContain("(F6)", toolbar);
        Assert.DoesNotContain("(Ctrl+Shift+S)", toolbar);
        Assert.DoesNotMatch(new Regex("KeyboardAcceleratorTextOverride=\"(Ctrl|Alt|Shift|F[0-9])"), toolbar);
    }
}
