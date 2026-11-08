using System.Linq;
using SimpleFile.PluginHost.Manifest;
using Xunit;

namespace SimpleFile.Tests;

public class PluginManifestReaderTests
{
    private const string Full = """
    {
      "id": "com.example.hello",
      "name": "Hello",
      "version": "1.2.3",
      "publisher": { "name": "Example", "url": "https://example.com", "publicKey": "abc" },
      "sdkVersion": "1.0.0",
      "ui": { "assembly": "Hello.dll", "entryType": "Hello.Plugin" },
      "backend": { "executable": "hello.exe", "args": ["--x"], "activateOn": "startup", "acceptsMultipleClients": true },
      "contributions": {
        "commands": [
          { "id": "greet", "label": "Greet", "group": "hello", "shortcut": "Ctrl+G" },
          { "id": "wave", "label": "Wave", "group": "hello" }
        ],
        "contextMenu": [
          { "group": "hello", "items": [ { "command": "greet", "label": "Greet", "when": "singleFile" } ] }
        ],
        "toolbar": [ { "command": "greet", "label": "Greet", "glyph": "E70F", "placement": "primary" } ],
        "archiveFormats": [
          { "extension": ".hello", "canList": true, "canExtract": true, "canCreate": false, "canModify": false, "engineName": "hello" }
        ]
      },
      "settings": [
        { "key": "volume", "type": "integer", "label": "Volume", "default": 5 },
        { "key": "mode", "type": "enum", "label": "Mode", "options": ["a", "b"], "default": "a" }
      ],
      "permissions": ["fs.read"]
    }
    """;

    private static string Minimal(string id = "com.example.a", string extra = "") =>
        "{ \"id\": " + System.Text.Json.JsonSerializer.Serialize(id) + ", \"name\": \"N\", \"version\": \"1.0.0\", \"sdkVersion\": \"1.0.0\", " +
        "\"publisher\": { \"name\": \"P\" }" + extra + " }";

    [Fact]
    public void Read_ValidManifest_ReturnsAllFields()
    {
        var r = PluginManifestReader.Read(Full, "com.example.hello");
        Assert.True(r.IsValid, string.Join("; ", r.Errors));
        var m = r.Manifest!;
        Assert.Equal("com.example.hello", m.Id);
        Assert.Equal("Example", m.Publisher.Name);
        Assert.Equal("startup", m.Backend!.ActivateOn);
        Assert.True(m.Backend.AcceptsMultipleClients);
        Assert.Equal("Hello.Plugin", m.Ui!.EntryType);
        Assert.Equal(2, m.Contributions.Commands.Count);
        Assert.Single(m.Contributions.ArchiveFormats);
        Assert.Equal(".hello", m.Contributions.ArchiveFormats[0].Extension);
        Assert.Equal(5, m.Settings[0].Default!.Value.GetInt32());
        Assert.Equal(new[] { "a", "b" }, m.Settings[1].Options);
        Assert.Equal("fs.read", Assert.Single(m.Permissions));
    }

    [Theory]
    [InlineData("Com.Example")]
    [InlineData("a")]
    [InlineData("..")]
    [InlineData("ex/ample")]
    [InlineData("ex\\ample")]
    [InlineData("com.example\n")]
    public void Read_RejectsBadIds(string id)
    {
        var r = PluginManifestReader.Read(Minimal(id), id == "com.example\n" ?"com.example" : id);
        Assert.False(r.IsValid);
        Assert.Contains(r.Errors, e => e.Contains("id"));
        Assert.Contains(r.Errors, e => e.Contains("is invalid"));
    }

    [Fact]
    public void Read_RejectsFolderNameMismatch()
    {
        var r = PluginManifestReader.Read(Minimal("com.example.a"), "com.example.b");
        Assert.False(r.IsValid);
        Assert.Contains(r.Errors, e => e.Contains("folder"));
    }

    [Fact]
    public void Read_RejectsMissingRequiredFields()
    {
        var r = PluginManifestReader.Read("{ \"id\": \"com.example.a\", \"publisher\": {} }", "com.example.a");
        Assert.False(r.IsValid);
        Assert.Equal(4, r.Errors.Count);
        Assert.Contains(r.Errors, e => e.Contains("name") && !e.Contains("publisher"));
        Assert.Contains(r.Errors, e => e.Contains("version"));
        Assert.Contains(r.Errors, e => e.Contains("sdkVersion"));
        Assert.Contains(r.Errors, e => e.Contains("publisher.name"));
    }

    [Fact]
    public void Read_RejectsIncompatibleSdkMajor()
    {
        var json = Minimal().Replace("\"sdkVersion\": \"1.0.0\"", "\"sdkVersion\": \"2.0.0\"");
        var r = PluginManifestReader.Read(json, "com.example.a");
        Assert.False(r.IsValid);
        Assert.Contains(r.Errors, e => e.Contains("sdkVersion"));
        Assert.False(PluginManifestReader.IsSdkCompatible("2.0.0"));
        Assert.True(PluginManifestReader.IsSdkCompatible("1.4.2"));
        Assert.False(PluginManifestReader.IsSdkCompatible("banana"));
    }

    [Fact]
    public void Read_RejectsDuplicateCommandIds()
    {
        var extra = ", \"contributions\": { \"commands\": [ " +
            "{ \"id\": \"greet\", \"label\": \"A\", \"group\": \"g\" }, { \"id\": \"greet\", \"label\": \"B\", \"group\": \"g\" } ] }";
        var r = PluginManifestReader.Read(Minimal(extra: extra), "com.example.a");
        Assert.False(r.IsValid);
        Assert.Contains(r.Errors, e => e.Contains("greet"));
    }

    [Fact]
    public void Read_RejectsUnknownEnumValues()
    {
        var extra = ", \"backend\": { \"executable\": \"x.exe\", \"activateOn\": \"always\" }" +
            ", \"contributions\": { \"toolbar\": [ { \"command\": \"c\", \"label\": \"L\", \"glyph\": \"g\", \"placement\": \"left\" } ] }" +
            ", \"settings\": [ { \"key\": \"k\", \"type\": \"float\", \"label\": \"K\" } ]";
        var r = PluginManifestReader.Read(Minimal(extra: extra), "com.example.a");
        Assert.False(r.IsValid);
        Assert.Equal(3, r.Errors.Count);
        Assert.Contains(r.Errors, e => e.Contains("activateOn"));
        Assert.Contains(r.Errors, e => e.Contains("placement"));
        Assert.Contains(r.Errors, e => e.Contains("type"));
    }

    [Fact]
    public void Read_IgnoresUnknownTopLevelProperties()
    {
        var r = PluginManifestReader.Read(Minimal(extra: ", \"panels\": []"), "com.example.a");
        Assert.True(r.IsValid, string.Join("; ", r.Errors));
    }

    [Fact]
    public void Read_DefaultsOptionalSections()
    {
        var r = PluginManifestReader.Read(Minimal(), "com.example.a");
        Assert.True(r.IsValid, string.Join("; ", r.Errors));
        var m = r.Manifest!;
        Assert.Null(m.Backend);
        Assert.Null(m.Ui);
        Assert.Empty(m.Settings);
        Assert.Empty(m.Permissions);
        Assert.Empty(m.Contributions.Commands);
        Assert.Empty(m.Contributions.ContextMenu);
        Assert.Empty(m.Contributions.Toolbar);
        Assert.Empty(m.Contributions.ArchiveFormats);
    }

    private static string Toolbar(string placementJson) =>
        ", \"contributions\": { \"toolbar\": [ { \"command\": \"c\", \"label\": \"L\", \"glyph\": \"g\"" + placementJson + " } ] }";

    [Fact]
    public void Read_DefaultsOmittedToolbarPlacementToOverflow()
    {
        var r = PluginManifestReader.Read(Minimal(extra: Toolbar("")), "com.example.a");
        Assert.True(r.IsValid, string.Join("; ", r.Errors));
        Assert.Equal("overflow", r.Manifest!.Contributions.Toolbar[0].Placement);
    }

    [Fact]
    public void Read_RejectsWrongTypedToolbarPlacement()
    {
        var r = PluginManifestReader.Read(Minimal(extra: Toolbar(", \"placement\": 5")), "com.example.a");
        Assert.False(r.IsValid);
        Assert.Single(r.Errors);
        Assert.Contains("placement", r.Errors[0]);
    }

    [Fact]
    public void Read_RejectsEnumSettingWithoutOptions()
    {
        var extra = ", \"settings\": [ { \"key\": \"k\", \"type\": \"enum\", \"label\": \"K\", \"options\": [] } ]";
        var r = PluginManifestReader.Read(Minimal(extra: extra), "com.example.a");
        Assert.False(r.IsValid);
        Assert.Contains(r.Errors, e => e.Contains("settings[0].options"));
    }

    [Fact]
    public void Read_RejectsOptionsOnNonEnumSetting()
    {
        var extra = ", \"settings\": [ { \"key\": \"k\", \"type\": \"string\", \"label\": \"K\", \"options\": [\"a\"] } ]";
        var r = PluginManifestReader.Read(Minimal(extra: extra), "com.example.a");
        Assert.False(r.IsValid);
        Assert.Contains(r.Errors, e => e.Contains("settings[0].options"));
    }

    [Fact]
    public void Read_RejectsDuplicateSettingKeys()
    {
        var extra = ", \"settings\": [ { \"key\": \"dup\", \"type\": \"string\", \"label\": \"A\" }, { \"key\": \"dup\", \"type\": \"string\", \"label\": \"B\" } ]";
        var r = PluginManifestReader.Read(Minimal(extra: extra), "com.example.a");
        Assert.False(r.IsValid);
        Assert.Contains(r.Errors, e => e.Contains("dup"));
    }

    [Fact]
    public void Read_RejectsNonBareUiAssembly()
    {
        var extra = ", \"ui\": { \"assembly\": \"..\\\\evil.dll\", \"entryType\": \"T\" }";
        var r = PluginManifestReader.Read(Minimal(extra: extra), "com.example.a");
        Assert.False(r.IsValid);
        Assert.Contains(r.Errors, e => e.Contains("ui.assembly"));
    }

    [Fact]
    public void Read_RejectsNonBareBackendExecutable()
    {
        var extra = ", \"backend\": { \"executable\": \"C:/x.exe\" }";
        var r = PluginManifestReader.Read(Minimal(extra: extra), "com.example.a");
        Assert.False(r.IsValid);
        Assert.Contains(r.Errors, e => e.Contains("backend.executable"));
    }

    private static string MenuWhen(string when) =>
        ", \"contributions\": { \"contextMenu\": [ { \"group\": \"g\", \"items\": [ " +
        "{ \"command\": \"c\", \"label\": \"L\", \"when\": \"" + when + "\" } ] } ] }";

    [Fact]
    public void Read_RejectsOverlongWhen()
    {
        var r = PluginManifestReader.Read(Minimal(extra: MenuWhen(new string('a', 513))), "com.example.a");
        Assert.False(r.IsValid);
        Assert.Contains(r.Errors, e => e.Contains("contextMenu") && e.Contains("when") && e.Contains("512"));
    }

    [Fact]
    public void Read_RejectsInvalidWhen()
    {
        var r = PluginManifestReader.Read(Minimal(extra: MenuWhen("selectionCount >")), "com.example.a");
        Assert.False(r.IsValid);
        Assert.Contains(r.Errors, e => e.Contains("contextMenu") && e.Contains("when") && e.Contains("Expected a number"));
    }

    [Fact]
    public void Read_AcceptsValidWhen()
    {
        var r = PluginManifestReader.Read(Minimal(extra: MenuWhen("selectionCount >= 1")), "com.example.a");
        Assert.True(r.IsValid);
    }

    [Theory]
    [InlineData("b.c")]
    [InlineData("..x")]
    public void Read_RejectsSettingKeysOutsideTheKeyGrammar(string key)
    {
        var extra = ", \"settings\": [ { \"key\": \"" + key + "\", \"type\": \"string\", \"label\": \"A\" } ]";
        var r = PluginManifestReader.Read(Minimal(extra: extra), "com.example.a");
        Assert.False(r.IsValid);
        Assert.Contains(r.Errors, e => e.Contains("settings[0].key") && e.Contains(key));
    }

    private const string HelloFormat =
        "\"contributions\": { \"archiveFormats\": [ { \"extension\": \".hello\", \"canList\": true, \"engineName\": \"hello\" } ] }";

    [Fact]
    public void Read_RejectsArchiveFormatsWithoutBackend()
    {
        var r = PluginManifestReader.Read(Minimal(extra: ", " + HelloFormat), "com.example.a");
        Assert.False(r.IsValid);
        Assert.Contains(r.Errors, e => e.Contains("archiveFormats") && e.Contains("acceptsMultipleClients"));
    }

    [Fact]
    public void Read_RejectsArchiveFormatsWithSingleClientBackend()
    {
        var extra = ", \"backend\": { \"executable\": \"b.exe\", \"acceptsMultipleClients\": false }, " + HelloFormat;
        var r = PluginManifestReader.Read(Minimal(extra: extra), "com.example.a");
        Assert.False(r.IsValid);
        Assert.Contains(r.Errors, e => e.Contains("archiveFormats") && e.Contains("acceptsMultipleClients"));
    }

    [Fact]
    public void Read_AcceptsArchiveFormatsWithMultiClientBackend()
    {
        var extra = ", \"backend\": { \"executable\": \"b.exe\", \"acceptsMultipleClients\": true }, " + HelloFormat;
        var r = PluginManifestReader.Read(Minimal(extra: extra), "com.example.a");
        Assert.True(r.IsValid, string.Join("; ", r.Errors));
    }
}
