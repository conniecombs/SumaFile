using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SimpleFile.Core;
using SimpleFile.Ipc;

namespace SimpleFile.App;

public sealed partial class ProgressPanel : UserControl
{
    private TransferProgressContext _context = new(false, 0, "", "");
    private readonly TransferProgressRateTracker _rateTracker = new();
    private string _lastCurrentItemPath = "";
    private bool _isComplete;

    public event EventHandler? CancelRequested;
    public event EventHandler? CloseRequested;

    public ProgressPanel()
    {
        InitializeComponent();
        CancelButton.Click += (_, _) =>
        {
            if (_isComplete)
            {
                CloseRequested?.Invoke(this, EventArgs.Empty);
                return;
            }

            CancelRequested?.Invoke(this, EventArgs.Empty);
        };
    }

    public void Start(TransferProgressContext context)
    {
        _context = context;
        _rateTracker.Reset();
        _lastCurrentItemPath = "";
        Visibility = Visibility.Visible;
        CancelButton.IsEnabled = true;
        PauseButton.IsEnabled = false;
        _isComplete = false;
        CancelButtonIcon.Glyph = "\uE711";
        CancelButtonLabel.Text = "Cancel";

        ApplyDisplay(TransferProgressFormatter.Format(
            _context,
            new ProgressUpdate
            {
                OperationType = context.Move ? "move" : "copy",
                Status = "running",
                CurrentItem = "Preparing transfer",
            },
            null,
            null));
    }

    public void UpdateProgress(ProgressUpdate update)
    {
        if (update.Status == "completed")
        {
            SetCompleted();
            return;
        }

        if (string.IsNullOrWhiteSpace(update.CurrentItem))
        {
            update.CurrentItem = _lastCurrentItemPath;
        }
        else
        {
            _lastCurrentItemPath = update.CurrentItem;
        }

        var display = _rateTracker.Format(_context, update);
        CancelButton.IsEnabled = update.Status is "running" or "finalizing";
        PauseButton.IsEnabled = false;
        ApplyDisplay(display);
    }

    public void SetCancelling()
    {
        _isComplete = false;
        OperationLabel.Text = _context.Move ? "Cancelling move" : "Cancelling copy";
        SummaryLabel.Text = "Stopping transfer safely";
        CancelButton.IsEnabled = false;
        PauseButton.IsEnabled = false;
        ProgressBar.IsIndeterminate = true;
        FileProgressBar.IsIndeterminate = true;
    }

    public void SetCompleted()
    {
        var itemCount = Math.Max(_context.ItemCount, 1);
        _isComplete = true;
        CancelButton.IsEnabled = true;
        PauseButton.IsEnabled = false;
        ProgressBar.Value = 100;
        ProgressBar.IsIndeterminate = false;
        FileProgressBar.Value = 100;
        FileProgressBar.IsIndeterminate = false;
        OperationLabel.Text = _context.Move ? "Move complete" : "Copy complete";
        SummaryLabel.Text = "Transfer complete";
        PercentLabel.Text = "100%";
        FileSummaryLabel.Text = itemCount == 1 ? "1 item complete" : $"{itemCount} items complete";
        FileRateLabel.Text = "Files complete";
        CurrentItemLabel.Text = "Transfer complete";
        SpeedLabel.Text = "Complete";
        EtaLabel.Text = "Done";
        CancelButtonIcon.Glyph = "\uE8BB";
        CancelButtonLabel.Text = "Close";
        ToolTipService.SetToolTip(CurrentItemLabel, null);
    }

    private void ApplyDisplay(TransferProgressDisplay display)
    {
        OperationLabel.Text = display.Title;
        SummaryLabel.Text = display.Summary;
        PercentLabel.Text = display.Percent;
        FileSummaryLabel.Text = display.FileSummary;
        FileRateLabel.Text = display.FileRate;
        CurrentItemLabel.Text = display.CurrentItemName;
        FromLabel.Text = display.From;
        ToLabel.Text = display.To;
        SpeedLabel.Text = display.Speed;
        EtaLabel.Text = display.Eta;

        ToolTipService.SetToolTip(CurrentItemLabel, display.CurrentItemPath);
        ToolTipService.SetToolTip(FromLabel, display.From);
        ToolTipService.SetToolTip(ToLabel, display.To);

        ProgressBar.IsIndeterminate = display.IsIndeterminate;
        if (!display.IsIndeterminate)
        {
            ProgressBar.Value = display.ProgressPercent;
        }

        FileProgressBar.IsIndeterminate = display.FileProgressIsIndeterminate;
        if (!display.FileProgressIsIndeterminate)
        {
            FileProgressBar.Value = display.FileProgressPercent;
        }
    }

}
