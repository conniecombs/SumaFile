using SimpleFile.PluginSdk;

namespace SumaFile.Git.Model;

/// <summary>
/// The plugin's <see cref="IRowDecorationProvider"/> logic (provider <c>status</c>). It runs on a thread-pool thread
/// and touches no WinUI object. Each request reads the <c>enabled</c> setting first:
/// <list type="bullet">
/// <item>Off, or the Recycle Bin: no decorations and no folder flag, and no backend call, so no git runs.</item>
/// <item>The first request that reads off after one that read on also hides a shown workbench. Turning Git off in
/// Settings writes through <c>WriteSettingAsync</c>, which asks this provider again at once, so the one write clears
/// the badges, the <c>inRepository</c> flag (and with it the Git menu group and the toolbar toggle) and the open
/// workbench: 1.0.4's complete disable. A workbench the user opens on purpose while Git is off is not hidden
/// again.</item>
/// <item>On: one <c>repo.decorations</c> call. Each changed child gets its status label, the label's brush and
/// "Git: &lt;label&gt;"; a folder inside a repository gets <c>inRepository</c>.</item>
/// </list>
/// A backend error (Git missing, the backend stopped) gives no decorations and is logged once per distinct message.
/// </summary>
public sealed class GitDecorationProvider(IGitBackend backend, IPluginSettings settings, IPluginPanels panels, IPluginLogger log)
{
    /// <summary>The folder flag the manifest's <c>when</c> expressions read with <c>folderFlag(inRepository)</c>.</summary>
    public const string InRepositoryFlag = "inRepository";

    private const int Unknown = 0;
    private const int Enabled = 1;
    private const int Disabled = 2;
    private const int MaxLoggedErrors = 64;

    private readonly IGitBackend _backend = backend ?? throw new ArgumentNullException(nameof(backend));
    private readonly IPluginSettings _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    private readonly IPluginPanels _panels = panels ?? throw new ArgumentNullException(nameof(panels));
    private readonly IPluginLogger _log = log ?? throw new ArgumentNullException(nameof(log));
    private readonly HashSet<string> _loggedErrors = new(StringComparer.Ordinal);
    private int _lastEnabled = Unknown;

    public async Task<PluginFolderDecorations> GetAsync(PluginDecorationRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            var enabled = await GitSettings.IsEnabledAsync(_settings, ct).ConfigureAwait(false);
            var previous = Interlocked.Exchange(ref _lastEnabled, enabled ? Enabled : Disabled);
            if (!enabled)
            {
                if (previous == Enabled && _panels.IsVisible(GitWorkbenchController.WorkbenchPanelId))
                {
                    // Queued to the UI thread by the host, never run inline.
                    _panels.Hide(GitWorkbenchController.WorkbenchPanelId);
                }

                return PluginFolderDecorations.Empty;
            }

            if (string.IsNullOrWhiteSpace(request.FolderPath) || GitPaths.IsRecycleBinPath(request.FolderPath))
            {
                return PluginFolderDecorations.Empty;
            }

            var result = await _backend.DecorationsAsync(request.FolderPath, ct).ConfigureAwait(false);
            if (result is null)
            {
                return PluginFolderDecorations.Empty;
            }

            var rows = new Dictionary<string, RowDecoration>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in result.Rows ?? [])
            {
                if (string.IsNullOrWhiteSpace(row?.Path) || string.IsNullOrWhiteSpace(row.Status))
                {
                    continue;
                }

                rows[row.Path] = new RowDecoration(row.Status, GitText.DecorationBrushKey(row.Status), "Git: " + row.Status);
            }

            IReadOnlyList<string> flags = result.InRepository ? [InRepositoryFlag] : [];
            return rows.Count == 0 && flags.Count == 0 ? PluginFolderDecorations.Empty : new PluginFolderDecorations(rows, flags);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The user navigated on; the host drops this request's result anyway.
            return PluginFolderDecorations.Empty;
        }
        catch (Exception exception)
        {
            LogOnce(exception.Message);
            return PluginFolderDecorations.Empty;
        }
    }

    private void LogOnce(string message)
    {
        lock (_loggedErrors)
        {
            if (_loggedErrors.Count >= MaxLoggedErrors)
            {
                _loggedErrors.Clear();
            }

            if (!_loggedErrors.Add(message))
            {
                return;
            }
        }

        _log.Warn("Git decorations failed: " + message);
    }
}
