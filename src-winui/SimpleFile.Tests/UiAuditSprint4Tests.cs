using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using SimpleFile.Core;
using Xunit;

namespace SimpleFile.Tests;

/// <summary>Sprint 4 guards for docs/UI_AUDIT_2026-11-05.md (structure).</summary>
public class UiAuditSprint4Tests
{
    // S18 / F28: PrimaryPaneView and SecondaryPaneView were verbatim copies.
    [Fact]
    public void Panes_ShareOneViewParameterisedByPaneId()
    {
        var appRoot = UiAuditSprint3Tests.AppRoot();
        Assert.True(File.Exists(Path.Combine(appRoot, "PaneView.xaml")));
        Assert.False(File.Exists(Path.Combine(appRoot, "PrimaryPaneView.xaml")));
        Assert.False(File.Exists(Path.Combine(appRoot, "SecondaryPaneView.xaml")));

        var mainWindow = UiAuditSprint3Tests.Read("MainWindow.xaml");
        Assert.Equal(2, Regex.Matches(mainWindow, @"<local:PaneView\b").Count);
        Assert.Contains("Pane=\"Primary\"", mainWindow);
        Assert.Contains("Pane=\"Secondary\"", mainWindow);

        var code = UiAuditSprint3Tests.Read("PaneView.xaml.cs");
        Assert.Contains("public static readonly DependencyProperty PaneProperty", code);
        Assert.Contains("x:Class=\"SimpleFile.App.PaneView\"", UiAuditSprint3Tests.Read("PaneView.xaml"));
    }
}

/// <summary>C21: command ids live in one place and every surface that emits them resolves.</summary>
public class CommandIdConsistencyTests
{
    private static HashSet<string> CatalogIds() =>
        AppCommandCatalog.All.Select(command => command.Id).ToHashSet(StringComparer.Ordinal);

    private static HashSet<string> HandlerIds()
    {
        var routing = UiAuditSprint3Tests.Read("MainWindow.CommandRouting.cs");
        return Regex.Matches(routing, @"\[""([a-z0-9:-]+)""\] = ")
            .Select(match => match.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static HashSet<string> ContextSwitchIds()
    {
        var commands = UiAuditSprint3Tests.Read("MainWindow.Commands.cs");
        var router = UiAuditSprint3Tests.Slice(commands, "private async Task RunContextCommandAsync(", "private async void OnRootPreviewKeyDown(");
        return Regex.Matches(router, "case \"([a-z0-9:-]+)\":")
            .Select(match => match.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
    }

    [Fact]
    public void CommandIds_CoverTheWholeCatalogAndNothingElse()
    {
        var constants = typeof(CommandIds)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(CatalogIds(), constants);
    }

    [Fact]
    public void EveryCatalogCommand_HasAnAppHandler()
    {
        var missing = CatalogIds().Except(HandlerIds()).OrderBy(id => id).ToList();
        Assert.True(missing.Count == 0, "Palette commands with no handler: " + string.Join(", ", missing));
    }

    [Fact]
    public void EveryAliasTarget_HasAnAppHandler()
    {
        var handlers = HandlerIds();
        var catalog = UiAuditSprint3Tests.Read(Path.Combine("..", "SimpleFile.Core", "CommandAliasCatalog.cs"));
        var targets = Regex.Matches(catalog, @"\] = ""([a-z0-9-]+)""")
            .Select(match => match.Groups[1].Value)
            .Distinct();
        var missing = targets.Where(target => !handlers.Contains(target)).ToList();
        Assert.True(missing.Count == 0, "Alias targets with no handler: " + string.Join(", ", missing));
    }

    [Fact]
    public void EveryContextMenuId_Routes()
    {
        var handlers = HandlerIds();
        var contextCases = ContextSwitchIds();
        var requests = new[]
        {
            new ContextMenuRequest(),
            new ContextMenuRequest { SelectionCount = 1, SelectedIsDirectory = false, AllSelectedAreFiles = true, SelectedIsArchive = true, ArchiveExtractFolderName = "pack", DualPaneEnabled = true, OtherPaneHasPath = true, HasClipboard = true },
            new ContextMenuRequest { SelectionCount = 2, AllSelectedAreFiles = true },
            new ContextMenuRequest { SelectionCount = 1, SelectedIsDirectory = true, InRecycleBin = true },
        };
        var unresolved = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var request in requests)
        {
            foreach (var entry in Flatten(ContextMenuBuilder.Build(request)).Concat(Flatten(ContextMenuBuilder.BuildPaneMoreMenu(request))))
            {
                if (entry.Kind != ContextMenuKind.Item || entry.Children.Count > 0)
                {
                    continue;
                }

                var id = entry.Id;
                if (id.StartsWith("new:", StringComparison.Ordinal) || id.StartsWith("view:", StringComparison.Ordinal)
                    || id.StartsWith("icon:", StringComparison.Ordinal) || id.StartsWith("profile:", StringComparison.Ordinal)
                    || id == "pane:dual" || id.StartsWith("ctx-open-with-app-", StringComparison.Ordinal))
                {
                    continue;
                }

                var normalized = CommandAliasCatalog.Normalize(id);
                if (!handlers.Contains(normalized) && !contextCases.Contains(id))
                {
                    unresolved.Add(id);
                }
            }
        }

        Assert.True(unresolved.Count == 0, "Context menu ids that reach no handler: " + string.Join(", ", unresolved));
    }

    private static IEnumerable<ContextMenuEntry> Flatten(IEnumerable<ContextMenuEntry> entries)
    {
        foreach (var entry in entries)
        {
            yield return entry;
            foreach (var child in Flatten(entry.Children))
            {
                yield return child;
            }
        }
    }
}
