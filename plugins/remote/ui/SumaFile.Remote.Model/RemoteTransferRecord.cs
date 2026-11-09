using CommunityToolkit.Mvvm.ComponentModel;

namespace SumaFile.Remote.Model;

/// <summary>One finished item of a transfer, shown in the manager's transfer list.</summary>
public sealed class RemoteTransferRecord : ObservableObject
{
    private string _status = "Pending";
    private string _detail = "";

    public RemoteTransferRecord(string direction, string remotePath, string localPath)
    {
        Direction = direction;
        RemotePath = remotePath;
        LocalPath = localPath;
    }

    public string Direction { get; }
    public string RemotePath { get; }
    public string LocalPath { get; }
    public string FileName => string.IsNullOrWhiteSpace(RemotePath)
        ? Path.GetFileName(LocalPath)
        : RemotePath.TrimEnd('/').Split('/').LastOrDefault() ?? RemotePath;

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public string Detail
    {
        get => _detail;
        private set => SetProperty(ref _detail, value);
    }

    public void Apply(string status, string detail)
    {
        Status = status.Length == 0 ? "Unknown" : char.ToUpperInvariant(status[0]) + status[1..];
        Detail = detail;
    }

    public void Complete()
    {
        Status = "Completed";
        Detail = "";
    }

    public void Fail(string message)
    {
        Status = "Failed";
        Detail = message;
    }
}

/// <summary>Result of a connect attempt; host-key trust failures carry the parsed fingerprints.</summary>
public sealed record ConnectOutcome(
    bool Connected,
    string? UntrustedFingerprint,
    string? MismatchExpected,
    string? MismatchActual,
    string? Error)
{
    public static ConnectOutcome Success { get; } = new(true, null, null, null, null);

    public bool IsHostKeyProblem => UntrustedFingerprint is not null || MismatchActual is not null;
}
