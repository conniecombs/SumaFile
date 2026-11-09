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

    private const string UiJson = ", \"ui\": { \"assembly\": \"A.dll\", \"entryType\": \"A.Plugin\" }";

    private static string Contributions(string body, bool ui = true) =>
        (ui ? UiJson : "") + ", \"contributions\": { " + body + " }";

    private const string GreetCommand = "\"commands\": [ { \"id\": \"greet\", \"label\": \"Greet\", \"group\": \"G\" } ]";

    [Fact]
    public void Read_SidebarAndSettingsPage_AreParsed()
    {
        var r = PluginManifestReader.Read(Minimal(extra: Contributions(GreetCommand + """
            , "sidebar": [
                { "id": "conn", "title": "Connections", "provider": "conn-section", "command": "greet" },
                { "id": "second_one", "title": "Second", "provider": "second" }
              ],
              "settingsPage": { "provider": "page" }
            """)), "com.example.a");

        Assert.True(r.IsValid, string.Join("; ", r.Errors));
        var contributions = r.Manifest!.Contributions;
        Assert.Equal(2, contributions.Sidebar.Count);
        Assert.Equal(new PluginSidebarSpec("conn", "Connections", "conn-section", "greet"), contributions.Sidebar[0]);
        Assert.Equal(new PluginSidebarSpec("second_one", "Second", "second", null), contributions.Sidebar[1]);
        Assert.Equal(new PluginSettingsPageSpec("page"), contributions.SettingsPage);

        var minimal = PluginManifestReader.Read(Minimal(), "com.example.a").Manifest!;
        Assert.Empty(minimal.Contributions.Sidebar);
        Assert.Null(minimal.Contributions.SettingsPage);
    }

    [Fact]
    public void Read_SidebarWithoutUi_IsRejected()
    {
        var sidebar = PluginManifestReader.Read(Minimal(extra: Contributions(
            "\"sidebar\": [ { \"id\": \"s\", \"title\": \"T\", \"provider\": \"p\" } ]", ui: false)), "com.example.a");
        Assert.False(sidebar.IsValid);
        Assert.Contains("contributions.sidebar needs a ui assembly.", sidebar.Errors);

        var page = PluginManifestReader.Read(Minimal(extra: Contributions(
            "\"settingsPage\": { \"provider\": \"p\" }", ui: false)), "com.example.a");
        Assert.False(page.IsValid);
        Assert.Contains("contributions.settingsPage needs a ui assembly.", page.Errors);
    }

    [Fact]
    public void Read_SidebarCommandMustBeDeclared()
    {
        var r = PluginManifestReader.Read(Minimal(extra: Contributions(GreetCommand +
            ", \"sidebar\": [ { \"id\": \"s\", \"title\": \"T\", \"provider\": \"p\", \"command\": \"wave\" } ]")), "com.example.a");

        Assert.False(r.IsValid);
        Assert.Contains(r.Errors, e => e.Contains("contributions.sidebar[0].command") && e.Contains("'wave'"));
    }

    [Fact]
    public void Read_DuplicateSidebarIds_AreRejected()
    {
        var r = PluginManifestReader.Read(Minimal(extra: Contributions("""
            "sidebar": [
              { "id": "s", "title": "One", "provider": "p" },
              { "id": "s", "title": "Two", "provider": "q" }
            ]
            """)), "com.example.a");

        Assert.False(r.IsValid);
        Assert.Contains(r.Errors, e => e.Contains("'s'") && e.Contains("more than once"));
    }

    [Fact]
    public void Read_SidebarIdOutsideTheKeyGrammar_IsRejected()
    {
        var r = PluginManifestReader.Read(Minimal(extra: Contributions(
            "\"sidebar\": [ { \"id\": \"Bad.Id\", \"title\": \"T\", \"provider\": \"p\" } ]")), "com.example.a");

        Assert.False(r.IsValid);
        Assert.Contains(r.Errors, e => e.Contains("contributions.sidebar[0].id") && e.Contains("'Bad.Id'"));
    }

    [Fact]
    public void Read_SidebarAndSettingsPage_RequireTheirFields()
    {
        var r = PluginManifestReader.Read(Minimal(extra: Contributions("""
            "sidebar": [ { "id": "s", "title": "", "provider": "p" }, { "id": "t", "title": "T" } ],
            "settingsPage": { }
            """)), "com.example.a");

        Assert.False(r.IsValid);
        Assert.Contains("The contributions.sidebar[0].title field must not be empty.", r.Errors);
        Assert.Contains("The contributions.sidebar[1].provider field is required.", r.Errors);
        Assert.Contains("The contributions.settingsPage.provider field is required.", r.Errors);
    }

    [Fact]
    public void Read_UnknownContributionKind_IsRejected()
    {
        var r = PluginManifestReader.Read(Minimal(extra: Contributions(
            "\"sidebars\": [ { \"id\": \"s\", \"title\": \"T\", \"provider\": \"p\" } ]")), "com.example.a");

        Assert.False(r.IsValid);
        Assert.Contains("The contributions.sidebars kind is not supported by this host.", r.Errors);
    }

    [Fact]
    public void Read_PanelsAndRowDecorations_AreAcceptedForLaterPhases()
    {
        var r = PluginManifestReader.Read(Minimal(extra: Contributions("""
            "panels": [ { "id": "p", "title": "P", "provider": "p", "placement": "right", "detachable": true } ],
            "rowDecorations": { "provider": "d" }
            """)), "com.example.a");

        Assert.True(r.IsValid, string.Join("; ", r.Errors));
    }

    /// The first-party 7-Zip manifest must parse with the host's own reader,
    /// so its errors show up here and not first in the UI smoke.
    [Fact]
    public void SevenZipManifest_InTheRepo_IsValid()
    {
        var json = System.IO.File.ReadAllText(
            System.IO.Path.Combine(FindRepoRoot(), "plugins", "sevenzip", "plugin.json"));

        var r = PluginManifestReader.Read(json, "com.sumafile.sevenzip");

        Assert.True(r.IsValid, string.Join("; ", r.Errors));
        Assert.Empty(r.Errors);
        var m = r.Manifest!;
        var format = Assert.Single(m.Contributions.ArchiveFormats);
        Assert.Equal(".7z", format.Extension);
        Assert.True(format.CanList);
        Assert.True(format.CanExtract);
        Assert.True(format.CanCreate);
        Assert.True(format.CanModify);
        Assert.Equal("7-Zip", format.EngineName);
        Assert.NotNull(m.Backend);
        Assert.True(m.Backend!.AcceptsMultipleClients);
        Assert.Equal("startup", m.Backend.ActivateOn);
        Assert.Null(m.Ui);
        var setting = Assert.Single(m.Settings);
        Assert.Equal("binaryOverride", setting.Key);
        Assert.Equal("path", setting.Type);
        Assert.NotNull(setting.Default);
        Assert.Equal(System.Text.Json.JsonValueKind.String, setting.Default!.Value.ValueKind);
        Assert.Equal("", setting.Default.Value.GetString());
    }

    /// The first-party FTP/SFTP manifest parses with the host's own reader and declares exactly the
    /// contributions and settings the remote UI and backend rely on.
    [Fact]
    public void RemoteManifest_InTheRepo_IsValid()
    {
        var r = PluginManifestReader.Read(ReadRemoteManifest(), "com.sumafile.remote");

        Assert.True(r.IsValid, string.Join("; ", r.Errors));
        Assert.Empty(r.Errors);
        var m = r.Manifest!;
        Assert.Equal("FTP/SFTP connections", m.Name);
        Assert.Equal("1.0.0", m.Version);
        Assert.Equal("SumaFile Team", m.Publisher.Name);
        Assert.Equal("SumaFile.Remote.dll", m.Ui!.Assembly);
        Assert.Equal("SumaFile.Remote.RemotePlugin", m.Ui.EntryType);
        Assert.Equal("sumafile-remote-backend.exe", m.Backend!.Executable);
        Assert.Empty(m.Backend.Args);
        Assert.Equal("first-call", m.Backend.ActivateOn);
        Assert.False(m.Backend.AcceptsMultipleClients);
        Assert.Equal(new[] { "network", "credentials", "backend-process" }, m.Permissions);

        var command = Assert.Single(m.Contributions.Commands);
        Assert.Equal("ftp-sftp-manager", command.Id);
        Assert.Equal("FTP/SFTP manager", command.Label);
        Assert.Equal("Tools", command.Group);
        var toolbar = Assert.Single(m.Contributions.Toolbar);
        Assert.Equal("ftp-sftp-manager", toolbar.Command);
        Assert.Equal("overflow", toolbar.Placement);
        Assert.Equal("", toolbar.Glyph);
        Assert.Empty(m.Contributions.ContextMenu);
        Assert.Empty(m.Contributions.ArchiveFormats);
        var section = Assert.Single(m.Contributions.Sidebar);
        Assert.Equal(new PluginSidebarSpec("connections", "Connections", "connections", "ftp-sftp-manager"), section);
        Assert.Equal(new PluginSettingsPageSpec("remote-settings"), m.Contributions.SettingsPage);

        Assert.Equal(8, m.Settings.Count);
        Assert.All(m.Settings, setting =>
        {
            Assert.Matches("^[a-z0-9][a-zA-Z0-9_-]{0,63}$", setting.Key);
            Assert.Equal("Transfers", setting.Group);
            Assert.False(string.IsNullOrWhiteSpace(setting.Label));
            Assert.NotNull(setting.Default);
        });
        Assert.Equal(m.Settings.Count, m.Settings.Select(s => s.Key).Distinct(System.StringComparer.Ordinal).Count());
        var checkMode = Assert.Single(m.Settings, s => s.Key == "checkMode");
        Assert.Equal("enum", checkMode.Type);
        Assert.Equal(new[] { "size_and_mod_time", "size_only", "checksum", "ignore_times" }, checkMode.Options);
    }

    /// The manifest's defaults are the plugin's own: each one, as a setting string, is what the remote
    /// UI's RemoteTransferSettings writes and reads by default (Task 6 pins the backend side in Rust).
    [Fact]
    public void RemoteTransferSettings_DefaultsMatchTheManifest()
    {
        var m = PluginManifestReader.Read(ReadRemoteManifest(), "com.sumafile.remote").Manifest!;

        Assert.Equal(
            SumaFile.Remote.Model.RemoteTransferSettings.Keys,
            m.Settings.Select(s => s.Key).ToArray());
        Assert.Equal(
            SumaFile.Remote.Model.RemoteTransferSettings.DefaultValues.Keys.OrderBy(k => k, System.StringComparer.Ordinal),
            m.Settings.Select(s => s.Key).OrderBy(k => k, System.StringComparer.Ordinal));
        foreach (var setting in m.Settings)
        {
            var value = setting.Default!.Value;
            var asSetting = value.ValueKind switch
            {
                System.Text.Json.JsonValueKind.Number => value.GetInt64().ToString(System.Globalization.CultureInfo.InvariantCulture),
                System.Text.Json.JsonValueKind.True => "true",
                System.Text.Json.JsonValueKind.False => "false",
                System.Text.Json.JsonValueKind.String => value.GetString(),
                _ => throw new Xunit.Sdk.XunitException($"Setting {setting.Key} has a {value.ValueKind} default."),
            };
            Assert.True(
                SumaFile.Remote.Model.RemoteTransferSettings.DefaultValues[setting.Key] == asSetting,
                $"Setting {setting.Key}: the manifest default {asSetting} differs from RemoteTransferSettings' "
                + SumaFile.Remote.Model.RemoteTransferSettings.DefaultValues[setting.Key] + ".");
        }
    }

    private static string ReadRemoteManifest() => System.IO.File.ReadAllText(
        System.IO.Path.Combine(FindRepoRoot(), "plugins", "remote", "plugin.json"));

    /// The folder above the test binaries that holds both `Cargo.toml` and
    /// `src-winui`.
    private static string FindRepoRoot()
    {
        for (var dir = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "Cargo.toml"))
                && System.IO.Directory.Exists(System.IO.Path.Combine(dir.FullName, "src-winui")))
            {
                return dir.FullName;
            }
        }

        throw new System.InvalidOperationException(
            "No folder above " + System.AppContext.BaseDirectory + " holds both Cargo.toml and src-winui.");
    }
}
