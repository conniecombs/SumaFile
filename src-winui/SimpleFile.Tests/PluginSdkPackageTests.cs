using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using Xunit;

namespace SimpleFile.Tests;

/// <summary>
/// Plan phase 5, Task 2 (#70/#75): the SDK packs as <c>SumaFile.PluginSdk</c> with its XML documentation and README, and
/// every public type and member of the pinned surface is documented.
/// </summary>
public class PluginSdkPackageTests
{
    private static readonly Assembly Sdk = typeof(SimpleFile.PluginSdk.PluginSdk).Assembly;

    [Fact]
    public void SdkProject_DeclaresThePackageMetadata()
    {
        var root = FindRepoRoot();
        var projectPath = Path.Combine(root, "src-winui", "SimpleFile.PluginSdk", "SimpleFile.PluginSdk.csproj");
        var project = XDocument.Load(projectPath);
        string? Property(string name) => project.Descendants(name).SingleOrDefault()?.Value.Trim();

        Assert.Equal("SumaFile.PluginSdk", Property("PackageId"));
        Assert.Equal("true", Property("IsPackable"));
        Assert.Equal("true", Property("GenerateDocumentationFile"));
        Assert.Equal("MIT", Property("PackageLicenseExpression"));
        Assert.Equal("https://github.com/conniecombs/SumaFile", Property("RepositoryUrl"));
        Assert.Equal("git", Property("RepositoryType"));
        Assert.Equal("README.md", Property("PackageReadmeFile"));
        Assert.Equal("SumaFile Team", Property("Authors"));
        Assert.Contains("SumaFile", Property("Description"));
        // The package version is the SDK contract version, not the app's version (Directory.Build.props).
        Assert.Equal(SimpleFile.PluginSdk.PluginSdk.Version, Property("PackageVersion"));
        // Only while packing: lock files name a project reference by its package id and version, so an unconditional
        // id or version would rewrite (and in CI's locked mode, fail) every referencing project's lock file.
        foreach (var name in new[] { "PackageId", "PackageVersion" })
        {
            Assert.Equal("'$(_IsPacking)' == 'true'", (string?)project.Descendants(name).Single().Attribute("Condition"));
        }

        // The README is packed at the package root, where PackageReadmeFile points.
        var readmeItem = Assert.Single(project.Descendants("None"), item => (string?)item.Attribute("Include") == "README.md");
        Assert.Equal("true", (string?)readmeItem.Attribute("Pack"));
        Assert.Equal("\\", (string?)readmeItem.Attribute("PackagePath"));

        var readme = File.ReadAllText(Path.Combine(root, "src-winui", "SimpleFile.PluginSdk", "README.md"));
        Assert.StartsWith("# SumaFile.PluginSdk", readme, StringComparison.Ordinal);
        Assert.Contains("docs/PLUGINS.md", readme, StringComparison.Ordinal);
        Assert.Contains("ExcludeAssets=\"runtime\"", readme, StringComparison.Ordinal);

        // npm run pack:sdk packs into target/packages and nowhere else; nothing is pushed.
        var packageJson = File.ReadAllText(Path.Combine(root, "package.json"));
        Assert.Contains("\"pack:sdk\": \"dotnet pack src-winui/SimpleFile.PluginSdk -c Release -o target/packages\"", packageJson, StringComparison.Ordinal);
        Assert.DoesNotContain("nuget push", packageJson, StringComparison.Ordinal);
    }

    [Fact]
    public void SdkAssemblyVersion_IsTheContractVersion()
    {
        // A plugin's reference names this version; the host's default load context binds it only when the host's copy
        // is at least as new, so the identity must be the contract version, not the app's.
        var contract = Version.Parse(SimpleFile.PluginSdk.PluginSdk.Version);
        var assembly = Sdk.GetName().Version;
        Assert.Equal(new Version(contract.Major, contract.Minor, 0, 0), assembly);
    }

    [Fact]
    public void SdkDocs_EveryPublicMemberIsDocumented()
    {
        var members = DocumentedMembers();
        var missing = new List<string>();
        foreach (var id in PublicMemberIds())
        {
            if (!members.TryGetValue(id, out var member) || string.IsNullOrWhiteSpace(Summary(member)))
            {
                missing.Add(id);
            }
        }

        Assert.True(missing.Count == 0, "Undocumented SDK members:\n" + string.Join("\n", missing));
    }

    [Theory]
    // The threading rules.
    [InlineData("T:SimpleFile.PluginSdk.IPlugin", "UI thread")]
    [InlineData("T:SimpleFile.PluginSdk.IPlugin", "thread-pool thread")]
    [InlineData("M:SimpleFile.PluginSdk.IPluginHost.OnBackendEvent``1(System.String,System.Action{``0})", "not on the UI thread")]
    // The FolderFlags grammar.
    [InlineData("P:SimpleFile.PluginSdk.PluginFolderDecorations.FolderFlags", "^[A-Za-z][A-Za-z0-9_-]{0,63}$")]
    [InlineData("P:SimpleFile.PluginSdk.PluginFolderDecorations.FolderFlags", "dropped")]
    // The window-option ranges.
    [InlineData("T:SimpleFile.PluginSdk.PluginWindowOptions", "320")]
    [InlineData("T:SimpleFile.PluginSdk.PluginWindowOptions", "3840")]
    [InlineData("T:SimpleFile.PluginSdk.PluginWindowOptions", "240")]
    [InlineData("T:SimpleFile.PluginSdk.PluginWindowOptions", "2160")]
    [InlineData("T:SimpleFile.PluginSdk.PluginWindowOptions", "120 characters")]
    [InlineData("M:SimpleFile.PluginSdk.IPluginWindows.Attach(System.Object,SimpleFile.PluginSdk.PluginWindowOptions)", "InvalidOperationException")]
    // The object hand-off rule.
    [InlineData("T:SimpleFile.PluginSdk.PluginSdk", "object")]
    [InlineData("M:SimpleFile.PluginSdk.ISidebarSectionProvider.CreateSidebarSection(SimpleFile.PluginSdk.PluginSidebarSectionRequest)", "Microsoft.UI.Xaml.FrameworkElement")]
    // The deadlines.
    [InlineData("M:SimpleFile.PluginSdk.IPlugin.DeactivateAsync", "5 s")]
    [InlineData("M:SimpleFile.PluginSdk.IRowDecorationProvider.GetDecorationsAsync(SimpleFile.PluginSdk.PluginDecorationRequest,System.Threading.CancellationToken)", "250 ms")]
    [InlineData("M:SimpleFile.PluginSdk.IPluginHost.InvokeBackendAsync``1(System.String,System.Object,System.Threading.CancellationToken)", "5 s")]
    [InlineData("P:SimpleFile.PluginSdk.IPluginOperation.CancellationToken", "30 s")]
    public void SdkDocs_StateTheContractsAuthorsDependOn(string member, string text)
    {
        var members = DocumentedMembers();
        Assert.True(members.TryGetValue(member, out var element), $"{member} is not in the SDK's XML documentation.");
        var docs = string.Join(" ", element!.DescendantNodes().OfType<XText>().Select(node => node.Value))
            + " " + string.Join(" ", element.Descendants().SelectMany(e => e.Attributes()).Select(a => a.Value));
        docs = string.Join(" ", docs.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        Assert.Contains(text, docs, StringComparison.Ordinal);
    }

    /// <summary>The <c>member</c> elements of the XML file the SDK build writes next to its assembly, by id.</summary>
    private static Dictionary<string, XElement> DocumentedMembers()
    {
        var path = Path.ChangeExtension(Sdk.Location, ".xml");
        Assert.True(File.Exists(path), $"The SDK's XML documentation is missing: {path}");
        return XDocument.Load(path).Descendants("member")
            .ToDictionary(member => (string)member.Attribute("name")!, StringComparer.Ordinal);
    }

    private static string Summary(XElement member) =>
        string.Concat(member.Element("summary")?.DescendantNodes().OfType<XText>().Select(text => text.Value) ?? []);

    /// <summary>
    /// The XML documentation ids of the members <see cref="PluginSdkSurface"/> pins, less the ones a compiler writes
    /// (property and event accessors, an enum's value field, the members a record synthesizes), which C# never asks to
    /// document.
    /// </summary>
    private static IEnumerable<string> PublicMemberIds()
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        string[] synthesized = ["Equals", "GetHashCode", "ToString", "Deconstruct", "<Clone>$", "PrintMembers", "EqualityContract"];
        foreach (var type in Sdk.GetExportedTypes())
        {
            yield return "T:" + type.FullName;
            var isRecord = type.GetMethod("<Clone>$") is not null;
            foreach (var member in type.GetMembers(flags))
            {
                switch (member)
                {
                    case MethodInfo method when method.IsSpecialName:
                    case FieldInfo { IsSpecialName: true }:
                        continue;
                    case MethodInfo or PropertyInfo when isRecord && synthesized.Contains(member.Name):
                        continue;
                    case ConstructorInfo ctor when isRecord && ctor.GetParameters() is [var only] && only.ParameterType == type:
                        continue;
                }

                yield return Id(type, member);
            }
        }
    }

    private static string Id(Type type, MemberInfo member) => member switch
    {
        ConstructorInfo ctor => $"M:{type.FullName}.#ctor{Parameters(ctor.GetParameters())}",
        MethodInfo method => $"M:{type.FullName}.{method.Name}{(method.IsGenericMethod ? "``" + method.GetGenericArguments().Length : "")}{Parameters(method.GetParameters())}",
        PropertyInfo property => $"P:{type.FullName}.{property.Name}",
        FieldInfo field => $"F:{type.FullName}.{field.Name}",
        EventInfo evt => $"E:{type.FullName}.{evt.Name}",
        _ => throw new InvalidOperationException($"Unexpected SDK member {member}."),
    };

    private static string Parameters(ParameterInfo[] parameters) =>
        parameters.Length == 0 ? "" : "(" + string.Join(",", parameters.Select(p => TypeId(p.ParameterType))) + ")";

    private static string TypeId(Type type)
    {
        if (type.IsGenericMethodParameter) return "``" + type.GenericParameterPosition;
        if (type.IsGenericTypeParameter) return "`" + type.GenericParameterPosition;
        if (type.IsByRef) return TypeId(type.GetElementType()!) + "@";
        if (type.IsArray) return TypeId(type.GetElementType()!) + "[]";
        if (type.IsGenericType)
        {
            var definition = type.GetGenericTypeDefinition().FullName!;
            var name = definition[..definition.IndexOf('`', StringComparison.Ordinal)];
            return name + "{" + string.Join(",", type.GetGenericArguments().Select(TypeId)) + "}";
        }

        return type.FullName!.Replace('+', '.');
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
