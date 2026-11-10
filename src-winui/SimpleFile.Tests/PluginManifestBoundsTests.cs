using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using SimpleFile.PluginHost.Discovery;
using SimpleFile.PluginHost.Manifest;
using Xunit;

namespace SimpleFile.Tests;

/// <summary>
/// Final fix wave, items 7 and 8: a manifest whose <c>sdkVersion</c> minor is above the host's is IncompatibleSdk (the
/// SDK assembly version is major.minor.0.0, so such a plugin could not bind); the remaining free strings are bounded;
/// and an error never echoes more than 80 characters of a value.
/// </summary>
public sealed class PluginManifestBoundsTests
{
    private const string Id = "com.example.bounds";

    private static readonly Version Host = Version.Parse(SimpleFile.PluginSdk.PluginSdk.Version);

    private const string Full = """
    {
      "id": "com.example.bounds", "name": "N", "version": "1.0.0", "sdkVersion": "1.0.0",
      "publisher": { "name": "P", "url": "https://example.com" },
      "ui": { "assembly": "Bounds.dll", "entryType": "Bounds.Plugin" },
      "backend": { "executable": "bounds.exe", "acceptsMultipleClients": true },
      "contributions": {
        "commands": [ { "id": "go", "label": "Go", "shortcut": "Ctrl+G" } ],
        "archiveFormats": [ { "extension": ".bnd", "engineName": "Bounds", "canList": true } ],
        "sidebar": [ { "id": "side", "title": "Side", "provider": "side" } ],
        "panels": [ { "id": "panel", "title": "Panel", "provider": "panel" } ],
        "settingsPage": { "provider": "page" },
        "rowDecorations": { "provider": "rows" }
      },
      "settings": [
        { "key": "k", "type": "string", "label": "K", "default": "d" },
        { "key": "e", "type": "enum", "label": "E", "options": [ "a", "b" ], "default": "a" }
      ]
    }
    """;

    private static ManifestReadResult Read(string json) => PluginManifestReader.Read(json, Id);

    private static string With(Action<JsonNode> change)
    {
        var root = JsonNode.Parse(Full)!;
        change(root);
        return root.ToJsonString();
    }

    [Fact]
    public void TheFullManifest_IsValid()
    {
        var result = Read(Full);
        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    // ---- item 7: sdkVersion minor ----

    [Fact]
    public void IsSdkCompatible_AcceptsTheHostsMinorOrLower_AndAnyPatch()
    {
        Assert.True(PluginManifestReader.IsSdkCompatible(SimpleFile.PluginSdk.PluginSdk.Version));
        Assert.True(PluginManifestReader.IsSdkCompatible($"{Host.Major}.{Host.Minor}.{Host.Build + 7}"));
        Assert.True(PluginManifestReader.IsSdkCompatible($"{Host.Major}.0.0"));
        Assert.True(PluginManifestReader.IsSdkCompatible($"{Host.Major}.{Host.Minor}.0-preview.1"));
        Assert.False(PluginManifestReader.IsSdkCompatible($"{Host.Major}.{Host.Minor + 1}.0"));
        Assert.False(PluginManifestReader.IsSdkCompatible($"{Host.Major + 1}.0.0"));
        Assert.False(PluginManifestReader.IsSdkCompatible("banana"));
    }

    [Fact]
    public void Read_ASdkVersionWithANewerMinor_IsIncompatibleWithAClearMessage()
    {
        var newer = $"{Host.Major}.{Host.Minor + 1}.0";

        var result = Read(Full.Replace("\"sdkVersion\": \"1.0.0\"", $"\"sdkVersion\": \"{newer}\"", StringComparison.Ordinal));

        Assert.False(result.IsValid);
        Assert.Equal(
            [$"The sdkVersion '{newer}' needs a newer SumaFile: this host provides SDK {SimpleFile.PluginSdk.PluginSdk.Version}, "
             + $"and a plugin built against SDK {Host.Major}.{Host.Minor + 1} can use what it lacks."],
            result.Errors);
    }

    [Fact]
    public void Discovery_ASdkVersionWithANewerMinor_IsIncompatibleSdk()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "sumafile-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var folder = Path.Combine(tmp, Id);
            Directory.CreateDirectory(folder);
            File.WriteAllText(
                Path.Combine(folder, "plugin.json"),
                Full.Replace("\"sdkVersion\": \"1.0.0\"", $"\"sdkVersion\": \"{Host.Major}.{Host.Minor + 1}.0\"", StringComparison.Ordinal));

            var found = PluginDiscovery.Scan([tmp]).Single();

            Assert.Equal(DiscoveryState.IncompatibleSdk, found.State);
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    // ---- item 8: bounds ----

    [Theory]
    [InlineData("version", 64)]
    [InlineData("publisher.url", 2048)]
    [InlineData("ui.entryType", 256)]
    [InlineData("contributions.commands[0].shortcut", 64)]
    [InlineData("contributions.archiveFormats[0].engineName", 100)]
    [InlineData("contributions.archiveFormats[0].extension", 32)]
    [InlineData("contributions.sidebar[0].provider", 256)]
    [InlineData("contributions.panels[0].provider", 256)]
    [InlineData("contributions.settingsPage.provider", 256)]
    [InlineData("contributions.rowDecorations.provider", 256)]
    [InlineData("settings[0].default", 1000)]
    public void Read_AStringLongerThanItsLimit_IsInvalid(string field, int limit)
    {
        var atLimit = Read(WithString(field, new string('x', limit)));
        Assert.True(atLimit.IsValid, string.Join("; ", atLimit.Errors));

        var over = Read(WithString(field, new string('x', limit + 1)));

        Assert.False(over.IsValid);
        Assert.Equal([$"The {field} field is longer than {limit} characters."], over.Errors);
    }

    [Fact]
    public void Read_ANonStringDefault_IsBoundedByItsJsonText()
    {
        var over = Read(With(root => root["settings"]![0]!["default"] = new JsonArray(Enumerable.Range(0, 400).Select(i => (JsonNode)i).ToArray())));

        Assert.Equal(["The settings[0].default field is longer than 1000 characters."], over.Errors);
    }

    [Fact]
    public void Read_AnEnumOptionLongerThan200_IsInvalid()
    {
        var at = Read(With(root => root["settings"]![1]!["options"]![1] = new string('x', 200)));
        Assert.True(at.IsValid, string.Join("; ", at.Errors));

        var over = Read(With(root => root["settings"]![1]!["options"]![1] = new string('x', 201)));

        Assert.Equal(["The settings[1].options[1] field is longer than 200 characters."], over.Errors);
    }

    [Fact]
    public void Read_MoreThan256EnumOptions_IsInvalid()
    {
        JsonArray Options(int count) => new(Enumerable.Range(0, count).Select(i => (JsonNode)("o" + i.ToString(CultureInfo.InvariantCulture))).ToArray());
        var at = Read(With(root => { root["settings"]![1]!["options"] = Options(256); root["settings"]![1]!["default"] = "o0"; }));
        Assert.True(at.IsValid, string.Join("; ", at.Errors));

        var over = Read(With(root => { root["settings"]![1]!["options"] = Options(257); root["settings"]![1]!["default"] = "o0"; }));

        Assert.Equal(["The settings[1].options field has more than 256 entries."], over.Errors);
    }

    // ---- item 8: echoed values ----

    [Fact]
    public void Errors_EchoAtMost80CharactersOfAnId()
    {
        var id = new string('A', 300);

        var result = PluginManifestReader.Read(Full.Replace("\"com.example.bounds\"", "\"" + id + "\"", StringComparison.Ordinal), Id);

        var error = Assert.Single(result.Errors);
        Assert.StartsWith($"The id '{new string('A', 80)}…' is invalid", error, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('A', 81), error, StringComparison.Ordinal);
    }

    [Fact]
    public void Errors_EchoAtMost80CharactersOfASdkVersion()
    {
        var sdk = "1.0" + new string('9', 300);

        var result = Read(Full.Replace("\"sdkVersion\": \"1.0.0\"", $"\"sdkVersion\": \"{sdk}\"", StringComparison.Ordinal));

        var error = Assert.Single(result.Errors);
        Assert.Equal($"The sdkVersion '{sdk[..80]}…' is not a valid semantic version (major.minor.patch).", error);
    }

    [Fact]
    public void Errors_EchoAtMost80CharactersOfAnyValue()
    {
        var long1 = new string('z', 500);
        var result = Read(With(root =>
        {
            root["backend"]!["activateOn"] = long1;
            root["settings"]![0]!["type"] = long1;
            root["settings"]![0]!["migrateFrom"] = long1;
            root["contributions"]!["sidebar"]![0]!["id"] = long1;
            root["contributions"]!["panels"]![0]!["command"] = long1;
            root["ui"]!["assembly"] = "a/" + long1;
        }));

        Assert.NotEmpty(result.Errors);
        Assert.All(result.Errors, error =>
        {
            Assert.DoesNotContain(new string('z', 81), error, StringComparison.Ordinal);
            Assert.Contains("…'", error, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Echo_DoesNotSplitASurrogatePair()
    {
        var value = new string('a', 79) + "\U0001F600" + "tail";

        Assert.Equal(new string('a', 79) + "…", PluginManifestReader.Echo(value));
        Assert.Equal("short", PluginManifestReader.Echo("short"));
        Assert.Equal(new string('b', 80), PluginManifestReader.Echo(new string('b', 80)));
    }

    /// <summary><see cref="Full"/> with the string at <paramref name="path"/> (dots and <c>[i]</c>) replaced.</summary>
    private static string WithString(string path, string value) => With(root =>
    {
        var node = root;
        var segments = path.Split('.');
        for (var i = 0; i < segments.Length; i++)
        {
            var segment = segments[i];
            var bracket = segment.IndexOf('[', StringComparison.Ordinal);
            var name = bracket < 0 ? segment : segment[..bracket];
            if (i == segments.Length - 1 && bracket < 0)
            {
                node[name] = value;
                break;
            }

            node = node[name]!;
            if (bracket >= 0)
            {
                node = node[int.Parse(segment[(bracket + 1)..^1], CultureInfo.InvariantCulture)]!;
            }
        }
    });
}
