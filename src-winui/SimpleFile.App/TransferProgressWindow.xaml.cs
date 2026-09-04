using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using SimpleFile.Core;
using SimpleFile.Ipc;
using Windows.Graphics;

namespace SimpleFile.App;

public sealed partial class TransferProgressWindow : Window
{
    public TransferProgressWindow()
    {
        InitializeComponent();
        Title = "File transfer";
        AppIcon.ApplyTo(this);
        AppWindow.Resize(new SizeInt32(620, 360));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = true;
            presenter.IsMaximizable = true;
            presenter.IsMinimizable = true;
        }

        FileProgressPanel.CancelRequested += (_, _) => CancelRequested?.Invoke(this, EventArgs.Empty);
        FileProgressPanel.CloseRequested += (_, _) => Close();
        Closed += (_, _) => IsClosed = true;
    }

    public bool IsClosed { get; private set; }

    public event EventHandler? CancelRequested;

    public void Start(TransferProgressContext context)
    {
        Title = context.Move ? "Moving files" : "Copying files";
        FileProgressPanel.Start(context);
        Activate();
    }

    public void UpdateProgress(ProgressUpdate update)
    {
        if (!IsClosed)
        {
            FileProgressPanel.UpdateProgress(update);
        }
    }

    public void SetCancelling()
    {
        if (!IsClosed)
        {
            FileProgressPanel.SetCancelling();
        }
    }

    public void SetCompleted()
    {
        if (!IsClosed)
        {
            Title = "Transfer complete";
            FileProgressPanel.SetCompleted();
        }
    }
}
