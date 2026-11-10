using SimpleFile.Core;
using Xunit;

namespace SimpleFile.Tests;

[Collection("Plugin catalogs")]
public class CommandSurfaceLayoutTests
{
    [Fact]
    public void FromJson_SanitizesToolbarLayout()
    {
        var layout = CommandSurfaceLayout.FromJson("""
            {
              "version": 99,
              "toolbarDisplayMode": "labels",
              "primaryToolbar": [
                { "kind": "separator" },
                { "kind": "command", "id": "settings" },
                { "kind": "command", "id": "missing" },
                { "kind": "separator" },
                { "kind": "separator" },
                { "kind": "command", "id": "copy" },
                { "kind": "command", "id": "copy" },
                { "kind": "separator" }
              ]
            }
            """);

        Assert.Equal(CommandSurfaceLayout.CurrentVersion, layout.Version);
        Assert.Equal(ToolbarActionCatalog.IconAndLabelDisplayMode, layout.ToolbarDisplayMode);
        Assert.Equal(["settings", "copy"], layout.VisiblePrimaryActionIds());
        Assert.Collection(
            layout.PrimaryToolbar,
            item => Assert.Equal("settings", item.Id),
            item => Assert.True(item.IsSeparator),
            item => Assert.Equal("copy", item.Id));
    }

    [Fact]
    public void FromJson_DropsTheRetiredCoreGitButtons()
    {
        // A custom toolbar saved before phase 4 loses its core git-* buttons; the plugin's own item stays.
        var layout = CommandSurfaceLayout.FromJson("""
            {
              "toolbarDisplayMode": "icons",
              "primaryToolbar": [
                { "kind": "command", "id": "copy" },
                { "kind": "command", "id": "git-panel" },
                { "kind": "command", "id": "git-pull" },
                { "kind": "command", "id": "plugin:com.sumafile.git:git-panel" }
              ]
            }
            """);

        Assert.Equal(["copy", "plugin:com.sumafile.git:git-panel"], layout.PrimaryToolbar.Select(item => item.Id));
    }

    [Fact]
    public void Catalog_IncludesBuiltInAndCommandPaletteActions()
    {
        Assert.True(ToolbarActionCatalog.Find(ToolbarOverflowPlanner.New)?.UsesBuiltInControl);
        Assert.True(ToolbarActionCatalog.Find(ToolbarOverflowPlanner.Settings)?.UsesBuiltInControl);
        Assert.Equal("Copy", ToolbarActionCatalog.Find("copy")?.Label);
        Assert.Equal("Tools", ToolbarActionCatalog.Find("terminal")?.Group);
        // The Git toggle is the Git plugin's toolbar item since phase 4.
        Assert.Null(ToolbarActionCatalog.Find("git-panel"));
        Assert.DoesNotContain(ToolbarActionCatalog.All, action => action.PluginId is null && action.Group == "Git");
        Assert.Null(ToolbarActionCatalog.Find("search"));
        Assert.Null(ToolbarActionCatalog.Find("filter"));
        Assert.Equal(ToolbarActionCatalog.IconOnlyDisplayMode, ToolbarActionCatalog.NormalizeDisplayMode("wide"));
        Assert.True(ToolbarActionCatalog.WidthFor("copy", ToolbarActionCatalog.IconAndLabelDisplayMode) > 32);
    }

    [Fact]
    public void CreateDefault_PrioritizesEverydayActionsWithLabels()
    {
        var layout = CommandSurfaceLayout.CreateDefault();

        Assert.Equal(
            [
                ToolbarOverflowPlanner.New,
                ToolbarOverflowPlanner.DualPane,
                "copy-to-pane",
                "move-to-pane",
                ToolbarOverflowPlanner.ViewOptions,
                ToolbarOverflowPlanner.Settings,
            ],
            layout.VisiblePrimaryActionIds());
        Assert.Collection(
            layout.PrimaryToolbar,
            item => Assert.Equal(ToolbarOverflowPlanner.New, item.Id),
            item => Assert.Equal(ToolbarOverflowPlanner.DualPane, item.Id),
            item => Assert.Equal("copy-to-pane", item.Id),
            item => Assert.Equal("move-to-pane", item.Id),
            item => Assert.True(item.IsSeparator),
            item => Assert.Equal(ToolbarOverflowPlanner.ViewOptions, item.Id),
            item => Assert.Equal(ToolbarOverflowPlanner.Settings, item.Id));
        Assert.Equal(ToolbarActionCatalog.IconAndLabelDisplayMode, layout.ToolbarDisplayMode);
    }

    [Fact]
    public void FromJson_UpgradesLegacyDefaultToolbarToCurrentDefault()
    {
        var layout = CommandSurfaceLayout.FromJson("""
            {
              "version": 1,
              "toolbarDisplayMode": "labels",
              "primaryToolbar": [
                { "kind": "command", "id": "new" },
                { "kind": "command", "id": "dual-pane" },
                { "kind": "command", "id": "profiles" },
                { "kind": "command", "id": "view-options" },
                { "kind": "command", "id": "settings" }
              ]
            }
            """);

        Assert.Equal(ToolbarActionCatalog.IconAndLabelDisplayMode, layout.ToolbarDisplayMode);
        Assert.Equal(CommandSurfaceLayout.DefaultPrimaryActionIds, layout.VisiblePrimaryActionIds());
    }

    [Fact]
    public void LegacyDefaultToolbarWithTheFtpButton_StillUpgradesToTheCurrentDefault()
    {
        // The pre-plugin default toolbar, as 1.0.4 stored it, still names the removed core ftp-sftp-manager command.
        var ids = new[] { "new", "dual-pane", "copy-to-pane", "move-to-pane", "operation-history", "ftp-sftp-manager", "|", "duplicate-checker", "disk-cleanup", "|", "profiles", "view-options", "settings" };
        var items = string.Join(",\n", ids.Select(id => id == "|"
            ? """    { "kind": "separator" }"""
            : $$"""    { "kind": "command", "id": "{{id}}" }"""));
        var layout = CommandSurfaceLayout.FromJson($$"""
            {
              "version": 1,
              "toolbarDisplayMode": "icons",
              "primaryToolbar": [
            {{items}}
              ]
            }
            """);

        Assert.Equal(CommandSurfaceLayout.DefaultPrimaryActionIds, layout.VisiblePrimaryActionIds());
        Assert.DoesNotContain(layout.PrimaryToolbar, item => item.Id == "ftp-sftp-manager");
    }

    [Fact]
    public void PrimaryHideOrderForDefaultLayout_CanOverflowEveryDefaultAction()
    {
        var layout = CommandSurfaceLayout.CreateDefault();

        var hideOrder = ToolbarOverflowPlanner.PrimaryHideOrderFor(layout);

        foreach (var id in layout.VisiblePrimaryActionIds())
        {
            Assert.Contains(id, hideOrder);
        }
    }

    [Fact]
    public void FromJson_SimplifiesPreviousDefaultButPreservesCustomLayoutsAndDisplayPreference()
    {
        var previous = new CommandSurfaceLayout
        {
            ToolbarDisplayMode = ToolbarActionCatalog.IconOnlyDisplayMode,
            PrimaryToolbar = [.. new[] { "new", "dual-pane", "copy-to-pane", "move-to-pane", "operation-history", "ftp-sftp-manager", "|", "duplicate-checker", "disk-cleanup", "|", "profiles", "view-options", "settings" }
                .Select(id => id == "|" ? CommandSurfaceItem.Separator() : CommandSurfaceItem.Command(id))],
        };
        var migrated = CommandSurfaceLayout.FromJson(previous.ToJson());
        Assert.Equal(CommandSurfaceLayout.DefaultPrimaryActionIds, migrated.VisiblePrimaryActionIds());
        Assert.Equal(ToolbarActionCatalog.IconOnlyDisplayMode, migrated.ToolbarDisplayMode);
        previous.PrimaryToolbar.Reverse();
        var custom = CommandSurfaceLayout.FromJson(previous.ToJson());
        Assert.Equal(previous.VisiblePrimaryActionIds(), custom.VisiblePrimaryActionIds());
    }

    private const string PluginItem = "plugin:com.a:run";

    private static ToolbarAction PluginAction() => new(PluginItem, "Run", "Hello", "", PluginItem);

    private static string[] Order(CommandSurfaceLayout layout) =>
        [.. layout.PrimaryToolbar.Select(item => item.IsSeparator ? "|" : item.Id)];

    [Fact]
    public void EnsurePrimaryItem_FirstOffer_InsertsBeforeSettings()
    {
        var layout = CommandSurfaceLayout.CreateDefault();

        Assert.True(layout.EnsurePrimaryItem(PluginItem));
        Assert.False(layout.EnsurePrimaryItem(PluginItem));

        var ids = layout.PrimaryToolbar.Select(item => item.Id).ToList();
        Assert.Single(ids, PluginItem);
        Assert.Equal(ToolbarOverflowPlanner.Settings, ids[^1]);
        Assert.Equal(PluginItem, ids[^2]);
        Assert.Equal([PluginItem], layout.OfferedPrimaryItems);

        var noSettings = new CommandSurfaceLayout { PrimaryToolbar = [CommandSurfaceItem.Command("copy")] };
        Assert.True(noSettings.EnsurePrimaryItem("x"));
        Assert.Equal("x", noSettings.PrimaryToolbar[^1].Id);
    }

    [Fact]
    public void EnsurePrimaryItem_AfterTheUserRemovedIt_DoesNotAddItAgain()
    {
        var layout = CommandSurfaceLayout.CreateDefault();
        Assert.True(layout.EnsurePrimaryItem(PluginItem));

        layout.PrimaryToolbar.RemoveAll(item => item.Id == PluginItem);
        var before = Order(layout);

        Assert.False(layout.EnsurePrimaryItem(PluginItem));
        Assert.Equal(before, Order(layout));
        Assert.Equal([PluginItem], layout.OfferedPrimaryItems);
    }

    [Fact]
    public void OfferedPrimaryItems_SurviveNormalizeAndJsonWhileThePluginIsAbsent()
    {
        var layout = CommandSurfaceLayout.CreateDefault();
        layout.OfferedPrimaryItems = [PluginItem, "plugin:com.gone:x", PluginItem];

        layout.Normalize();
        Assert.Equal([PluginItem, "plugin:com.gone:x"], layout.OfferedPrimaryItems);

        var json = layout.ToJson();
        Assert.Contains("\"offeredPrimaryItems\"", json);
        var restored = CommandSurfaceLayout.FromJson(json);
        Assert.Null(ToolbarActionCatalog.Find(PluginItem));
        Assert.Equal([PluginItem, "plugin:com.gone:x"], restored.OfferedPrimaryItems);
    }

    [Fact]
    public void PluginToolbarItem_SurvivesASaveWhileThePluginIsDisabled()
    {
        try
        {
            // 1. The plugin is enabled and offers its item.
            ToolbarActionCatalog.SetPluginActions([PluginAction()]);
            var layout = CommandSurfaceLayout.CreateDefault();
            Assert.True(layout.EnsurePrimaryItem(PluginItem));
            var index = layout.PrimaryToolbar.FindIndex(item => item.Id == PluginItem);
            var enabledIds = layout.VisiblePrimaryActionIds().ToList();

            // 2. The plugin is disabled, and the layout is saved and loaded again.
            ToolbarActionCatalog.SetPluginActions([]);
            var saved = CommandSurfaceLayout.FromJson(layout.ToJson());
            Assert.Equal(index, saved.PrimaryToolbar.FindIndex(item => item.Id == PluginItem));
            Assert.DoesNotContain(saved.VisibleItems(), item => item.Id == PluginItem);
            Assert.DoesNotContain(PluginItem, saved.VisiblePrimaryActionIds());
            Assert.DoesNotContain(PluginItem, saved.Signature());
            var hiddenSignature = saved.Signature();

            // 3. The plugin is enabled again: the item is back where it was, and nothing is offered twice.
            ToolbarActionCatalog.SetPluginActions([PluginAction()]);
            Assert.Equal(enabledIds, saved.VisiblePrimaryActionIds());
            Assert.NotEqual(hiddenSignature, saved.Signature());
            Assert.False(saved.EnsurePrimaryItem(PluginItem));
            Assert.Single(saved.PrimaryToolbar, item => item.Id == PluginItem);
        }
        finally
        {
            ToolbarActionCatalog.SetPluginActions([]);
        }
    }

    [Fact]
    public void PluginToolbarItem_RemovedByTheUser_StaysRemovedAcrossDisableAndEnable()
    {
        try
        {
            ToolbarActionCatalog.SetPluginActions([PluginAction()]);
            var layout = CommandSurfaceLayout.CreateDefault();
            Assert.True(layout.EnsurePrimaryItem(PluginItem));
            layout.PrimaryToolbar.RemoveAll(item => item.Id == PluginItem);

            ToolbarActionCatalog.SetPluginActions([]);
            var saved = CommandSurfaceLayout.FromJson(layout.ToJson());

            ToolbarActionCatalog.SetPluginActions([PluginAction()]);
            Assert.False(saved.EnsurePrimaryItem(PluginItem));
            Assert.DoesNotContain(saved.PrimaryToolbar, item => item.Id == PluginItem);
            Assert.DoesNotContain(PluginItem, saved.VisiblePrimaryActionIds());
        }
        finally
        {
            ToolbarActionCatalog.SetPluginActions([]);
        }
    }

    private static CommandSurfaceLayout Copy(CommandSurfaceLayout layout) => CommandSurfaceLayout.FromJson(layout.ToJson());

    [Fact]
    public void MergeOffersFrom_AnOfferMadeWhileEditing_KeepsItsItem()
    {
        // Settings opens on the saved layout; the editor works on a copy of it.
        var baseline = CommandSurfaceLayout.CreateDefault();
        var live = Copy(baseline);
        var edited = Copy(baseline);
        edited.ToolbarDisplayMode = ToolbarActionCatalog.IconOnlyDisplayMode;

        // A plugin is enabled while Settings is open: the live layout gets its item and records the offer.
        Assert.True(live.EnsurePrimaryItem(PluginItem));

        // Save: the edited rows never had the item, but the user never removed it.
        edited.MergeOffersFrom(live, baseline);

        Assert.Equal(Order(live), Order(edited));
        Assert.Equal(ToolbarActionCatalog.IconOnlyDisplayMode, edited.ToolbarDisplayMode);
        Assert.Equal([PluginItem], edited.OfferedPrimaryItems);
        var saved = Copy(edited);
        Assert.False(saved.EnsurePrimaryItem(PluginItem));
        Assert.Single(saved.PrimaryToolbar, item => item.Id == PluginItem);
    }

    [Fact]
    public void MergeOffersFrom_AnOfferMadeWhileEditing_GoesBeforeTheEditedSettingsItem()
    {
        var baseline = CommandSurfaceLayout.CreateDefault();
        var live = Copy(baseline);
        var edited = Copy(baseline);
        edited.PrimaryToolbar =
        [
            CommandSurfaceItem.Command(ToolbarOverflowPlanner.Settings),
            CommandSurfaceItem.Command(ToolbarOverflowPlanner.New),
        ];
        Assert.True(live.EnsurePrimaryItem(PluginItem));

        edited.MergeOffersFrom(live, baseline);

        Assert.Equal([PluginItem, ToolbarOverflowPlanner.Settings, ToolbarOverflowPlanner.New], Order(edited));
    }

    [Fact]
    public void MergeOffersFrom_AnItemTheUserRemovedInTheEditor_StaysRemoved()
    {
        // Offered before Settings opened, then removed in the editor.
        var baseline = CommandSurfaceLayout.CreateDefault();
        Assert.True(baseline.EnsurePrimaryItem(PluginItem));
        var live = Copy(baseline);
        var edited = Copy(baseline);
        edited.PrimaryToolbar.RemoveAll(item => item.Id == PluginItem);
        var expected = Order(edited);

        edited.MergeOffersFrom(live, baseline);

        Assert.Equal(expected, Order(edited));
        Assert.Equal([PluginItem], edited.OfferedPrimaryItems);
        Assert.False(edited.EnsurePrimaryItem(PluginItem));
    }

    [Fact]
    public void MergeOffersFrom_AnItemShownInTheEditorAndRemovedThere_StaysRemovedWhenItsOfferArrivesLater()
    {
        // A toolbar saved before offers existed: the item is there but was never offered.
        var baseline = CommandSurfaceLayout.CreateDefault();
        baseline.PrimaryToolbar.Insert(0, CommandSurfaceItem.Command(PluginItem));
        var live = Copy(baseline);
        var edited = Copy(baseline);
        edited.PrimaryToolbar.RemoveAll(item => item.Id == PluginItem);
        var expected = Order(edited);

        // The offer is recorded while Settings is open; the user had already seen the item and removed it.
        Assert.True(live.EnsurePrimaryItem(PluginItem));
        edited.MergeOffersFrom(live, baseline);

        Assert.Equal(expected, Order(edited));
        Assert.Equal([PluginItem], edited.OfferedPrimaryItems);
    }

    [Fact]
    public void MergeOffersFrom_KeepsTheEditedOrderAndAddsNothingTwice()
    {
        var baseline = CommandSurfaceLayout.CreateDefault();
        var live = Copy(baseline);
        var edited = Copy(baseline);
        // The user added the item by hand in the editor, at the front.
        edited.PrimaryToolbar.Insert(0, CommandSurfaceItem.Command(PluginItem));
        var expected = Order(edited);
        Assert.True(live.EnsurePrimaryItem(PluginItem));
        Assert.True(live.EnsurePrimaryItem("plugin:com.b:hidden"));
        live.PrimaryToolbar.RemoveAll(item => item.Id == "plugin:com.b:hidden");

        edited.MergeOffersFrom(live, baseline);

        Assert.Equal(expected, Order(edited));
        Assert.Equal([PluginItem, "plugin:com.b:hidden"], edited.OfferedPrimaryItems);
    }

    [Fact]
    public void Normalize_DropsUnknownCoreIdsButKeepsWellFormedPluginIds()
    {
        // git-panel is still a core command until Task 15 removes it; ftp-sftp-manager stands in for a retired core id.
        var layout = new CommandSurfaceLayout
        {
            PrimaryToolbar =
            [
                CommandSurfaceItem.Command("settings"),
                CommandSurfaceItem.Command("missing"),
                CommandSurfaceItem.Command("ftp-sftp-manager"),
                CommandSurfaceItem.Command("plugin:"),
                CommandSurfaceItem.Command("plugin:BAD ID:x"),
                CommandSurfaceItem.Command("plugin:com.a:"),
                CommandSurfaceItem.Command("plugin:com.a:has space"),
                CommandSurfaceItem.Command(PluginItem),
            ],
        };

        layout.Normalize();

        Assert.Equal(["settings", PluginItem], Order(layout));
        Assert.True(CommandSurfaceLayout.IsPluginItemId(PluginItem));
        Assert.False(CommandSurfaceLayout.IsPluginItemId("plugin:a:b"));
        Assert.False(CommandSurfaceLayout.IsPluginItemId("plugin:com.a:" + new string('x', 129)));
        Assert.True(CommandSurfaceLayout.IsPluginItemId("plugin:com.a:" + new string('x', 128)));

        // The command grammar the manifest reader enforces on every command id.
        Assert.Equal(128, CommandSurfaceLayout.MaxPluginCommandLength);
        Assert.True(CommandSurfaceLayout.IsPluginCommand("x"));
        Assert.True(CommandSurfaceLayout.IsPluginCommand(new string('x', 128)));
        Assert.False(CommandSurfaceLayout.IsPluginCommand(""));
        Assert.False(CommandSurfaceLayout.IsPluginCommand(new string('x', 129)));
        Assert.False(CommandSurfaceLayout.IsPluginCommand("has space"));
        Assert.False(CommandSurfaceLayout.IsPluginCommand("tab\t"));
    }

    [Fact]
    public void VisibleItems_CollapseTheSeparatorsAroundHiddenItems()
    {
        var layout = new CommandSurfaceLayout
        {
            PrimaryToolbar =
            [
                CommandSurfaceItem.Command("settings"),
                CommandSurfaceItem.Separator(),
                CommandSurfaceItem.Command(PluginItem),
                CommandSurfaceItem.Separator(),
                CommandSurfaceItem.Command("copy"),
            ],
        };

        var visible = layout.VisibleItems().Select(item => item.IsSeparator ? "|" : item.Id).ToArray();

        Assert.Equal(["settings", "|", "copy"], visible);
        Assert.Equal(["settings", "|", PluginItem, "|", "copy"], Order(layout));

        var leading = new CommandSurfaceLayout
        {
            PrimaryToolbar = [CommandSurfaceItem.Command(PluginItem), CommandSurfaceItem.Separator(), CommandSurfaceItem.Command("copy")],
        };
        Assert.Equal(["copy"], leading.VisibleItems().Select(item => item.IsSeparator ? "|" : item.Id));
    }

    [Fact]
    public void FromJson_PreviousDefaultWithTheCoreFtpCommand_UpgradesToTheCurrentDefault()
    {
        // The 1.0.4 default as stored, ftp-sftp-manager included; Normalize drops that retired core id before the upgrade.
        var ids = new[] { "new", "dual-pane", "copy-to-pane", "move-to-pane", "operation-history", "ftp-sftp-manager", "|", "duplicate-checker", "disk-cleanup", "|", "profiles", "view-options", "settings" };
        var items = string.Join(",\n", ids.Select(id => id == "|"
            ? """    { "kind": "separator" }"""
            : $$"""    { "kind": "command", "id": "{{id}}" }"""));

        var layout = CommandSurfaceLayout.FromJson($$"""
            {
              "version": 1,
              "toolbarDisplayMode": "labels",
              "primaryToolbar": [
            {{items}}
              ]
            }
            """);

        Assert.Equal(CommandSurfaceLayout.DefaultPrimaryActionIds, layout.VisiblePrimaryActionIds());
        Assert.Equal(Order(CommandSurfaceLayout.CreateDefault()), Order(layout));
    }

    [Fact]
    public void ToolbarActionCatalog_SetPluginActions_AddsAndRestores()
    {
        var raised = 0;
        void Handler(object? sender, EventArgs e) => raised++;
        ToolbarActionCatalog.Changed += Handler;
        try
        {
            ToolbarActionCatalog.SetPluginActions([new ToolbarAction("plugin:com.a:run", "Run", "Hello", "\uE768", "plugin:com.a:run")]);
            Assert.Equal(1, raised);
            Assert.NotNull(ToolbarActionCatalog.Find("plugin:com.a:run"));
            Assert.Contains(ToolbarActionCatalog.All, action => action.Id == "plugin:com.a:run");

            ToolbarActionCatalog.SetPluginActions([]);
            Assert.Null(ToolbarActionCatalog.Find("plugin:com.a:run"));
        }
        finally
        {
            ToolbarActionCatalog.Changed -= Handler;
            ToolbarActionCatalog.SetPluginActions([]);
        }
    }
}
