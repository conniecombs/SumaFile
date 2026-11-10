using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using SimpleFile.PluginHost.Loading;
using Xunit;

namespace SimpleFile.Tests;

/// <summary>
/// Plan phase 5, the "Mvvm prefix" row: <c>CommunityToolkit.Mvvm</c> stays host-shared, and docs/PLUGINS.md prints the
/// shared prefixes and the package versions a plugin is bound to. These tests keep that section true.
/// </summary>
public class PluginSharedAssembliesDocTests
{
    private const string Section = "## Host-shared assemblies";

    [Fact]
    public void PluginLoadContext_SharedPrefixes_MatchTheTableInPluginsDoc()
    {
        var documented = Table(SectionText(), "Prefix");
        Assert.Equal(PluginLoadContext.SharedPrefixList.ToArray(), documented.Select(row => row.Key).ToArray());
        Assert.Contains("CommunityToolkit.Mvvm", documented.Select(row => row.Key));
        Assert.All(documented, row => Assert.False(string.IsNullOrWhiteSpace(row.Value), $"{row.Key} has no description."));
    }

    [Fact]
    public void PluginsDoc_PinnedVersions_MatchTheVersionsTheHostBuildsWith()
    {
        var documented = Table(SectionText(), "Package").ToDictionary(row => row.Key, row => row.Value.Trim('`'));
        Assert.Equal(["CommunityToolkit.Mvvm", "Microsoft.Windows.SDK.BuildTools", "Microsoft.WindowsAppSDK"], documented.Keys.Order(StringComparer.Ordinal));

        using var lockFile = JsonDocument.Parse(File.ReadAllText(Path.Combine(FindRepoRoot(), "src-winui", "SimpleFile.App", "packages.lock.json")));
        var framework = lockFile.RootElement.GetProperty("dependencies").EnumerateObject().First(target => !target.Name.Contains('/', StringComparison.Ordinal));
        foreach (var (package, version) in documented)
        {
            Assert.True(framework.Value.TryGetProperty(package, out var entry), $"The App lock file has no {package}.");
            Assert.Equal(entry.GetProperty("resolved").GetString(), version);
        }
    }

    /// <summary>The <c>## Host-shared assemblies</c> section of docs/PLUGINS.md, up to the next level-2 heading.</summary>
    private static string SectionText()
    {
        var doc = File.ReadAllText(Path.Combine(FindRepoRoot(), "docs", "PLUGINS.md")).Replace("\r\n", "\n", StringComparison.Ordinal);
        var start = doc.IndexOf("\n" + Section + "\n", StringComparison.Ordinal);
        Assert.True(start >= 0, $"docs/PLUGINS.md has no '{Section}' section.");
        var body = doc[(start + Section.Length + 2)..];
        var next = body.IndexOf("\n## ", StringComparison.Ordinal);
        return next < 0 ? body : body[..next];
    }

    /// <summary>
    /// The rows of the Markdown table whose first header cell is <paramref name="firstHeader"/>: the backticked first cell
    /// and the second cell, in document order.
    /// </summary>
    private static List<KeyValuePair<string, string>> Table(string section, string firstHeader)
    {
        var lines = section.Split('\n');
        var header = Array.FindIndex(lines, line => line.StartsWith("| " + firstHeader + " |", StringComparison.Ordinal));
        Assert.True(header >= 0, $"The '{Section}' section has no table headed '{firstHeader}'.");
        var rows = new List<KeyValuePair<string, string>>();
        foreach (var line in lines.Skip(header + 2))
        {
            if (!line.StartsWith('|')) break;
            var cells = line.Split('|', StringSplitOptions.TrimEntries)[1..^1];
            Assert.True(cells.Length >= 2, $"The table row '{line}' has fewer than two cells.");
            Assert.True(cells[0].Length > 2 && cells[0][0] == '`' && cells[0][^1] == '`', $"The first cell of '{line}' must be backticked.");
            rows.Add(new KeyValuePair<string, string>(cells[0][1..^1], cells[1]));
        }

        Assert.NotEmpty(rows);
        return rows;
    }

    /// The folder above the test binaries that holds both `Cargo.toml` and `src-winui`.
    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Cargo.toml")) && Directory.Exists(Path.Combine(dir.FullName, "src-winui")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("No folder above " + AppContext.BaseDirectory + " holds both Cargo.toml and src-winui.");
    }
}
