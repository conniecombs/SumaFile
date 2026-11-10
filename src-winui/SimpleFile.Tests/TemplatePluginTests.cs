using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using SimpleFile.PluginHost;
using SimpleFile.PluginHost.Backend;
using SimpleFile.PluginHost.Manifest;
using SimpleFile.PluginHost.Trust;
using SimpleFile.PluginSdk;
using Xunit;

namespace SimpleFile.Tests;

/// <summary>
/// The plugin template <c>templates/sumafile-plugin</c> (phase 5 Task 5): a copyable starter for a third-party author.
/// The test project builds its UI project (a <c>ReferenceOutputAssembly="false"</c> reference) and its
/// <c>CopyTemplatePlugin</c> target copies the build output to <c>Fixtures\template\com.example.template</c>.
/// </summary>
public sealed class TemplatePluginFactAttribute : FactAttribute
{
    public TemplatePluginFactAttribute()
    {
        if (!TemplatePluginTests.IsBuilt)
        {
            Skip = TemplatePluginTests.NotBuiltMessage;
        }
    }
}

public class TemplatePluginTests : IAsyncLifetime
{
    public const string PluginId = "com.example.template";
    public const string NotBuiltMessage =
        "The plugin template's build output is missing from Fixtures/template/com.example.template. Build "
        + "src-winui/SimpleFile.Tests (it builds templates/sumafile-plugin/ui/Example.Plugin and copies it there).";

    private const string AssemblyName = "Example.Plugin.dll";
    private const string BackendExecutable = "example-plugin-backend.exe";

    private readonly string _temp = Path.Combine(Path.GetTempPath(), "sumafile-tests", Guid.NewGuid().ToString("N"));
    private readonly List<FakePluginProcessLauncher> _launchers = [];
    private readonly List<PluginManager> _managers = [];

    private string Root => Path.Combine(_temp, "plugins");
    private string StorePath => Path.Combine(_temp, "approvals.json");

    /// <summary>The template's build output, as the test project's <c>CopyTemplatePlugin</c> target copies it.</summary>
    public static string BuiltFolder => Path.Combine(AppContext.BaseDirectory, "Fixtures", "template", PluginId);

    public static bool IsBuilt =>
        File.Exists(Path.Combine(BuiltFolder, "plugin.json")) && File.Exists(Path.Combine(BuiltFolder, AssemblyName));

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(Root);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        foreach (var manager in _managers)
        {
            await manager.DisposeAsync();
        }

        foreach (var launcher in _launchers)
        {
            await launcher.DisposeAsync();
        }

        try { Directory.Delete(_temp, true); } catch { }
    }

    [Fact]
    public async Task TemplatePlugin_ManifestIsValidAndNeedsConsent()
    {
        var manifestPath = Path.Combine(TemplateRoot(), "plugin.json");
        var read = PluginManifestReader.Read(File.ReadAllText(manifestPath), PluginId);

        Assert.True(read.IsValid, string.Join("; ", read.Errors));
        var m = read.Manifest!;
        Assert.Equal(PluginId, m.Id);
        Assert.Equal("1.0.0", m.SdkVersion);
        Assert.Null(m.Publisher.PublicKey);
        Assert.Equal(new PluginUiSpec(AssemblyName, "Example.Plugin.ExamplePlugin"), m.Ui);
        Assert.Equal(BackendExecutable, m.Backend!.Executable);
        Assert.Empty(m.Backend.Args);
        Assert.Equal("first-call", m.Backend.ActivateOn);
        Assert.Equal(["backend-process"], m.Permissions);
        var command = Assert.Single(m.Contributions.Commands);
        Assert.Equal("echo", command.Id);
        var setting = Assert.Single(m.Settings);
        Assert.Equal(("greeting", "string"), (setting.Key, setting.Type));
        Assert.Equal(JsonValueKind.String, setting.Default!.Value.ValueKind);
        Assert.Null(setting.MigrateFrom);
        Assert.Empty(m.Contributions.ContextMenu);
        Assert.Empty(m.Contributions.Toolbar);
        Assert.Empty(m.Contributions.ArchiveFormats);

        // The plugin's version is the backend crate's, which the handshake reports.
        var crate = File.ReadAllText(Path.Combine(TemplateRoot(), "backend", "Cargo.toml"));
        Assert.Matches(new Regex("^version = \"" + Regex.Escape(m.Version) + "\"$", RegexOptions.Multiline), crate);

        // Not bundled: nothing that stages the payload or writes bundled.json names the template.
        foreach (var script in new[]
                 {
                     "scripts/build-plugins.ps1", "scripts/lib/plugin-staging.ps1", "scripts/build-winui-release.ps1",
                     "packaging/winui/Product.wxs", "packaging/winui/sumafile-winui.nsi",
                 })
        {
            var text = File.ReadAllText(Path.Combine(RepoRoot(), script));
            Assert.False(text.Contains(PluginId, StringComparison.OrdinalIgnoreCase), $"{script} names {PluginId}.");
            Assert.False(text.Contains("template-plugins", StringComparison.OrdinalIgnoreCase), $"{script} names template-plugins.");
            Assert.False(text.Contains("Example.Plugin", StringComparison.OrdinalIgnoreCase), $"{script} names Example.Plugin.");
        }

        // Discovered through the real host, even in the bundled root, it waits for consent.
        var folder = Path.Combine(Root, PluginId);
        Directory.CreateDirectory(folder);
        File.Copy(manifestPath, Path.Combine(folder, "plugin.json"));
        var manager = Manager(new FakeSettings(), new RecordingDialogs(), launcher: null, bundledRoot: Root);

        await manager.DiscoverAsync(CancellationToken.None);

        var record = manager.Plugins.Single(p => p.Id == PluginId);
        Assert.Equal(PluginState.NeedsConsent, record.State);
        Assert.False(record.IsBundledId);
        Assert.Equal(SignatureStatus.Unsigned, record.Signature.Status);
        Assert.Empty(manager.EnabledPlugins);
    }

    [TemplatePluginFact]
    public async Task TemplatePlugin_LoadsThroughTheRealHostAndRunsItsCommand()
    {
        var folder = Path.Combine(Root, PluginId);
        Directory.CreateDirectory(folder);
        foreach (var file in Directory.GetFiles(BuiltFolder))
        {
            File.Copy(file, Path.Combine(folder, Path.GetFileName(file)));
        }

        new PluginApprovalStore(StorePath).Set(
            PluginId, PluginPackageHash.ComputeManifestHash(folder), SignatureStatus.Unsigned, ApprovalState.Enabled, folder);
        var settings = new FakeSettings();
        settings.Values[$"plugin.{PluginId}.greeting"] = "Hi there, ünïcode";
        var dialogs = new RecordingDialogs();
        var backend = new EchoBackend();
        var launcher = new FakePluginProcessLauncher(backend.ServeAsync, createServer: true);
        _launchers.Add(launcher);
        var manager = Manager(settings, dialogs, launcher);

        await manager.DiscoverAsync(CancellationToken.None);
        Assert.Equal(PluginState.Enabled, manager.Plugins.Single(p => p.Id == PluginId).State);
        // activateOn: first-call: nothing starts before the command calls the backend.
        Assert.Equal(0, launcher.Launches);

        var commandId = PluginManager.HostCommandId(PluginId, "echo");
        await manager.RunCommandAsync(commandId, new PluginCommandContext(commandId, null, []), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(Path.Combine(folder, BackendExecutable), launcher.LastExecutable);
        Assert.Equal([("example.echo", "Hi there, ünïcode")], backend.Calls);
        Assert.Equal([("The backend echoed \"Hi there, ünïcode\" (17 characters).", false)], dialogs.Notifications);
        var record = manager.Plugins.Single(p => p.Id == PluginId);
        Assert.Equal(PluginState.Enabled, record.State);
        Assert.Null(record.Error);
    }

    [Fact]
    public void TemplatePlugin_ProjectsReferenceOnlyTheSdk()
    {
        var template = TemplateRoot();
        var ui = Path.Combine(template, "ui");
        var projects = SourceFiles(ui, "*.csproj");
        var projectPath = Assert.Single(projects);
        Assert.Equal(Path.Combine(ui, "Example.Plugin", "Example.Plugin.csproj"), projectPath);

        var text = File.ReadAllText(projectPath);
        foreach (var forbidden in new[] { "SimpleFile.Core", "SimpleFile.Ipc", "SimpleFile.PluginHost", "SimpleFile.App" })
        {
            Assert.False(text.Contains(forbidden, StringComparison.Ordinal), $"Example.Plugin.csproj references {forbidden}.");
        }

        var project = XDocument.Parse(text);
        // WinUI-free: the plain Windows TFM and no package at all, so it restores offline from the repo's lock graph.
        Assert.Equal("net10.0-windows", project.Descendants("TargetFramework").Single().Value);
        Assert.Equal("true", project.Descendants("EnableDynamicLoading").Single().Value);
        Assert.Empty(project.Descendants("PackageReference"));
        var reference = Assert.Single(project.Descendants("ProjectReference"));
        Assert.Equal(
            "SimpleFile.PluginSdk",
            Path.GetFileNameWithoutExtension(((string?)reference.Attribute("Include") ?? "").Replace('\\', '/')));
        Assert.Equal("false", ValueOf(reference, "Private"));
        Assert.Equal("runtime", ValueOf(reference, "ExcludeAssets"));

        // Its lock file holds the SDK project and nothing else.
        using (var lockFile = JsonDocument.Parse(File.ReadAllText(Path.Combine(ui, "Example.Plugin", "packages.lock.json"))))
        {
            var entries = lockFile.RootElement.GetProperty("dependencies").EnumerateObject()
                .SelectMany(framework => framework.Value.EnumerateObject().Select(e => e.Name))
                .ToArray();
            Assert.Equal(["simplefile.pluginsdk"], entries);
        }

        // No compiled XAML (spec §5.3) and no WinUI in the template's code.
        Assert.Empty(SourceFiles(ui, "*.xaml"));
        foreach (var file in SourceFiles(ui, "*.cs"))
        {
            var source = File.ReadAllText(file);
            Assert.False(source.Contains("Microsoft.UI", StringComparison.Ordinal), $"{file} uses WinUI.");
            Assert.False(source.Contains("GetTempPath", StringComparison.Ordinal), $"{file} resolves the temp folder.");
            Assert.False(source.Contains("SpecialFolder", StringComparison.Ordinal), $"{file} resolves a user folder.");
        }

        // The backend crate: never published, on the helper crate, never on the host crates (spec §12).
        var crate = File.ReadAllText(Path.Combine(template, "backend", "Cargo.toml"));
        Assert.Contains("name = \"example-plugin-backend\"", crate, StringComparison.Ordinal);
        Assert.Contains("publish = false", crate, StringComparison.Ordinal);
        var dependencies = Regex.Match(crate, @"^\[dependencies\]\s*$(.*?)(?=^\[|\z)", RegexOptions.Multiline | RegexOptions.Singleline);
        Assert.True(dependencies.Success, "backend/Cargo.toml has no [dependencies] table.");
        Assert.Matches(new Regex("^sumafile-plugin = ", RegexOptions.Multiline), dependencies.Groups[1].Value);
        Assert.DoesNotContain("simplefile-", dependencies.Groups[1].Value, StringComparison.Ordinal);

        // The test project builds the template without referencing its assembly.
        var tests = XDocument.Load(Path.Combine(RepoRoot(), "src-winui", "SimpleFile.Tests", "SimpleFile.Tests.csproj"));
        var testReference = Assert.Single(
            tests.Descendants("ProjectReference"),
            r => ((string?)r.Attribute("Include") ?? "").EndsWith("Example.Plugin.csproj", StringComparison.Ordinal));
        Assert.Equal("false", (string?)testReference.Attribute("ReferenceOutputAssembly"));
        Assert.Equal("true", (string?)testReference.Attribute("SkipGetTargetFrameworkProperties"));
    }

    private PluginManager Manager(
        FakeSettings settings, RecordingDialogs dialogs, IPluginProcessLauncher? launcher, string? bundledRoot = null)
    {
        var pipe = "SumaFile.Plugin.Test." + Guid.NewGuid().ToString("N");
        var manager = new PluginManager(new PluginManagerOptions
        {
            Roots = [Root],
            ApprovalStorePath = StorePath,
            DataRoot = Path.Combine(_temp, "data"),
            Launcher = launcher ?? new FakePluginProcessLauncher(null, createServer: false),
            PipeNameFor = _ => pipe,
            Settings = settings,
            Progress = new RecordingSink(),
            Notifier = new RecordingNotifier(),
            DialogsFor = id => id == PluginId ? dialogs : throw new InvalidOperationException("unexpected plugin " + id),
            Workspace = null!,
            Log = (_, _) => { },
            BundledRoot = bundledRoot,
        });
        _managers.Add(manager);
        return manager;
    }

    /// <summary>
    /// The template backend's contract, served over the fake pipe: the handshake as <c>com.example.template</c>, then
    /// <c>example.echo { text } -> { text, length }</c> with <c>length</c> in Unicode scalar values.
    /// </summary>
    private sealed class EchoBackend
    {
        private readonly List<(string Method, string? Text)> _calls = [];

        public IReadOnlyList<(string Method, string? Text)> Calls
        {
            get { lock (_calls) return _calls.ToArray(); }
        }

        public async Task ServeAsync(FakeIpcServer server, CancellationToken ct)
        {
            await server.WaitForConnectionAsync(ct);
            var handshake = await server.ReadRequestAsync(ct);
            Assert.Equal("plugin.handshake", handshake.Method);
            await server.SendResultAsync(
                handshake.Id, new { pluginId = PluginId, pluginVersion = "0.1.0", sdkVersion = "1.0.0" }, ct);
            while (!ct.IsCancellationRequested)
            {
                var request = await server.ReadRequestAsync(ct);
                var text = request.Params is JsonElement { ValueKind: JsonValueKind.Object } p
                    && p.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String
                        ? t.GetString()
                        : null;
                lock (_calls) _calls.Add((request.Method, text));
                if (request.Method == "example.echo" && text is not null)
                {
                    await server.SendResultAsync(request.Id, new { text, length = text.EnumerateRunes().Count() }, ct);
                }
                else
                {
                    await server.SendErrorAsync(request.Id, -32601, "Method not found", cancellationToken: ct);
                }
            }
        }
    }

    private sealed class RecordingDialogs : IPluginDialogs
    {
        private readonly List<(string Message, bool IsError)> _notifications = [];

        public IReadOnlyList<(string Message, bool IsError)> Notifications
        {
            get { lock (_notifications) return _notifications.ToArray(); }
        }

        public void Notify(string message, bool isError)
        {
            lock (_notifications) _notifications.Add((message, isError));
        }

        public Task ShowMessageAsync(string title, string message, CancellationToken ct) =>
            throw new InvalidOperationException("The template shows no message dialog.");

        public Task<bool> ConfirmAsync(string title, string message, string confirmLabel, CancellationToken ct) =>
            throw new InvalidOperationException("The template asks no confirmation.");

        public Task<string?> PickFolderAsync(CancellationToken ct) =>
            throw new InvalidOperationException("The template picks no folder.");

        public Task<PluginDialogResult> ShowDialogAsync(object contentDialog, CancellationToken ct) =>
            throw new InvalidOperationException("The template shows no dialog.");
    }

    private static string? ValueOf(XElement element, string name) =>
        (string?)element.Attribute(name) ?? element.Element(name)?.Value;

    private static string[] SourceFiles(string root, string pattern) =>
        Directory.GetFiles(root, pattern, SearchOption.AllDirectories)
            .Where(f => !f.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj"))
            .ToArray();

    private static string TemplateRoot() => Path.Combine(RepoRoot(), "templates", "sumafile-plugin");

    private static string RepoRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Cargo.toml"))
                && Directory.Exists(Path.Combine(directory.FullName, "src-winui")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("No folder above " + AppContext.BaseDirectory + " holds Cargo.toml and src-winui.");
    }
}
