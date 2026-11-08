using System;
using System.IO;
using System.Linq;
using System.Text.Json;
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
