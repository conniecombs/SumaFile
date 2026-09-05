using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SimpleFile.Core;
using Windows.Graphics;

namespace SimpleFile.App;

public sealed partial class TransferProgressWindow : Window
{
    public TransferProgressWindow()
    {
        InitializeComponent();
        Title = "Transfers";
        AppIcon.ApplyTo(this);
        AppWindow.Resize(new SizeInt32(700, 460));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = true;
            presenter.IsMaximizable = true;
            presenter.IsMinimizable = true;
        }

        Closed += (_, _) => IsClosed = true;
    }

    public bool IsClosed { get; private set; }

    public event Action<TransferOperationViewModel>? CancelRequested;
    public event Action? ClearCompletedRequested;
    public event Action? CloseRequested;

    public void Start(TransferManagerViewModel manager)
    {
        Root.DataContext = manager;
        TransferList.ItemsSource = manager.Operations;
        Activate();
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
