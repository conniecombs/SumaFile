namespace SimpleFile.Core;

/// <summary>An entry that was renamed inside its folder: the path it had and the path it has.</summary>
public readonly record struct PathRename(string From, string To);
