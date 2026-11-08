using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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

    private static string UserPluginRoot => PluginDiscovery.UserRoot();

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
            CanRemove = IsUserInstalled,
            SetEnabledAsync = (record, enabled) => enabled
                ? manager.EnableAsync(record.Id, token)
                : manager.DisableAsync(record.Id, token),
            ReviewAsync = record => ReviewPluginAsync(manager, record, token),
            RemoveAsync = record => RemovePluginAsync(manager, record, token),
            PickFolderAsync = PickPluginFolderAsync,
        };
        foreach (var record in plugins)
        {
            PluginsList.Children.Add(builder.Build(
                record,
                key => fileOps.GetSettingAsync(key, token),
                (key, value) => fileOps.SetSettingAsync(key, value, token)));
        }
    }

    private static bool IsUserInstalled(PluginRecord record) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(record.Discovered.Root)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(UserPluginRoot)),
            StringComparison.OrdinalIgnoreCase);

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
        if (!IsUserInstalled(record))
        {
            throw new InvalidOperationException("Only plugins installed in your user plugins folder can be removed.");
        }

        if (record.State is PluginState.Enabled or PluginState.Faulted or PluginState.LoadFailed)
        {
            // Stop its backend and detach it before its files go away.
            await manager.DisableAsync(record.Id, token);
        }

        var folder = record.Discovered.Folder;
        await Task.Run(() => PluginPackageInstaller.Remove(folder, UserPluginRoot), token);
        await manager.RescanAsync(token);
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
            var userRoot = UserPluginRoot;
            var folder = await Task.Run(() => PluginPackageInstaller.Install(file.Path, userRoot), token);
            await manager.RescanAsync(token);
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
            var userRoot = UserPluginRoot;
            Directory.CreateDirectory(userRoot);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{userRoot}\"") { UseShellExecute = true });
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
}
