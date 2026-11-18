#if DEBUG
using System.Diagnostics;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using SimpleFile.Core;

namespace SimpleFile.App;

public sealed partial class MainWindow
{
    /// <summary>
    /// Thumbnails of a camera-like photo folder (see New-SmokePhotoFixture in scripts/smoke-winui-ui.ps1): how long
    /// the viewport takes to fill after navigating, then every remaining thumbnail cold (decoded by the service and
    /// written to its disk cache) and warm (served from that cache, the in-app cache dropped). Each thumbnail must
    /// exist, and a portrait photo, stored on its side with an orientation tag, must come out portrait.
    /// </summary>
    private async Task SmokeThumbnailsAsync(ExplorerWorkspace workspace, string root, string photos, List<object> results)
    {
        var cacheFolder = Path.Combine(root, "state", "thumbnail-cache");
        Directory.CreateDirectory(cacheFolder);
        var previousCache = workspace.Settings.ThumbnailCachePath;
        var previousIconSize = workspace.IconSizeFor(PaneId.Primary);
        var previousView = workspace.Primary.View;
        workspace.Settings.ThumbnailCachePath = cacheFolder;
        await workspace.SaveUiSettingsAsync();
        try
        {
            workspace.SetFileListView(PaneId.Primary, "tiles");
            workspace.SetFileListIconSize(PaneId.Primary, 96);
            ApplyFileListViewPresentation();
            var expected = Directory.GetFiles(photos, "*.jpg").Length;
            SmokeAssert(expected > 0, "The photo fixture is empty");

            var timer = Stopwatch.StartNew();
            await workspace.NavigatePaneAsync(PaneId.Primary, photos);
            await SmokeWaitAsync(() => PrimaryFiles.Count == expected && !workspace.Primary.ListingInProgress, "photo listing");
            ApplyFileListThumbnailPolicy();
            var iconSize = workspace.IconSizeFor(PaneId.Primary);
            SmokeAssert(FileListThumbnailHost.ShouldUseThumbnails(PrimaryFiles[0], iconSize), "Thumbnails are off for the photo folder");

            List<FileRow> Visible() => PrimaryFiles
                .Where(row => PrimaryFileList.ContainerFromItem(row) is ListViewItem { ActualHeight: > 0 })
                .ToList();
            await SmokeWaitAsync(() => Visible().Count > 0, "visible photo rows");
            await SmokeWaitAsync(() => Visible().All(row => FileListThumbnailHost.CachedThumbnail(row, iconSize) is not null), "viewport thumbnails");
            var viewportMs = timer.Elapsed.TotalMilliseconds;
            var viewportRows = Visible().Count;

            // Cold: every thumbnail the viewport did not ask for, one at a time through the same host the rows use.
            var remaining = PrimaryFiles.Where(row => FileListThumbnailHost.CachedThumbnail(row, iconSize) is null).ToList();
            timer.Restart();
            foreach (var row in remaining)
            {
                SmokeAssert(await FileListThumbnailHost.LoadThumbnailAsync(row, iconSize, CancellationToken.None) is not null, $"No thumbnail for {row.Name}");
            }
            var coldMs = timer.Elapsed.TotalMilliseconds;

            foreach (var row in PrimaryFiles)
            {
                SmokeAssert(FileListThumbnailHost.CachedThumbnail(row, iconSize) is BitmapImage, $"{row.Name} has no decoded thumbnail");
                var bitmap = (BitmapImage)FileListThumbnailHost.CachedThumbnail(row, iconSize)!;
                var portrait = row.Name.Contains("-portrait", StringComparison.Ordinal);
                SmokeAssert(portrait ? bitmap.PixelHeight > bitmap.PixelWidth : bitmap.PixelWidth > bitmap.PixelHeight,
                    $"{row.Name} thumbnail is {bitmap.PixelWidth}x{bitmap.PixelHeight}");
            }

            // Warm: the in-app cache dropped, the service answers from its disk cache.
            FileListThumbnailHost.InvalidatePaths(PrimaryFiles.Select(row => row.Path));
            timer.Restart();
            foreach (var row in PrimaryFiles)
            {
                SmokeAssert(await FileListThumbnailHost.LoadThumbnailAsync(row, iconSize, CancellationToken.None) is not null, $"No cached thumbnail for {row.Name}");
            }
            var warmMs = timer.Elapsed.TotalMilliseconds;

            results.Add(new
            {
                scenario = "thumbnails", photos = expected, viewportRows, viewportMs,
                coldRows = remaining.Count, coldMs, coldPerThumbnailMs = remaining.Count > 0 ? coldMs / remaining.Count : 0,
                warmMs, warmPerThumbnailMs = warmMs / expected, passed = true,
            });
        }
        finally
        {
            workspace.SetFileListView(PaneId.Primary, previousView);
            workspace.SetFileListIconSize(PaneId.Primary, previousIconSize);
            workspace.Settings.ThumbnailCachePath = previousCache;
            await workspace.SaveUiSettingsAsync();
            ApplyFileListViewPresentation();
        }
    }
}
#endif
