using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SimpleFile.Core;
using SimpleFile.PluginHost;
using SimpleFile.PluginHost.Discovery;
using SimpleFile.PluginHost.Install;
using Windows.Storage.Pickers;

namespace SimpleFile.App;

/// <summary>Settings > Plugins: one card per discovered plugin, plus install, rescan and open-folder actions.</summary>
public sealed partial class SettingsWindow
{
    private PluginManager? _pluginManager;
    private readonly CancellationTokenSource _pluginsCts = new();

    // Custom settings pages (spec 8.2): one page per plugin and Settings window, reused while the record's Epoch holds.
    private readonly PluginPageCache<FrameworkElement> _pluginPages = new();
    private readonly HashSet<string> _pluginPageIds = new(StringComparer.Ordinal);
    // The slot each plugin's page shows in now, with the epoch of the render that made it.
    private readonly Dictionary<string, (int Epoch, ContentPresenter Slot)> _pluginPageSlots = new(StringComparer.Ordinal);
    private readonly HashSet<(string PluginId, int Epoch)> _pluginPagesLoading = [];
    private bool _pluginPageSearchQueued;

    /// <summary>
    /// SumaFile's one plugins folder, <c>&lt;install&gt;\plugins</c>: Install from file unzips into it, Remove deletes
    /// from it and Open plugins folder shows it (user requirement 2026-11-09: no plugin file outside the install folder).
    /// </summary>
    private static string PluginsRoot => PluginDiscovery.InstallRoot();

    /// <summary>Remembers the manager and re-renders the panel whenever it reports a change, until the window closes.</summary>
    private void AttachPlugins(PluginManager? plugins)
    {
        if (!ReferenceEquals(_pluginManager, plugins))
        {
            DetachPlugins();
            _pluginManager = plugins;
            if (plugins is not null)
            {
                plugins.Changed += OnPluginManagerChanged;
            }
        }

        RenderPluginsPanel();
    }

    private void DetachPlugins()
    {
        if (_pluginManager is { } manager)
        {
            manager.Changed -= OnPluginManagerChanged;
            _pluginManager = null;
        }
    }

    private void OnPluginManagerChanged(object? sender, EventArgs e)
    {
        // Raised on whichever thread changed the plugin.
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!IsClosed && ReferenceEquals(sender, _pluginManager))
            {
                RenderPluginsPanel();
            }
        });
    }

    private void RenderPluginsPanel()
    {
        // Every card is rebuilt: detach the pages from the old slots first, so a cached page can join its new slot.
        foreach (var (_, slot) in _pluginPageSlots.Values)
        {
            slot.Content = null;
        }

        _pluginPageSlots.Clear();
        PluginsList.Children.Clear();
        var available = _pluginManager is not null && _fileOps is not null;
        InstallPluginButton.IsEnabled = available;
        RescanPluginsButton.IsEnabled = available;
        if (_pluginManager is not { } manager || _fileOps is not { } fileOps)
        {
            PluginsList.Children.Add(new TextBlock { Text = "Plugins are not available.", TextWrapping = TextWrapping.Wrap });
            return;
        }

        var plugins = manager.Plugins;
        if (plugins.Count == 0)
        {
            PluginsList.Children.Add(new TextBlock
            {
                Text = "No plugins are installed. Install a .sfplugin file or copy a plugin folder into the plugins folder, then rescan.",
                TextWrapping = TextWrapping.Wrap,
            });
            return;
        }

        var token = _pluginsCts.Token;
        var builder = new PluginSettingsPanelBuilder
        {
            XamlRoot = () => Content?.XamlRoot,
            CanRemove = IsRemovable,
            SetEnabledAsync = (record, enabled) => enabled
                ? manager.EnableAsync(record.Id, token)
                : manager.DisableAsync(record.Id, token),
            ReviewAsync = record => ReviewPluginAsync(manager, record, token),
            RemoveAsync = record => RemovePluginAsync(manager, record, token),
            PickFolderAsync = PickPluginFolderAsync,
            AttachSettingsPage = (record, slot) => AttachSettingsPage(manager, record, slot, token),
        };
        ForgetSettingsPagesOfPluginsNotEnabled(plugins);
        foreach (var record in plugins)
        {
            PluginsList.Children.Add(builder.Build(
                record,
                key => fileOps.GetSettingAsync(key, token),
                // Through the manager: it checks the key is the plugin's own and re-decorates the shown folders.
                (storageKey, value) => manager.WriteSettingAsync(record.Id, storageKey, value, token)));
        }
    }

    /// <summary>A page is kept only for an Enabled plugin: any other state retired the runtime that made it.</summary>
    private void ForgetSettingsPagesOfPluginsNotEnabled(IReadOnlyList<PluginRecord> plugins)
    {
        var enabled = plugins.Where(record => record.State == PluginState.Enabled).Select(record => record.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var id in _pluginPageIds.Where(id => !enabled.Contains(id)).ToList())
        {
            _pluginPages.Remove(id);
            _pluginPageIds.Remove(id);
        }
    }

    /// <summary>
    /// Puts the plugin's settings page in <paramref name="slot"/>: the cached page while the record's Epoch is unchanged,
    /// otherwise a new one from <see cref="PluginManager.CreateSettingsPageAsync"/> (asked at most once at a time).
    /// </summary>
    private void AttachSettingsPage(PluginManager manager, PluginRecord record, ContentPresenter slot, CancellationToken token)
    {
        _pluginPageSlots[record.Id] = (record.Epoch, slot);
        if (_pluginPages.TryGet(record.Id, record.Epoch, out var page) && page is not null)
        {
            InsertSettingsPage(slot, page);
            return;
        }

        if (_pluginPagesLoading.Add((record.Id, record.Epoch)))
        {
            _ = LoadSettingsPageAsync(manager, record.Id, record.Epoch, token);
        }
    }

    private async Task LoadSettingsPageAsync(PluginManager manager, string pluginId, int epoch, CancellationToken token)
    {
        try
        {
            var created = await manager.CreateSettingsPageAsync(pluginId, token);
            if (IsClosed || !ReferenceEquals(_pluginManager, manager))
            {
                return;
            }

            var page = created as FrameworkElement
                ?? throw new InvalidOperationException($"returned {created.GetType().FullName}, not a Microsoft.UI.Xaml.FrameworkElement");
            _pluginPages.Store(pluginId, epoch, page);
            _pluginPageIds.Add(pluginId);
            if (_pluginPageSlots.TryGetValue(pluginId, out var current) && current.Epoch == epoch)
            {
                InsertSettingsPage(current.Slot, page);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            // A provider failure also marks the plugin LoadFailed, whose re-render drops the slot; until then, say why.
            if (!IsClosed && _pluginPageSlots.TryGetValue(pluginId, out var current) && current.Epoch == epoch)
            {
                current.Slot.Content = PluginSettingsPanelBuilder.Muted($"This settings page could not be loaded: {exception.Message}");
            }
        }
        finally
        {
            _pluginPagesLoading.Remove((pluginId, epoch));
        }
    }

    private void InsertSettingsPage(ContentPresenter slot, FrameworkElement page)
    {
        // A cached page may still sit in an earlier slot of this window: detach it before it is re-added.
        foreach (var (_, other) in _pluginPageSlots.Values)
        {
            if (!ReferenceEquals(other, slot) && ReferenceEquals(other.Content, page))
            {
                other.Content = null;
            }
        }

        if (!ReferenceEquals(slot.Content, page))
        {
            slot.Content = page;
        }

        // Search indexes the page once it exists: run the current query again so its controls can match. Queued, since
        // a cached page is inserted while its card is still being built, before the card joins the list.
        if (!_pluginPageSearchQueued)
        {
            _pluginPageSearchQueued = DispatcherQueue.TryEnqueue(() =>
            {
                _pluginPageSearchQueued = false;
                if (!IsClosed)
                {
                    RerunSettingsSearch();
                }
            });
        }
    }

    /// <summary>A plugin the user installed into the plugins folder; bundled plugins are disabled, never removed.</summary>
    private static bool IsRemovable(PluginRecord record) => PluginPackageInstaller.IsRemovable(record, PluginsRoot);

    private async Task ReviewPluginAsync(PluginManager manager, PluginRecord record, CancellationToken token)
    {
        if (Content?.XamlRoot is not { } root)
        {
            return;
        }

        var dialog = new PluginConsentDialog(record) { XamlRoot = root };
        if (await DialogHost.ShowAsync(dialog, token) == ContentDialogResult.Primary)
        {
            await manager.EnableAsync(record.Id, token);
        }
    }

    private static async Task RemovePluginAsync(PluginManager manager, PluginRecord record, CancellationToken token)
    {
        if (!IsRemovable(record))
        {
            throw new InvalidOperationException(record.IsBundledId
                ? $"{record.Discovered.Manifest?.Name ?? record.Id} ships with SumaFile and cannot be removed. Disable it instead."
                : "Only plugins in SumaFile's plugins folder can be removed.");
        }

        if (record.State is PluginState.Enabled or PluginState.Faulted or PluginState.LoadFailed)
        {
            // Stop its backend and detach it before its files go away.
            await manager.DisableAsync(record.Id, token);
        }

        // Only the folder and its stored approval go: the plugin's settings and its plugins-data folder are kept.
        await manager.RemovePackageAsync(record.Discovered.Folder, PluginsRoot, token);
    }

    private async Task<string?> PickPluginFolderAsync()
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        if (OwnerHwnd != 0)
        {
            WinRT.Interop.InitializeWithWindow.Initialize(picker, OwnerHwnd);
        }

        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }

    private async void OnInstallPluginClicked(object sender, RoutedEventArgs e)
    {
        if (_pluginManager is not { } manager)
        {
            return;
        }

        InstallPluginButton.IsEnabled = false;
        try
        {
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(".sfplugin");
            if (OwnerHwnd != 0)
            {
                WinRT.Interop.InitializeWithWindow.Initialize(picker, OwnerHwnd);
            }

            var file = await picker.PickSingleFileAsync();
            if (file is null)
            {
                return;
            }

            var token = _pluginsCts.Token;
            // Clears the plugin's stored approval and rescans, so the installed code always asks for consent.
            var folder = await manager.InstallPackageAsync(file.Path, PluginsRoot, token);
            var id = Path.GetFileName(folder);
            var installed = manager.Plugins.FirstOrDefault(record => record.Id == id);
            ShowPluginsStatus(installed?.State == PluginState.NeedsConsent
                ? $"Installed {installed.Discovered.Manifest?.Name ?? id}. Choose Review… to enable it."
                : $"Installed {installed?.Discovered.Manifest?.Name ?? id}.");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            ShowPluginsStatus(exception.Message);
        }
        finally
        {
            InstallPluginButton.IsEnabled = _pluginManager is not null;
        }
    }

    private async void OnRescanPluginsClicked(object sender, RoutedEventArgs e)
    {
        if (_pluginManager is not { } manager)
        {
            return;
        }

        RescanPluginsButton.IsEnabled = false;
        try
        {
            await manager.RescanAsync(_pluginsCts.Token);
            ShowPluginsStatus($"Found {manager.Plugins.Count} plugin(s).");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            ShowPluginsStatus(exception.Message);
        }
        finally
        {
            RescanPluginsButton.IsEnabled = _pluginManager is not null;
        }
    }

    private void OnOpenPluginsFolderClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            var pluginsRoot = PluginsRoot;
            Directory.CreateDirectory(pluginsRoot);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{pluginsRoot}\"") { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            ShowPluginsStatus(exception.Message);
        }
    }

    private void ShowPluginsStatus(string message)
    {
        PluginsStatusText.Text = message;
        PluginsStatusText.Visibility = Visibility.Visible;
    }

#if DEBUG
    // For the UI smoke (MainWindow.UiSmoke).
    internal Panel SmokePluginsPanel => PluginsPanel;

    internal Panel SmokePluginsList => PluginsList;

    internal FrameworkElement? SmokeFirstSearchMatch => _firstSearchMatch;

    internal void SmokeSearch(string query) => SearchBox.Text = query;
#endif
}
