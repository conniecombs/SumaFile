namespace SimpleFile.Core;

/// <summary>Turns a transfer failure kind or raw error into one plain sentence plus the raw details.</summary>
public static class TransferFailureText
{
    public static (string Summary, string Details) Describe(string? kind, string? rawError, string? path)
    {
        var details = rawError ?? "";
        var name = string.IsNullOrWhiteSpace(path) ? "this file" : FileNameOf(path);
        var haystack = $"{kind} {rawError}".ToLowerInvariant();

        string summary;
        if (ContainsAny(haystack, "access denied", "access_denied", "access is denied", "denied", "permission"))
        {
            summary = $"Can't write to {name}. Access was denied.";
        }
        else if (ContainsAny(haystack, "disk full", "disk_full", "no space", "not enough space"))
        {
            summary = "The destination drive is full.";
        }
        else if (ContainsAny(haystack, "connection", "timed out", "timeout", "no such host", "host not found", "host unreachable"))
        {
            summary = "Lost the connection to the server.";
        }
        else if (ContainsAny(haystack, "not found", "not_found", "notfound", "no such file", "does not exist"))
        {
            summary = $"{name} no longer exists.";
        }
        else if (ContainsAny(haystack, "cancel"))
        {
            summary = "The transfer was cancelled.";
        }
        else
        {
            summary = $"Couldn't finish {name}.";
        }

        return (summary, details);
    }

    private static bool ContainsAny(string haystack, params string[] needles)
    {
        foreach (var needle in needles)
        {
            if (haystack.Contains(needle, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string FileNameOf(string path)
    {
        var trimmed = path.Trim().TrimEnd('\\', '/');
        var index = trimmed.LastIndexOfAny(['\\', '/']);
        var name = index >= 0 ? trimmed[(index + 1)..] : trimmed;
        return name.Length == 0 ? path.Trim() : name;
    }
}
