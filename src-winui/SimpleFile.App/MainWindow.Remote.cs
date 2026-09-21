using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SimpleFile.Core;

namespace SimpleFile.App;

public sealed partial class MainWindow
{
    private void ShowRemoteManagerWindow()
    {
        if (_workspace?.FileOps is null)
        {
            ShowMessage("FTP/SFTP manager", "The backend service is not ready yet.", InfoBarSeverity.Warning);
            return;
        }

        if (_remoteManagerWindow is { IsClosed: false } existing)
        {
            existing.Activate();
            return;
        }

        var viewModel = new RemoteManagerViewModel(_workspace.FileOps);
        _remoteManagerWindow = new RemoteManagerWindow(viewModel);
        _remoteManagerWindow.Closed += OnRemoteManagerWindowClosed;
        _remoteManagerWindow.Activate();
    }

    private void OnRemoteManagerWindowClosed(object sender, WindowEventArgs args)
    {
        if (ReferenceEquals(_remoteManagerWindow, sender))
        {
            _remoteManagerWindow = null;
        }
    }

    private void CloseRemoteManagerWindow()
    {
        var window = _remoteManagerWindow;
        if (window is null)
        {
            return;
        }

        if (!window.IsClosed)
        {
            window.Close();
        }

        _remoteManagerWindow = null;
    }
}
