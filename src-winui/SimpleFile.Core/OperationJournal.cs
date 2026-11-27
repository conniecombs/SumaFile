using System.Text.Json;
using System.Text.Json.Serialization;

namespace SimpleFile.Core;

public sealed class OperationJournal
{
    /// <summary>Failed items one line lists; the line still says how many there were.</summary>
    public const int MaxFailuresPerEntry = 200;

    /// <summary>The journal starts again, with the earlier lines kept beside it, at this size.</summary>
    public const long DefaultMaxBytes = 4 * 1024 * 1024;

    private readonly object _gate = new();
    private readonly long _maxBytes;
    private readonly Action<string> _log;
    /// <summary>The last attempt to start the journal again failed; logged once until one works.</summary>
    private bool _restartFailing;

    public OperationJournal(string path, long maxBytes = DefaultMaxBytes)
        : this(path, maxBytes, message => StartupTrace.WriteInstant("OperationJournal", "rotate", message))
    {
    }

    /// <summary>A journal that hands what it cannot do to <paramref name="log"/> (tests).</summary>
    internal OperationJournal(string path, long maxBytes, Action<string> log)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Journal path cannot be empty.", nameof(path));

        Path = path;
        _maxBytes = maxBytes;
        _log = log;
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

    public void Completed(string operationType, string operationId, OperationSummary? summary = null)
        => Append(OperationJournalEntry.Create(operationType, operationId, "completed").With(summary));

    public void Cancelled(string operationType, string operationId, OperationSummary? summary = null)
        => Append(OperationJournalEntry.Create(operationType, operationId, "cancelled").With(summary));

    public void Failed(string operationType, string operationId, Exception exception, OperationSummary? summary = null)
        => Append(OperationJournalEntry.Create(operationType, operationId, "failed", error: exception.Message)
            .With(summary));

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
                StartAgainWhenLarge();
                File.AppendAllText(Path, json + Environment.NewLine);
            }
        }
        catch
        {
        }
    }

    /// <summary>
    /// The journal was appended to and never trimmed. A full one is kept beside the new one under the same
    /// name with ".1" added, which replaces the one kept before. When it cannot be (the kept file is read-only
    /// or locked, or another instance holds the journal), the line still goes to the full one: the failure is
    /// logged once, and the next line tries again.
    /// </summary>
    private void StartAgainWhenLarge()
    {
        var info = new FileInfo(Path);
        if (_maxBytes <= 0 || !info.Exists || info.Length < _maxBytes)
        {
            return;
        }

        try
        {
            File.Move(Path, Path + ".1", overwrite: true);
            _restartFailing = false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            if (_restartFailing)
            {
                return;
            }

            _restartFailing = true;
            try
            {
                _log($"{Path} could not be started again, so lines go on being added to it: {exception.Message}");
            }
            catch
            {
            }
        }
    }
}

/// <summary>What a pass did, for the line that ends it in the journal.</summary>
public sealed class OperationSummary
{
    public TimeSpan? Duration { get; init; }
    /// <summary>Bytes done and bytes in all, as the last progress report had them.</summary>
    public ulong? Bytes { get; init; }
    public ulong? TotalBytes { get; init; }
    /// <summary>Files done and files in all, as the last progress report had them.</summary>
    public ulong? Files { get; init; }
    public ulong? TotalFiles { get; init; }
    /// <summary>Selected items that were transferred.</summary>
    public int? Items { get; init; }
    /// <summary>Selected items that were left as they were.</summary>
    public int? Skipped { get; init; }
    public IReadOnlyList<SimpleFile.Ipc.TransferFailure>? Failures { get; init; }
}

public sealed class OperationJournalFailure
{
    [JsonPropertyName("source")]
    public string Source { get; set; } = "";

    [JsonPropertyName("destination")]
    public string Destination { get; set; } = "";

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "";

    [JsonPropertyName("error")]
    public string Error { get; set; } = "";
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

    // The fields below are written only by the line that ends a pass, and only when they are known.

    [JsonPropertyName("duration_ms")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? DurationMs { get; set; }

    [JsonPropertyName("bytes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ulong? Bytes { get; set; }

    [JsonPropertyName("total_bytes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ulong? TotalBytes { get; set; }

    [JsonPropertyName("files")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ulong? Files { get; set; }

    [JsonPropertyName("total_files")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ulong? TotalFiles { get; set; }

    [JsonPropertyName("items")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Items { get; set; }

    [JsonPropertyName("skipped")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Skipped { get; set; }

    /// <summary>How many items failed; <see cref="Failures"/> lists the first of them.</summary>
    [JsonPropertyName("failure_count")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? FailureCount { get; set; }

    [JsonPropertyName("failures")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OperationJournalFailure[]? Failures { get; set; }

    internal OperationJournalEntry With(OperationSummary? summary)
    {
        if (summary is null)
        {
            return this;
        }

        DurationMs = summary.Duration is { } duration ? (long)duration.TotalMilliseconds : null;
        Bytes = summary.Bytes;
        TotalBytes = summary.TotalBytes;
        Files = summary.Files;
        TotalFiles = summary.TotalFiles;
        Items = summary.Items;
        Skipped = summary.Skipped;
        if (summary.Failures is { Count: > 0 } failures)
        {
            FailureCount = failures.Count;
            Failures = failures
                .Take(OperationJournal.MaxFailuresPerEntry)
                .Select(failure => new OperationJournalFailure
                {
                    Source = failure.Source,
                    Destination = failure.Destination,
                    Kind = failure.Kind,
                    Error = failure.Error,
                })
                .ToArray();
        }

        return this;
    }

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
