namespace SimpleFile.Core;

public static class ServiceLocator
{
    private static readonly string[] BuiltRelativePaths =
    [
        Path.Combine("target", "debug", "simplefile-service.exe"),
        Path.Combine("target", "release", "simplefile-service.exe"),
    ];

    public static string? FindServiceExecutable()
    {
        var overridePath = Environment.GetEnvironmentVariable("SIMPLEFILE_SERVICE_PATH");
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
        {
            return Path.GetFullPath(overridePath);
        }

        return FindServiceExecutable(CandidateRoots());
    }

    /// <summary>
    /// The service executable under the first of <paramref name="roots"/> that has one. In a
    /// development checkout both a debug and a release build may exist under <c>target</c>; the
    /// one written last is started, so a fresh build of either kind is not passed over for a
    /// stale build of the other. An installed service lies beside the app, with no builds.
    /// </summary>
    internal static string? FindServiceExecutable(IEnumerable<string> roots)
    {
        foreach (var start in roots)
        {
            var built = BuiltRelativePaths
                .Select(relative => Path.GetFullPath(Path.Combine(start, relative)))
                .Where(File.Exists)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
            if (built is not null)
            {
                return built;
            }

            var beside = Path.GetFullPath(Path.Combine(start, "simplefile-service.exe"));
            if (File.Exists(beside))
            {
                return beside;
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
