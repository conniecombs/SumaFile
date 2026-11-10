using System.Text.Json;
using SimpleFile.PluginHost.Manifest;
using SumaFile.Git.Model;
using Xunit;

namespace SimpleFile.Tests.Git;

public class GitPluginManifestTests
{
    [Fact]
    public void GitManifest_InTheRepo_IsValidAndDeclaresExactlyThePlan()
    {
        var r = PluginManifestReader.Read(File.ReadAllText(ManifestPath()), "com.sumafile.git");

        Assert.True(r.IsValid, string.Join("; ", r.Errors));
        Assert.Empty(r.Errors);
        var m = r.Manifest!;
        Assert.Equal("com.sumafile.git", m.Id);
        Assert.Equal("Git", m.Name);
        Assert.Equal("1.0.0", m.Version);
        Assert.Equal("1.0.0", m.SdkVersion);
        Assert.Equal(new PluginPublisher("SumaFile Team", "https://github.com/conniecombs/SumaFile", null), m.Publisher);
        Assert.Equal(new PluginUiSpec("SumaFile.Git.dll", "SumaFile.Git.GitPlugin"), m.Ui);
        Assert.NotNull(m.Backend);
        Assert.Equal("sumafile-git-backend.exe", m.Backend!.Executable);
        Assert.Empty(m.Backend.Args);
        Assert.Equal("startup", m.Backend.ActivateOn);
        Assert.False(m.Backend.AcceptsMultipleClients);
        Assert.Equal(["backend-process", "shell", "network"], m.Permissions);

        var commands = m.Contributions.Commands;
        Assert.Equal(
            [
                ("git-panel", "Git: show workbench"),
                ("git-refresh", "Git: refresh status"),
                ("git-fetch", "Git: fetch"),
                ("git-pull", "Git: pull"),
                ("git-push", "Git: push"),
                ("git-commit", "Git: commit staged changes"),
                ("git-stage-selected", "Git: stage selected paths"),
                ("git-unstage-selected", "Git: unstage selected paths"),
                ("git-discard-selected", "Git: discard selected changes"),
                ("git-diff-selected", "Git: show selected diff"),
            ],
            commands.Select(c => (c.Id, c.Label)).ToArray());
        Assert.All(commands, c => Assert.Equal("Git", c.Group));
        Assert.All(commands, c => Assert.Null(c.Shortcut));

        // The host ids GitCommandIds names are exactly the manifest's commands.
        var hostIds = typeof(GitCommandIds).GetFields()
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(commands.Select(c => "plugin:com.sumafile.git:" + c.Id).Order(StringComparer.Ordinal), hostIds);
        Assert.Equal(hostIds.Where(id => id != GitCommandIds.Panel), GitCommandIds.Handled.Order(StringComparer.Ordinal));

        var group = Assert.Single(m.Contributions.ContextMenu);
        Assert.Equal("Git", group.Group);
        const string inRepository = "folderFlag(inRepository)";
        Assert.Equal(
            [
                new PluginContextMenuItemSpec("git-panel", "Show Git workbench", inRepository),
                new PluginContextMenuItemSpec("git-refresh", "Refresh status", inRepository),
                new PluginContextMenuItemSpec("git-diff-selected", "Show diff", "selectionCount == 1 && hasDecoration"),
                new PluginContextMenuItemSpec("git-stage-selected", "Stage selected", "selectionCount >= 1 && hasDecoration"),
                new PluginContextMenuItemSpec("git-unstage-selected", "Unstage selected", "selectionCount >= 1 && hasDecoration"),
                new PluginContextMenuItemSpec("git-discard-selected", "Discard selected changes", "selectionCount >= 1 && hasDecoration"),
                new PluginContextMenuItemSpec("git-fetch", "Fetch", inRepository),
                new PluginContextMenuItemSpec("git-pull", "Pull", inRepository),
                new PluginContextMenuItemSpec("git-push", "Push", inRepository),
                new PluginContextMenuItemSpec("git-commit", "Commit", inRepository),
            ],
            group.Items);

        var toolbar = Assert.Single(m.Contributions.Toolbar);
        Assert.Equal(new PluginToolbarSpec("git-panel", "Git", "", "primary") { When = inRepository }, toolbar);

        var panel = Assert.Single(m.Contributions.Panels);
        Assert.Equal(new PluginPanelSpec("workbench", "Git workbench", "workbench", "bottom", true, "git-panel"), panel);
        Assert.Equal(GitWorkbenchController.WorkbenchPanelId, panel.Id);
        Assert.Equal(new PluginRowDecorationSpec("status"), m.Contributions.RowDecorations);
        Assert.Empty(m.Contributions.Sidebar);
        Assert.Null(m.Contributions.SettingsPage);
        Assert.Empty(m.Contributions.ArchiveFormats);

        var setting = Assert.Single(m.Settings);
        Assert.Equal(GitSettings.EnabledKey, setting.Key);
        Assert.Equal("boolean", setting.Type);
        Assert.Equal("Show Git status, menus and the Git workbench", setting.Label);
        Assert.Equal("Off: SumaFile runs no Git commands.", setting.Description);
        Assert.Equal("General", setting.Group);
        Assert.Equal(JsonValueKind.True, setting.Default!.Value.ValueKind);
        Assert.Equal("enableGitIntegration", setting.MigrateFrom);
        Assert.Null(setting.Options);
    }

    private static string ManifestPath()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "plugins", "git", "plugin.json");
            if (File.Exists(Path.Combine(directory.FullName, "Cargo.toml")) && File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("plugins/git/plugin.json not found above " + AppContext.BaseDirectory);
    }
}
