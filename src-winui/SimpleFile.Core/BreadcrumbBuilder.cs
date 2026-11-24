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

        if (PathRules.IsRecycleBinPath(path))
        {
            return
            [
                new BreadcrumbSegment
                {
                    Label = "Recycle Bin",
                    Path = PathRules.RecycleBinPath,
                    Current = true,
                },
            ];
        }

        if (PathRules.TrySplitUncRoot(path, out var uncRoot, out var remainder))
        {
            var uncParts = string.IsNullOrEmpty(remainder)
                ? []
                : remainder.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
            var uncSegments = new List<BreadcrumbSegment>(1 + uncParts.Length)
            {
                new BreadcrumbSegment
                {
                    Label = uncRoot,
                    Path = uncRoot,
                    Current = uncParts.Length == 0,
                },
            };
            var accumulated = uncRoot;
            for (var index = 0; index < uncParts.Length; index++)
            {
                accumulated = PathRules.JoinPath(accumulated, uncParts[index]);
                uncSegments.Add(new BreadcrumbSegment
                {
                    Label = uncParts[index],
                    Path = accumulated,
                    Current = index == uncParts.Length - 1,
                });
            }

            return uncSegments;
        }

        var parts = path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        var currentAccumulated = "";
        var segments = new List<BreadcrumbSegment>(parts.Length);
        for (var index = 0; index < parts.Length; index++)
        {
            var part = parts[index];
            var isDrive = index == 0 && part.EndsWith(':');
            // Joined, never concatenated: `V:\` + `\sync` would give `V:\\sync`, a path Windows accepts for
            // reading and writing files but rejects (error 87) when a transfer renames its finished file into place.
            currentAccumulated = index == 0
                ? (isDrive ? $"{part}\\" : part)
                : PathRules.JoinPath(currentAccumulated, part);
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
