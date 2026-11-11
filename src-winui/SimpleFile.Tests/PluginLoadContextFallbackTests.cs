using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Loader;
using System.Text.Json.Nodes;
using SimpleFile.PluginHost.Loading;
using Xunit;

namespace SimpleFile.Tests;

/// <summary>
/// Final fix wave, item 6: <c>System.</c> and <c>Microsoft.Extensions.</c> are host-first, not host-only. A plugin gets
/// the host's copy of such an assembly when the default context can provide it, and its own bundled copy otherwise
/// (System.Reactive, a Microsoft.Extensions package the host does not ship). The WinUI, Windows App SDK, Windows SDK,
/// WinRT, SDK and Mvvm families stay host-only: a plugin never loads its own copy of those. <c>Microsoft.Windows.</c>
/// keeps its dot, so <c>Microsoft.WindowsAzure.*</c> is the plugin's own.
/// </summary>
[Collection(HelloFixture.Collection)]
public sealed class PluginLoadContextFallbackTests : IDisposable
{
    private const string HelloAssembly = "SimpleFile.Tests.Fixtures.HelloPlugin.dll";
    private const string HelloDeps = "SimpleFile.Tests.Fixtures.HelloPlugin.deps.json";
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "sumafile-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_temp, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// A copy of the Hello plugin whose deps.json also lists one bundled assembly per name, each a real (empty)
    /// assembly written next to it.
    /// </summary>
    private string PluginWith(params string[] bundled)
    {
        var folder = Path.Combine(_temp, "plugin");
        Directory.CreateDirectory(folder);
        File.Copy(Path.Combine(HelloFixture.Folder, HelloAssembly), Path.Combine(folder, HelloAssembly));
        var deps = JsonNode.Parse(File.ReadAllText(Path.Combine(HelloFixture.Folder, HelloDeps)))!;
        var target = deps["targets"]!.AsObject().Single().Value!.AsObject();
        var libraries = deps["libraries"]!.AsObject();
        foreach (var name in bundled)
        {
            WriteAssembly(name, Path.Combine(folder, name + ".dll"));
            target[name + "/99.0.0"] = new JsonObject { ["runtime"] = new JsonObject { [name + ".dll"] = new JsonObject() } };
            libraries[name + "/99.0.0"] = new JsonObject { ["type"] = "project", ["serviceable"] = false, ["sha512"] = "" };
        }

        File.WriteAllText(Path.Combine(folder, HelloDeps), deps.ToJsonString());
        return folder;
    }

    private static void WriteAssembly(string name, string path)
    {
        var builder = new PersistedAssemblyBuilder(new AssemblyName(name) { Version = new Version(99, 0, 0, 0) }, typeof(object).Assembly);
        var module = builder.DefineDynamicModule(name);
        module.DefineType(name + ".Probe", TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Sealed).CreateType();
        builder.Save(path);
    }

    private static PluginLoadContext Context(string folder) => new(folder, Path.Combine(folder, HelloAssembly));

    [Theory]
    [InlineData("System.SumaFileProbe")]
    [InlineData("System.Reactive.SumaFileProbe")]
    [InlineData("Microsoft.Extensions.SumaFileProbe")]
    [InlineData("Microsoft.WindowsAzure.SumaFileProbe")]
    public void AHostFirstNameTheHostDoesNotShip_LoadsThePluginsOwnCopy(string name)
    {
        var folder = PluginWith(name);
        var context = Context(folder);

        var assembly = context.LoadFromAssemblyName(new AssemblyName(name));

        Assert.Same(context, AssemblyLoadContext.GetLoadContext(assembly));
        Assert.Equal(Path.GetFullPath(Path.Combine(folder, name + ".dll")), Path.GetFullPath(assembly.Location), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void AHostFirstNameTheHostShips_IsTheHostsCopyEvenWhenThePluginBundlesOne()
    {
        // Types such as JsonElement must unify with the host's: the plugin's System.Text.Json is never used.
        var context = Context(PluginWith("System.Text.Json"));

        var assembly = context.LoadFromAssemblyName(new AssemblyName("System.Text.Json"));

        Assert.Same(typeof(System.Text.Json.JsonSerializer).Assembly, assembly);
        Assert.Same(AssemblyLoadContext.Default, AssemblyLoadContext.GetLoadContext(assembly));
    }

    [Theory]
    [InlineData("WinRT.SumaFileProbe")]
    [InlineData("CommunityToolkit.Mvvm.SumaFileProbe")]
    [InlineData("SimpleFile.PluginSdk.SumaFileProbe")]
    [InlineData("Microsoft.UI.Xaml.SumaFileProbe")]
    [InlineData("Microsoft.Windows.SumaFileProbe")]
    [InlineData("Microsoft.WindowsAppSDK.SumaFileProbe")]
    public void AHostOnlyName_NeverLoadsThePluginsCopy(string name)
    {
        var context = Context(PluginWith(name));

        Assert.Throws<FileNotFoundException>(() => context.LoadFromAssemblyName(new AssemblyName(name)));
    }

    [Theory]
    [InlineData("System.Text.Json", "HostFirst")]
    [InlineData("System.Reactive", "HostFirst")]
    [InlineData("Microsoft.Extensions.Logging", "HostFirst")]
    [InlineData("Microsoft.Windows.SDK.NET", "HostOnly")]
    [InlineData("Microsoft.Windows.AppLifecycle.Projection", "HostOnly")]
    [InlineData("Microsoft.WindowsAppRuntime.Bootstrap.Net", "HostOnly")]
    [InlineData("Microsoft.WindowsAzure.Storage", "None")]
    [InlineData("WinRT.Runtime", "HostOnly")]
    [InlineData("CommunityToolkit.Mvvm", "HostOnly")]
    [InlineData("SimpleFile.PluginSdk", "HostOnly")]
    [InlineData("Newtonsoft.Json", "None")]
    public void Classify_SortsNamesIntoHostOnlyHostFirstAndThePlugins(string name, string kind) =>
        Assert.Equal(kind, PluginLoadContext.Classify(name).ToString());

    [Fact]
    public void HostProvides_ReadsTheTrustedPlatformAssemblies()
    {
        Assert.True(PluginLoadContext.HostProvides("System.Text.Json"));
        Assert.True(PluginLoadContext.HostProvides("System.Private.CoreLib"));
        Assert.True(PluginLoadContext.HostProvides("system.text.json"));
        Assert.False(PluginLoadContext.HostProvides("System.SumaFileProbe"));
    }
}
