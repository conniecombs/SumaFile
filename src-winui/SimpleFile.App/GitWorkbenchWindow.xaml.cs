using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace SimpleFile.App;

public sealed partial class GitWorkbenchWindow : Window
{
    public GitWorkbenchWindow(GitWorkbenchViewModel viewModel)
    {
        InitializeComponent();
        Title = "Git Workbench";
        AppIcon.ApplyTo(this);
        SystemBackdrop = new MicaBackdrop();
        AppWindow.Resize(new SizeInt32(1180, 740));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = true;
            presenter.IsMaximizable = true;
            presenter.IsMinimizable = true;
        }

        Workbench.Start(viewModel);
        Closed += (_, _) => IsClosed = true;
    }

    public bool IsClosed { get; private set; }

    public GitWorkbenchView WorkbenchView => Workbench;
}
