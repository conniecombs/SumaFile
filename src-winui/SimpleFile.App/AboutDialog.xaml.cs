using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SimpleFile.Core;
using SimpleFile.Ipc;

namespace SimpleFile.App;

public sealed partial class AboutDialog : ContentDialog
{
    private const string RepositoryUrl = "https://github.com/conniecombs/SimpleFile-Windows";

    public AboutDialog()
    {
        InitializeComponent();
    }

    public Func<string, Task>? OpenUrlAsync { get; set; }

    public void SetInfo(AppAboutInfo info)
    {
        VersionText.Text = $"Version {info.Version}";
        OsText.Text = $"{info.Os} ({info.Arch})";
    }

    private async void OnGitHubClicked(object sender, RoutedEventArgs e)
    {
        if (OpenUrlAsync is null)
        {
            return;
        }

        var link = sender as Control;
        if (link is not null)
        {
            link.IsEnabled = false;
        }

        StatusText.Visibility = Visibility.Collapsed;
        try
        {
            await OpenUrlAsync(RepositoryUrl).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            StatusText.Text = exception.Message;
            StatusText.Visibility = Visibility.Visible;
        }
        finally
        {
            if (link is not null)
            {
                link.IsEnabled = true;
            }
        }
    }
}
