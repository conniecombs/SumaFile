using Microsoft.UI.Xaml;

namespace SimpleFile.App;

public partial class App : Application
{
    private const long MaxLogBytes = 1_048_576; // 1 MB
    private Window? _window;

    public App()
    {
        UnhandledException += OnUnhandledException;
        try
        {
            InitializeComponent();
        }
        catch (Exception exception)
        {
            LogCrash("App.InitializeComponent", exception);
            throw;
        }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            LogCrash("OnLaunched", null);
            _window = new MainWindow();
            _window.Activate();
        }
        catch (Exception exception)
        {
            LogCrash("OnLaunched", exception);
            throw;
        }
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        LogCrash("UnhandledException", e.Exception);

        // Only swallow exceptions that are known to be recoverable.
        // Cancellation exceptions are benign (user navigated away, etc.).
        // All other exceptions may leave internal state inconsistent, so
        // let the runtime terminate the process after logging.
        if (e.Exception is OperationCanceledException or TaskCanceledException)
        {
            e.Handled = true;
            return;
        }

        // For genuinely unhandled exceptions, allow the process to terminate.
        // The diagnostic report has already been written to startup.log above.
        e.Handled = false;
    }

    internal static void LogCrash(string stage, Exception? exception)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SumaFile");
            Directory.CreateDirectory(directory);

            var logPath = Path.Combine(directory, "startup.log");
            RotateLogIfNeeded(logPath);

            var line = exception is null
                ? $"[{DateTime.Now:O}] {stage}"
                : $"[{DateTime.Now:O}] {stage}{Environment.NewLine}{exception}";
            File.AppendAllText(logPath, line + Environment.NewLine);
        }
        catch
        {
            // Logging must never take down startup further.
        }
    }

    private static void RotateLogIfNeeded(string logPath)
    {
        try
        {
            var info = new FileInfo(logPath);
            if (!info.Exists || info.Length < MaxLogBytes)
            {
                return;
            }

            var backup = logPath + ".1";
            if (File.Exists(backup))
            {
                File.Delete(backup);
            }

            File.Move(logPath, backup);
        }
        catch
        {
            // Best-effort rotation; don't fail startup.
        }
    }
}
