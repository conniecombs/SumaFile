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

public class OpenWithPreferencesTests
{
    [Fact]
    public void OpenWithPreferences_ComposesPinnedRecentAndDiscoveredAppsInOrder()
    {
        var preferences = new OpenWithPreferences();
        var pinned = OpenWithApplication.FromPath(@"C:\Program Files\Pinned\App.exe", "Pinned App", "custom");
        var recent = OpenWithApplication.FromPath(@"C:\Program Files\Recent\App.exe", "Recent App", "custom");
        var discovered = OpenWithApplication.FromPath(@"C:\Program Files\Suggested\App.exe", "Suggested App", "suggested");

        preferences.RecordRecent("TXT", recent);
        preferences.PinForExtension("txt", pinned);
        var apps = preferences.ComposeMenuApplications(".txt", [recent, discovered]);

        Assert.Collection(
            apps,
            app =>
            {
                Assert.Equal("Pinned App", app.DisplayName);
                Assert.True(app.IsFavorite);
                Assert.False(app.IsRecent);
            },
            app =>
            {
                Assert.Equal("Recent App", app.DisplayName);
                Assert.False(app.IsFavorite);
                Assert.True(app.IsRecent);
            },
            app =>
            {
                Assert.Equal("Suggested App", app.DisplayName);
                Assert.Equal("suggested", app.Source);
            });
    }
    [Fact]
    public void OpenWithPreferences_CapsRecentsAndRoundTripsJson()
    {
        var preferences = new OpenWithPreferences();
        for (var index = 0; index < OpenWithPreferences.MaxRecentApplications + 2; index++)
        {
            preferences.RecordRecent(
                ".log",
                OpenWithApplication.FromPath($@"C:\Program Files\App{index}\app.exe", $"App {index}", "custom"));
        }

        var roundTripped = OpenWithPreferences.FromJson(preferences.ToJson());
        var apps = roundTripped.ComposeMenuApplications("log", []);

        Assert.Equal(OpenWithPreferences.MaxRecentApplications, apps.Count);
        Assert.Equal("App 9", apps[0].DisplayName);
        Assert.Equal("App 2", apps[^1].DisplayName);
        Assert.All(apps, app => Assert.True(app.IsRecent));
    }
    [Fact]
    public void OpenWithPreferences_UnpinsFavoritesForExtension()
    {
        var preferences = new OpenWithPreferences();
        var favorite = OpenWithApplication.FromPath(@"C:\Program Files\Pinned\App.exe", "Pinned App", "custom");

        preferences.PinForExtension(".md", favorite);
        Assert.Contains(preferences.ComposeMenuApplications("md", []), app => app.IsFavorite);

        preferences.UnpinForExtension("md", favorite);

        Assert.DoesNotContain(preferences.ComposeMenuApplications("md", []), app => app.IsFavorite);
        Assert.False(preferences.FavoritesByExtension.ContainsKey(".md"));
    }
}
