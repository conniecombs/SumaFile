using SimpleFile.Core;
using Xunit;

namespace SimpleFile.Tests;

public class KeyboardShortcutMapTests
{
    [Fact]
    public void KeyboardShortcuts_IncludeEditableCommandDefaultsAndUnassignedCommands()
    {
        Assert.Contains(KeyboardShortcutMap.Defaults, item =>
            item.Id == "commandPalette.open" && item.Keys == "Ctrl+Shift+P");
        Assert.Contains(KeyboardShortcutMap.Defaults, item =>
            item.Id == "path.focus" && item.Keys == "Ctrl+L, Alt+D");
        Assert.Contains(KeyboardShortcutMap.Defaults, item =>
            item.Id == "pane.switch" && item.Keys == "Tab");
        Assert.Contains(KeyboardShortcutMap.Defaults, item =>
            item.Id == "preview.toggle" && item.Keys == "Alt+P");
        Assert.Contains(KeyboardShortcutMap.Defaults, item =>
            item.Id == "tabs.reopen" && item.Keys == "Unassigned");
        Assert.Contains(KeyboardShortcutMap.Defaults, item =>
            item.Id == "view.toggleHidden" && item.Keys == "Ctrl+H");
        Assert.Contains(KeyboardShortcutMap.Defaults, item =>
            item.Id == "file.properties" && item.Keys == "Alt+Enter");
        Assert.Contains(KeyboardShortcutMap.Defaults, item =>
            item.Id == "tabs.jump" && item.Keys == "Ctrl+1-9" && !item.IsEditable);
    }

    [Fact]
    public void ApplyOverrides_StillAcceptsLegacySingleShortcutValues()
    {
        var remapped = KeyboardShortcutMap.ApplyOverrides(new Dictionary<string, string>
        {
            ["search.focus"] = "Ctrl+K",
        });

        Assert.Equal("Ctrl+K", remapped.Single(item => item.Id == "search.focus").Keys);
        Assert.Equal("F5", remapped.Single(item => item.Id == "directory.refresh").Keys);
    }

    [Fact]
    public void NormalizeOverrides_PreservesClearAndDropsDefaultEquivalentValues()
    {
        var normalized = KeyboardShortcutMap.NormalizeOverrides(new Dictionary<string, List<string>>
        {
            ["search.focus"] = ["ctrl + k", "Ctrl+K"],
            ["preview.toggle"] = ["ctrl+shift+v"],
            ["directory.refresh"] = ["F5"],
            ["tabs.close"] = [],
            ["tabs.jump"] = ["Ctrl+9"],
            ["missing"] = ["Ctrl+M"],
        });

        Assert.Equal(["Ctrl+K"], normalized["search.focus"]);
        Assert.Equal(["Ctrl+Shift+V"], normalized["preview.toggle"]);
        Assert.Equal([], normalized["tabs.close"]);
        Assert.False(normalized.ContainsKey("directory.refresh"));
        Assert.False(normalized.ContainsKey("tabs.jump"));
        Assert.False(normalized.ContainsKey("missing"));
    }

    [Fact]
    public void ImportExport_RoundTripsMultipleShortcuts()
    {
        var json = KeyboardShortcutExportDocument.ToJson(new Dictionary<string, List<string>>
        {
            ["search.focus"] = ["Ctrl+K", "Ctrl+F"],
            ["tabs.reopen"] = ["Ctrl+Shift+T"],
        });

        var imported = KeyboardShortcutExportDocument.FromJson(json);

        Assert.Equal(["Ctrl+K", "Ctrl+F"], imported["search.focus"]);
        Assert.Equal(["Ctrl+Shift+T"], imported["tabs.reopen"]);
    }

    [Fact]
    public void ImportExport_RoundTripsPunctuationChords()
    {
        var json = KeyboardShortcutExportDocument.ToJson(new Dictionary<string, List<string>>
        {
            ["search.focus"] = ["Ctrl+`", "Ctrl+,"],
            ["tabs.reopen"] = ["Ctrl+/"],
        });

        var imported = KeyboardShortcutExportDocument.FromJson(json);

        Assert.Equal(["Ctrl+`", "Ctrl+,"], imported["search.focus"]);
        Assert.Equal(["Ctrl+/"], imported["tabs.reopen"]);
    }

    [Fact]
    public void ValidateOverrides_FindsConflictsAndReservedWindowsShortcuts()
    {
        var issues = KeyboardShortcutMap.ValidateOverrides(new Dictionary<string, List<string>>
        {
            ["search.focus"] = ["Ctrl+L"],
            ["tabs.reopen"] = ["Alt+F4"],
        });

        Assert.Contains(issues, issue =>
            issue.Severity == KeyboardShortcutIssueSeverity.Error
            && issue.CommandId == "search.focus"
            && issue.Shortcut == "Ctrl+L");
        Assert.Contains(issues, issue =>
            issue.Severity == KeyboardShortcutIssueSeverity.Error
            && issue.CommandId == "path.focus"
            && issue.Shortcut == "Ctrl+L");
        Assert.Contains(issues, issue =>
            issue.Severity == KeyboardShortcutIssueSeverity.Warning
            && issue.CommandId == "tabs.reopen"
            && issue.Shortcut == "Alt+F4");
    }

    [Theory]
    [InlineData("tabs.new", "Ctrl+T, Ctrl+N")]
    [InlineData("nav.parent", "Alt+Up")]
    [InlineData("nav.back", "Alt+Left, Backspace")]
    [InlineData("terminal.open", "Ctrl+`")]
    [InlineData("settings.open", "Ctrl+,")]
    [InlineData("help.keyboard", "F1, Ctrl+/")]
    [InlineData("preview.toggle", "Alt+P")]
    [InlineData("file.newFile", "Unassigned")]
    public void Defaults_MatchUxRecommendations(string id, string keys)
    {
        Assert.Equal(keys, KeyboardShortcutMap.Find(id)!.Keys);
    }

    [Theory]
    [InlineData("Ctrl+`", "Oem3")]
    [InlineData("Ctrl+backtick", "Oem3")]
    [InlineData("Ctrl+oem3", "Oem3")]
    [InlineData("Ctrl+,", "OemComma")]
    [InlineData("Ctrl+comma", "OemComma")]
    [InlineData("Ctrl+/", "Oem2")]
    [InlineData("Ctrl+slash", "Oem2")]
    [InlineData("Ctrl+Divide", "Divide")]
    public void TryParseShortcut_NormalizesPunctuationKeys(string text, string key)
    {
        Assert.True(KeyboardShortcutMap.TryParseShortcut(text, out var gesture, out _));
        Assert.Equal(key, gesture!.Key);
        Assert.True(gesture.Control);
    }

    [Theory]
    [InlineData("pane.switch", true)]
    [InlineData("quickLook.toggle", true)]
    [InlineData("nav.back", true)]
    [InlineData("file.copy", false)]
    [InlineData("nav.parent", false)]
    public void IsFileListScopedKey_FlagsBareKeyCommands(string id, bool expected)
    {
        Assert.Equal(expected, KeyboardShortcutMap.IsFileListScopedKey(id));
    }
}
