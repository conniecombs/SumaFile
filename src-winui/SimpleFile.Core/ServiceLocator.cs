namespace SimpleFile.Core;

public static class ServiceLocator
{
    public static string? FindServiceExecutable()
    {
        var overridePath = Environment.GetEnvironmentVariable("SIMPLEFILE_SERVICE_PATH");
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
        {
            return Path.GetFullPath(overridePath);
        }

        foreach (var start in CandidateRoots())
        {
            foreach (var relative in new[]
            {
                Path.Combine("target", "debug", "simplefile-service.exe"),
                Path.Combine("target", "release", "simplefile-service.exe"),
                "simplefile-service.exe",
            })
            {
                var candidate = Path.GetFullPath(Path.Combine(start, relative));
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static IEnumerable<string> CandidateRoots()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var start in new[]
        {
            AppContext.BaseDirectory,
            Environment.CurrentDirectory,
        })
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null && seen.Add(directory.FullName))
            {
                yield return directory.FullName;
                directory = directory.Parent;
            }
        }
    }
}
