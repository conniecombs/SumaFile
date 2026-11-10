using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using SimpleFile.PluginHost.Discovery;
using SimpleFile.PluginHost.Loading;
using SimpleFile.PluginHost.Manifest;
using SimpleFile.PluginSdk;
using Xunit;

namespace SimpleFile.Tests;

[Collection(HelloFixture.Collection)]
public class PluginLoadingTests
{
    private const string HelloId = "com.sumafile.hello";
    private const string EntryType = "SimpleFile.Tests.Fixtures.HelloPlugin.HelloPlugin";

    private static DiscoveredPlugin Hello()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "Fixtures", "plugins");
        var found = PluginDiscovery.Scan([root]).Single(p => p.Id == HelloId);
        Assert.Equal(DiscoveryState.Valid, found.State);
        return found;
    }

    private static DiscoveredPlugin WithUi(DiscoveredPlugin p, PluginUiSpec ui) =>
        p with { Manifest = p.Manifest! with { Ui = ui } };

    [Fact]
    public void CreateInstance_LoadsFixtureAndReturnsIPlugin()
    {
        var instance = PluginActivator.CreateInstance(Hello());
        Assert.Equal(EntryType, instance.GetType().FullName);
    }

    [Fact]
    public void CreateInstance_SdkTypeUnifies()
    {
        var instance = PluginActivator.CreateInstance(Hello());
        Assert.True(typeof(IPlugin).IsAssignableFrom(instance.GetType()));
        object boxed = instance;
        Assert.True(boxed is IPlugin);
        Assert.True(boxed is ICommandHandler);
        // Loaded from the plugin folder, not the test bin root.
        var folder = Path.GetFullPath(Hello().Folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        Assert.StartsWith(folder, Path.GetFullPath(instance.GetType().Assembly.Location), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CreateInstance_SameFolderTwice_ReusesLoadContextAndType()
    {
        var hello = Hello();
        var a = PluginActivator.CreateInstance(hello);
        var b = PluginActivator.CreateInstance(hello);
        Assert.NotSame(a, b);
        Assert.True(ReferenceEquals(a.GetType(), b.GetType()));
        Assert.Same(
            System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(a.GetType().Assembly),
            System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(b.GetType().Assembly));
    }

    [Fact]
    public void CreateInstance_MissingAssembly_ThrowsPluginLoadException()
    {
        var p = WithUi(Hello(), new PluginUiSpec("Nope.dll", EntryType));
        Assert.Throws<PluginLoadException>(() => PluginActivator.CreateInstance(p));
    }

    [Fact]
    public void CreateInstance_MissingType_ThrowsPluginLoadException()
    {
        var hello = Hello();
        var p = WithUi(hello, new PluginUiSpec(hello.Manifest!.Ui!.Assembly, "No.Such.Type"));
        Assert.Throws<PluginLoadException>(() => PluginActivator.CreateInstance(p));
    }

    private const string HelloAssembly = "SimpleFile.Tests.Fixtures.HelloPlugin.dll";
    private const string NativeProbe = "sumafile_native_probe";

    /// <summary>
    /// A plugin folder holding a copy of the Hello assembly whose deps.json lists the native asset
    /// <c>sumafile_native_probe.dll</c>, a copy of the system's version.dll, in the folder root.
    /// </summary>
    private static string NativePluginFolder(string tmp)
    {
        var folder = Path.Combine(tmp, "plugin");
        Directory.CreateDirectory(folder);
        File.Copy(Path.Combine(HelloFixture.Folder, HelloAssembly), Path.Combine(folder, HelloAssembly));

        const string depsName = "SimpleFile.Tests.Fixtures.HelloPlugin.deps.json";
        var deps = JsonNode.Parse(File.ReadAllText(Path.Combine(HelloFixture.Folder, depsName)))!;
        var target = deps["targets"]!.AsObject().Single().Value!.AsObject();
        var main = target
            .Single(library => library.Key.StartsWith("SimpleFile.Tests.Fixtures.HelloPlugin/", StringComparison.Ordinal))
            .Value!.AsObject();
        main["native"] = new JsonObject { [NativeProbe + ".dll"] = new JsonObject() };
        File.WriteAllText(Path.Combine(folder, depsName), deps.ToJsonString());

        File.Copy(Path.Combine(Environment.SystemDirectory, "version.dll"), Path.Combine(folder, NativeProbe + ".dll"));
        return folder;
    }

    private static string NewTemp() =>
        Path.Combine(Path.GetTempPath(), "sumafile-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void LoadContext_LoadsANativeDllListedInThePluginDepsJson()
    {
        var tmp = NewTemp();
        try
        {
            var folder = NativePluginFolder(tmp);
            var host = Path.Combine(tmp, "host");
            Directory.CreateDirectory(host);
            var context = new PluginLoadContext(folder, Path.Combine(folder, HelloAssembly), host);

            var resolved = context.ResolveUnmanagedPath(NativeProbe);

            Assert.NotNull(resolved);
            Assert.Equal(
                Path.GetFullPath(Path.Combine(folder, NativeProbe + ".dll")),
                Path.GetFullPath(resolved),
                StringComparer.OrdinalIgnoreCase);
            var handle = context.LoadUnmanagedForTest(NativeProbe);
            Assert.NotEqual(IntPtr.Zero, handle);
            NativeLibrary.Free(handle);
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public void LoadContext_LeavesANativeDllTheHostShipsToTheHost()
    {
        var tmp = NewTemp();
        try
        {
            var folder = NativePluginFolder(tmp);
            var host = Path.Combine(tmp, "host");
            Directory.CreateDirectory(host);
            File.Copy(Path.Combine(folder, NativeProbe + ".dll"), Path.Combine(host, NativeProbe + ".dll"));
            var context = new PluginLoadContext(folder, Path.Combine(folder, HelloAssembly), host);

            Assert.Null(context.ResolveUnmanagedPath(NativeProbe));
            Assert.Null(context.ResolveUnmanagedPath(NativeProbe + ".dll"));
            Assert.Equal(IntPtr.Zero, context.LoadUnmanagedForTest(NativeProbe));
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    [Theory]
    [InlineData("Microsoft.WinUI", true)]
    [InlineData("Microsoft.InteractiveExperiences.Projection", true)]
    [InlineData("Microsoft.Web.WebView2.Core", true)]
    [InlineData("Microsoft.Windows.SDK.NET", true)]
    [InlineData("WinRT.Runtime", true)]
    [InlineData("SumaFile.Remote.Model", false)]
    [InlineData("Newtonsoft.Json", false)]
    public void LoadContext_SharesWinUiAndWindowsAppSdkProjections(string name, bool shared) =>
        Assert.Equal(shared, PluginLoadContext.IsHostShared(name));

    /// Parked #17/#22: the host ships the Windows App SDK's Microsoft.Graphics.Display and Microsoft.Graphics.Imaging
    /// families, never Win2D (Microsoft.Graphics.Canvas*), so a plugin that bundles Win2D loads its own copy.
    [Theory]
    [InlineData("Microsoft.Graphics.Canvas", false)]
    [InlineData("Microsoft.Graphics.Canvas.Interop", false)]
    [InlineData("Microsoft.Graphics.Display", true)]
    [InlineData("Microsoft.Graphics.Imaging", true)]
    [InlineData("Microsoft.Graphics.Imaging.Projection", true)]
    public void SharedPrefixes_DoNotCaptureWin2D(string name, bool shared) =>
        Assert.Equal(shared, PluginLoadContext.IsHostShared(name));

    private sealed class FakePluginHost : IPluginHost
    {
        public string PluginId => HelloId;
        public Task<T> InvokeBackendAsync<T>(string method, object? args, CancellationToken ct) =>
            throw new NotSupportedException();
        public IDisposable OnBackendEvent<T>(string eventName, Action<T> handler) => throw new NotSupportedException();
        public IPluginSettings Settings => null!;
        public IPluginProgress Progress => null!;
        public IPluginDialogs Dialogs => null!;
        public IPluginWorkspace Workspace => null!;
        public IPluginLogger Logger => null!;
        public IPluginWindows Windows => null!;
        public IPluginPanels Panels => null!;
        public IPluginDecorations Decorations => null!;
    }

    [Fact]
    public void HelloEntry_ImplementsTheSidebarProviderAndLoadsWithoutWinUi()
    {
        // The test host has no WinUI: the entry type must load and construct without touching it. Its WinUI code
        // lives in HelloUi, which only the provider methods reach.
        var instance = PluginActivator.CreateInstance(Hello());
        Assert.IsAssignableFrom<ISidebarSectionProvider>(instance);
        Assert.IsAssignableFrom<ISettingsPageProvider>(instance);
        var entry = instance.GetType();
        Assert.Equal(typeof(object), entry.BaseType);
        const System.Reflection.BindingFlags all = System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.Static
            | System.Reflection.BindingFlags.Public
            | System.Reflection.BindingFlags.NonPublic;
        var memberTypes = entry.GetFields(all).Select(field => field.FieldType)
            .Concat(entry.GetProperties(all).Select(property => property.PropertyType));
        Assert.DoesNotContain(memberTypes, type => type.Namespace?.StartsWith("Microsoft.UI", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(AppDomain.CurrentDomain.GetAssemblies(), assembly => assembly.GetName().Name == "Microsoft.WinUI");
    }

    [Fact]
    public void HelloManifest_DeclaresTheSettingsPageAndTheWindowAndDialogCommands()
    {
        var manifest = Hello().Manifest!;
        Assert.Equal("hello-page", manifest.Contributions.SettingsPage?.Provider);
        var commands = manifest.Contributions.Commands.ToDictionary(command => command.Id, command => command.Label);
        Assert.Equal("Hello: open window", commands["open-window"]);
        Assert.Equal("Hello: show dialog", commands["show-dialog"]);
    }

    [Fact]
    public async Task Fixture_ExecuteGreet_RecordsCommand()
    {
        using var markers = new HelloFixture.MarkerScope();
        var instance = PluginActivator.CreateInstance(Hello());
        var handler = Assert.IsAssignableFrom<ICommandHandler>(instance);
        await handler.ExecuteAsync(new PluginCommandContext("greet", null, []), CancellationToken.None);
        Assert.Contains("greet", File.ReadAllLines(markers.CommandsLog));
    }
}
