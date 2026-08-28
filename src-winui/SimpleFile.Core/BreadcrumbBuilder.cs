namespace SimpleFile.Core;

public sealed class BreadcrumbSegment
{
    public required string Label { get; init; }
    public required string Path { get; init; }
    public bool Current { get; init; }
}

/// <summary>
/// Builds Windows breadcrumb segments for the WinUI shell.
/// </summary>
public static class BreadcrumbBuilder
{
    public static IReadOnlyList<BreadcrumbSegment> FromPath(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return [];
        }

        var parts = path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        var currentAccumulated = "";
        var segments = new List<BreadcrumbSegment>(parts.Length);
        for (var index = 0; index < parts.Length; index++)
        {
            var part = parts[index];
            var isDrive = index == 0 && part.EndsWith(':');
            currentAccumulated += index == 0
                ? (isDrive ? $"{part}\\" : part)
                : $"\\{part}";
            segments.Add(new BreadcrumbSegment
            {
                Label = part,
                Path = currentAccumulated,
                Current = index == parts.Length - 1,
            });
        }

        return segments;
    }
}
