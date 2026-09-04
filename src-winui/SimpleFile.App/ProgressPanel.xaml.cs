using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SimpleFile.Core;
using SimpleFile.Ipc;

namespace SimpleFile.App;

public sealed partial class ProgressPanel : UserControl
{
    private TransferProgressContext _context = new(false, 0, "", "");
    private DateTimeOffset _lastSampleAt;
    private DateTimeOffset _startedAt;
    private ulong _lastSampleBytes;
    private double? _bytesPerSecond;
    private bool _hasSample;
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
        ResetRate();
        _lastCurrentItemPath = "";
        _startedAt = DateTimeOffset.UtcNow;
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
        if (string.IsNullOrWhiteSpace(update.CurrentItem))
        {
            update.CurrentItem = _lastCurrentItemPath;
        }
        else
        {
            _lastCurrentItemPath = update.CurrentItem;
        }

        var speed = TrackSpeed(update);
        var display = TransferProgressFormatter.Format(_context, update, speed, AverageFilesPerSecond(update));
        CancelButton.IsEnabled = update.Status == "running";
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

    private double? TrackSpeed(ProgressUpdate update)
    {
        if (update.Status != "running")
        {
            return _bytesPerSecond;
        }

        var now = DateTimeOffset.UtcNow;
        if (!_hasSample)
        {
            _hasSample = true;
            _lastSampleAt = now;
            _lastSampleBytes = update.Current;
            return _bytesPerSecond;
        }

        if (update.Current < _lastSampleBytes)
        {
            _lastSampleAt = now;
            _lastSampleBytes = update.Current;
            _bytesPerSecond = null;
            return _bytesPerSecond;
        }

        var elapsed = (now - _lastSampleAt).TotalSeconds;
        if (elapsed < 0.25 || update.Current == _lastSampleBytes)
        {
            return _bytesPerSecond;
        }

        var sample = (update.Current - _lastSampleBytes) / elapsed;
        _bytesPerSecond = _bytesPerSecond is > 0
            ? (_bytesPerSecond.Value * 0.65) + (sample * 0.35)
            : sample;
        _lastSampleAt = now;
        _lastSampleBytes = update.Current;
        return _bytesPerSecond;
    }

    private double? AverageFilesPerSecond(ProgressUpdate update)
    {
        if (update.CurrentFiles == 0)
        {
            return null;
        }

        var elapsed = (DateTimeOffset.UtcNow - _startedAt).TotalSeconds;
        return elapsed > 0.25 ? update.CurrentFiles / elapsed : null;
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

    private void ResetRate()
    {
        _hasSample = false;
        _lastSampleAt = default;
        _lastSampleBytes = 0;
        _bytesPerSecond = null;
    }
}
