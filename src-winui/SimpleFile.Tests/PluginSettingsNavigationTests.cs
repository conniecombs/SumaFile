using System;
using System.IO;
using Xunit;

namespace SimpleFile.Tests;

/// <summary>
/// Settings > Plugins gives each plugin its own page under the Plugins navigation item (which shows an overview),
/// rather than stacking every plugin's card in one panel. The App alone compiles this code, so its shape is pinned
/// here; the UI smoke drives it for real.
/// </summary>
public sealed class PluginSettingsNavigationTests
{
    private static string AppFile(string name)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Cargo.toml")) && Directory.Exists(Path.Combine(dir.FullName, "src-winui")))
            {
                return File.ReadAllText(Path.Combine(dir.FullName, "src-winui", "SimpleFile.App", name)).ReplaceLineEndings("\n");
            }
        }

        throw new InvalidOperationException("No folder above " + AppContext.BaseDirectory + " holds both Cargo.toml and src-winui.");
    }

    private static string Body(string source, string declaration)
    {
        var start = source.IndexOf(declaration, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{declaration}' was not found.");
        var end = source.IndexOf("\n    }\n", start, StringComparison.Ordinal);
        Assert.True(end > start, $"The body of '{declaration}' has no end.");
        return source[start..end];
    }

    [Fact]
    public void SettingsXaml_HasThePluginsItemTheOverviewAndThePageList()
    {
        var xaml = AppFile("SettingsWindow.xaml");

        Assert.Contains("<NavigationViewItem x:Name=\"PluginsNavItem\" Content=\"Plugins\" Tag=\"Plugins\" />", xaml, StringComparison.Ordinal);
        var overview = xaml.IndexOf("x:Name=\"PluginsOverview\"", StringComparison.Ordinal);
        var overviewList = xaml.IndexOf("x:Name=\"PluginsOverviewList\"", StringComparison.Ordinal);
        var pages = xaml.IndexOf("x:Name=\"PluginsList\"", StringComparison.Ordinal);
        Assert.True(overview >= 0 && overviewList > overview && pages > overviewList,
            "PluginsPanel does not hold the overview (with its list) followed by the page list.");
        // The actions belong to the overview, not to a plugin's page.
        var install = xaml.IndexOf("x:Name=\"InstallPluginButton\"", StringComparison.Ordinal);
        Assert.True(install > overview && install < pages, "Install from file is not inside the overview.");
    }

    [Fact]
    public void Render_BuildsAnOverviewRowAndATaggedPagePerPlugin_AndOneNavigationItemEach()
    {
        var plugins = AppFile("SettingsWindow.Plugins.cs");
        var render = Body(plugins, "private void RenderPluginsPanel()");

        Assert.Contains("PluginsOverviewList.Children.Add(builder.BuildOverviewRow(record, () => SelectPluginPage(id)));", render, StringComparison.Ordinal);
        Assert.Contains("page.Tag = id;", render, StringComparison.Ordinal);
        Assert.Contains("PluginsList.Children.Add(page);", render, StringComparison.Ordinal);
        Assert.Contains("UpdatePluginNavigation(plugins);", render, StringComparison.Ordinal);

        var navigation = Body(plugins, "private void UpdatePluginNavigation(IReadOnlyList<PluginRecord> plugins)");
        Assert.Contains("Tag = PluginSettingsPanelBuilder.NavigationTag(id)", navigation, StringComparison.Ordinal);
        Assert.Contains("PluginsNavItem.MenuItems.Add(item);", navigation, StringComparison.Ordinal);
        // The rebuild must not let SelectionChanged blank the page.
        Assert.Contains("_updatingPluginNavigation = true;", navigation, StringComparison.Ordinal);
        Assert.Contains("if (_updatingPluginNavigation)", Body(AppFile("SettingsWindow.xaml.cs"), "private void OnCategorySelectionChanged("), StringComparison.Ordinal);
    }

    [Fact]
    public void ShowPluginPage_ShowsOnePageOrTheOverview()
    {
        var show = Body(AppFile("SettingsWindow.Plugins.cs"), "private void ShowPluginPage(string? pluginId)");

        Assert.Contains("PluginsOverview.Visibility = pluginId is null ? Visibility.Visible : Visibility.Collapsed;", show, StringComparison.Ordinal);
        Assert.Contains("page.Visibility = string.Equals(page.Tag as string, pluginId, StringComparison.Ordinal) ? Visibility.Visible : Visibility.Collapsed;", show, StringComparison.Ordinal);

        var category = Body(AppFile("SettingsWindow.xaml.cs"), "private void ShowCategory(string category)");
        Assert.Contains("ShowPluginPage(null);", category, StringComparison.Ordinal);
        Assert.Contains("ShowPluginPage(category[PluginSettingsPanelBuilder.PluginCategoryPrefix.Length..]);", category, StringComparison.Ordinal);
    }

    [Fact]
    public void ACustomSettingsPage_IsCreatedOnlyWhenItsPluginsPageIsShown()
    {
        var plugins = AppFile("SettingsWindow.Plugins.cs");

        // The builder's slot goes to RequestSettingsPage, which attaches at once only for the shown plugin or a page
        // this window already holds; any other slot waits in _pluginPageRequests.
        Assert.Contains("AttachSettingsPage = (record, slot) => RequestSettingsPage(manager, record, slot, token),", plugins, StringComparison.Ordinal);
        var request = Body(plugins, "private void RequestSettingsPage(");
        Assert.Contains("string.Equals(_selectedPluginId, record.Id, StringComparison.Ordinal)", request, StringComparison.Ordinal);
        Assert.Contains("_pluginPages.TryGet(record.Id, record.Epoch, out var cached)", request, StringComparison.Ordinal);
        Assert.Contains("_pluginPageRequests[record.Id] = (record, slot);", request, StringComparison.Ordinal);

        var show = Body(plugins, "private void ShowPluginPage(string? pluginId)");
        Assert.Contains("_pluginPageRequests.Remove(pluginId, out var request)", show, StringComparison.Ordinal);
        Assert.Contains("AttachSettingsPage(manager, request.Record, request.Slot, _pluginsCts.Token);", show, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsSearch_SearchesEveryPluginsPage_AndShowsThePageOfTheMatch()
    {
        var search = Body(AppFile("SettingsWindow.xaml.cs"), "private void ApplySettingsSearch()");

        Assert.Contains("(hit, matchedPluginId) = FindPluginsSearchHit(Matches);", search, StringComparison.Ordinal);
        Assert.Contains("PluginNavigationItem(matchedPluginId) is { } pluginItem", search, StringComparison.Ordinal);
        Assert.Contains("target = pluginItem;", search, StringComparison.Ordinal);
    }
}
