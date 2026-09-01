using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SimpleFile.Core;
using SimpleFile.Ipc;
using Xunit;
using DriveInfo = SimpleFile.Ipc.DriveInfo;

namespace SimpleFile.Tests;

public class KeyboardShortcutMapTests
{
    [Fact]
    public void KeyboardShortcuts_IncludePaletteAndAllowOverrides()
    {
        Assert.Contains(KeyboardShortcutMap.Defaults, item => item.Id == "commandPalette.open" && item.Keys == "Ctrl+Shift+P");
        Assert.Contains(KeyboardShortcutMap.Defaults, item => item.Id == "pane.switch" && item.Keys == "Tab");
        Assert.Contains(KeyboardShortcutMap.Defaults, item => item.Id == "pane.toggleDual" && item.Label == "Open or close second pane");
        Assert.Contains(KeyboardShortcutMap.Defaults, item => item.Id == "view.toggleHidden" && item.Keys == "Ctrl+H");
        Assert.Contains(KeyboardShortcutMap.Defaults, item => item.Id == "file.properties" && item.Keys == "Alt+Enter");
        Assert.Contains(KeyboardShortcutMap.Defaults, item => item.Id == "tabs.jump" && item.Keys == "Ctrl+1–9");
        var remapped = KeyboardShortcutMap.ApplyOverrides(new Dictionary<string, string>
        {
            ["search.focus"] = "Ctrl+K",
        });
        Assert.Equal("Ctrl+K", remapped.Single(item => item.Id == "search.focus").Keys);
        Assert.Equal("F5", remapped.Single(item => item.Id == "directory.refresh").Keys);
    }
}
