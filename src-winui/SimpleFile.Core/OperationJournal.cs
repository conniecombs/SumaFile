using System.Text.Json;
using System.Text.Json.Serialization;

namespace SimpleFile.Core;

public sealed class OperationJournal
{
    private readonly object _gate = new();

    public OperationJournal(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Journal path cannot be empty.", nameof(path));

        Path = path;
    }

    public string Path { get; }

    public static OperationJournal CreateDefault()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var root = string.IsNullOrWhiteSpace(localAppData)
            ? System.IO.Path.GetTempPath()
            : localAppData;
        return new OperationJournal(System.IO.Path.Combine(root, "SumaFile", "operations.jsonl"));
    }

    public void Started(
        string operationType,
        string operationId,
        IEnumerable<string>? sources = null,
        string? destination = null)
        => Append(OperationJournalEntry.Create(operationType, operationId, "started", sources, destination));

    public void Completed(string operationType, string operationId)
        => Append(OperationJournalEntry.Create(operationType, operationId, "completed"));

    public void Cancelled(string operationType, string operationId)
        => Append(OperationJournalEntry.Create(operationType, operationId, "cancelled"));

    public void Failed(string operationType, string operationId, Exception exception)
        => Append(OperationJournalEntry.Create(operationType, operationId, "failed", error: exception.Message));

    public IReadOnlyList<OperationJournalEntry> ReadEntries()
    {
        if (!File.Exists(Path))
            return [];

        var entries = new List<OperationJournalEntry>();
        foreach (var line in File.ReadLines(Path))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            try
            {
                var entry = JsonSerializer.Deserialize<OperationJournalEntry>(line);
                if (entry is not null)
                    entries.Add(entry);
            }
            catch
            {
            }
        }

        return entries;
    }

    private void Append(OperationJournalEntry entry)
    {
        try
        {
            var directory = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            var json = JsonSerializer.Serialize(entry);
            lock (_gate)
            {
                File.AppendAllText(Path, json + Environment.NewLine);
            }
        }
        catch
        {
        }
    }
}

public sealed class OperationJournalEntry
{
    [JsonPropertyName("timestamp")]
    public DateTimeOffset Timestamp { get; set; }

    [JsonPropertyName("operation_type")]
    public string OperationType { get; set; } = "";

    [JsonPropertyName("operation_id")]
    public string OperationId { get; set; } = "";

    [JsonPropertyName("state")]
    public string State { get; set; } = "";

    [JsonPropertyName("sources")]
    public string[] Sources { get; set; } = [];

    [JsonPropertyName("destination")]
    public string? Destination { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    public static OperationJournalEntry Create(
        string operationType,
        string operationId,
        string state,
        IEnumerable<string>? sources = null,
        string? destination = null,
        string? error = null)
    {
        return new OperationJournalEntry
        {
            Timestamp = DateTimeOffset.UtcNow,
            OperationType = operationType,
            OperationId = operationId,
            State = state,
            Sources = sources?.ToArray() ?? [],
            Destination = destination,
            Error = error,
        };
    }
}
