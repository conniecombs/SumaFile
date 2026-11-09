using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SimpleFile.Core;
using Windows.Graphics;

namespace SimpleFile.App;

public sealed partial class TransferProgressWindow : Window
{
    private ISettingsBackend? _settings;
    public TransferProgressWindow()
    {
        InitializeComponent();
        // Escape closes the window; transfers keep running in the background.
        SecondaryWindowChrome.Apply(this, "Transfers", 800, 620, mica: false, cancel: () => CloseRequested?.Invoke());

        Closed += (_, _) => IsClosed = true;
    }

    public bool IsClosed { get; private set; }

    public event Action<TransferOperationViewModel>? CancelRequested;
    public event Action? ClearCompletedRequested;
    public event Action? CloseRequested;

    public void Start(TransferManagerViewModel manager, ISettingsBackend? settings = null)
    {
        _settings = settings;
        Root.DataContext = manager;
        TransferList.ItemsSource = manager.Operations;
    }

    private async void OnDefaultsClicked(object sender, RoutedEventArgs e)
    {
        if (_settings is not null) await TransferOptionsDialog.ShowAsync(Root.XamlRoot, _settings);
    }

    private async void OnTaskOptionsClicked(object sender, RoutedEventArgs e)
    {
        // A plugin or archive-engine row never reads transfer.overrides.v1: its settings live in its plugin.
        if (_settings is not null && sender is Button { Tag: TransferOperationViewModel { IsExternal: false } operation })
            await TransferOptionsDialog.ShowAsync(Root.XamlRoot, _settings, operation);
    }

    private void OnCancelOperationClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: TransferOperationViewModel operation })
        {
            CancelRequested?.Invoke(operation);
        }
    }

    private void OnClearCompletedClicked(object sender, RoutedEventArgs e)
    {
        ClearCompletedRequested?.Invoke();
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e)
    {
        CloseRequested?.Invoke();
    }
}
