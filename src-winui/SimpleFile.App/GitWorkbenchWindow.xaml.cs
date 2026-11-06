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
        SecondaryWindowChrome.Apply(this, "Git Workbench", 1180, 740);

        Workbench.Start(viewModel);
        Closed += (_, _) => IsClosed = true;
    }

    public bool IsClosed { get; private set; }

    public GitWorkbenchView WorkbenchView => Workbench;
}
