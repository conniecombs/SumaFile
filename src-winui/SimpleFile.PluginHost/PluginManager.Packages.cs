using SimpleFile.PluginHost.Install;

namespace SimpleFile.PluginHost;

public sealed partial class PluginManager
{
    /// <summary>
    /// Installs a <c>.sfplugin</c> into <paramref name="pluginsRoot"/> (<see cref="PluginPackageInstaller.Install(string, string, Action{string}?)"/>),
    /// clearing the plugin's stored approval once the package is staged and before its files go into place, then
    /// rescans. So the installed plugin asks for consent again even when its <c>plugin.json</c> is byte for byte the one
    /// the user approved: the approval is keyed on that file's hash, but the code beside it may differ. Returns the
    /// installed folder. An approvals file that cannot be written fails the install and leaves the previous version.
    /// </summary>
    public async Task<string> InstallPackageAsync(string sfpluginPath, string pluginsRoot, CancellationToken ct)
    {
        string folder;
        await _ops.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            folder = await Task.Run(() => PluginPackageInstaller.Install(sfpluginPath, pluginsRoot, ForgetApproval), ct)
                .ConfigureAwait(false);
        }
        finally
        {
            _ops.Release();
        }

        await RescanAsync(ct).ConfigureAwait(false);
        return folder;
    }

    /// <summary>
    /// Removes a plugin folder the user installed (<see cref="PluginPackageInstaller.Remove(string, string, Action{string}?)"/>),
    /// clearing its stored approval first, then rescans; a later install of the same id asks for consent again. The
    /// caller disables a running plugin first, as Settings does.
    /// </summary>
    public async Task RemovePackageAsync(string pluginFolder, string pluginsRoot, CancellationToken ct)
    {
        await _ops.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await Task.Run(() => PluginPackageInstaller.Remove(pluginFolder, pluginsRoot, ForgetApproval), ct)
                .ConfigureAwait(false);
        }
        finally
        {
            _ops.Release();
        }

        await RescanAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Removes the stored approval of <paramref name="pluginId"/>; throws when the approvals file cannot be read.</summary>
    private void ForgetApproval(string pluginId) => _approvals.Remove(pluginId);
}
