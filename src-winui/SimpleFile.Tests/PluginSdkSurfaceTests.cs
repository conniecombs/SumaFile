using System.Reflection;
using SimpleFile.PluginSdk;
using Xunit;

namespace SimpleFile.Tests;

public class PluginSdkSurfaceTests
{
    [Fact]
    public void PublicSurface_MatchesPinnedSnapshot()
    {
        var actual = PluginSdkSurface.Describe(typeof(SimpleFile.PluginSdk.PluginSdk).Assembly);
        var expected = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "PluginSdkSurface.v1.txt"));
        Assert.Equal(expected, actual);
    }

    /// Spec §5.1 (user ruling 2026-11-09): the SDK stays WinUI-free and hands UI objects across as object, and the
    /// phase 4 contracts follow the same pattern.
    [Fact]
    public void SdkContracts_UiHandOffsStayObjectTyped()
    {
        var createPanel = typeof(IPanelProvider).GetMethod(nameof(IPanelProvider.CreatePanel));
        Assert.NotNull(createPanel);
        Assert.Equal(typeof(object), createPanel!.ReturnType);
        Assert.Equal([typeof(PluginPanelRequest)], createPanel.GetParameters().Select(p => p.ParameterType));

        var assembly = typeof(SimpleFile.PluginSdk.PluginSdk).Assembly;
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var offenders = new List<string>();
        foreach (var type in assembly.GetExportedTypes())
        {
            foreach (var member in type.GetMembers(flags))
            {
                foreach (var used in TypesOf(member))
                {
                    if (IsWinUi(used))
                    {
                        offenders.Add($"{type.FullName}.{member.Name}: {used.FullName}");
                    }
                }
            }
        }

        Assert.Empty(offenders);
        Assert.DoesNotContain(
            PluginSdkSurface.Describe(assembly),
            line => line.Contains("Microsoft.UI", StringComparison.Ordinal));
        Assert.DoesNotContain(
            assembly.GetReferencedAssemblies(),
            reference => reference.Name is { } name
                && (name.StartsWith("Microsoft.WindowsAppSDK", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("Microsoft.WinUI", StringComparison.OrdinalIgnoreCase)));
    }

    private static IEnumerable<Type> TypesOf(MemberInfo member) => member switch
    {
        MethodInfo method => method.GetParameters().Select(p => p.ParameterType).Append(method.ReturnType).SelectMany(Expand),
        ConstructorInfo ctor => ctor.GetParameters().Select(p => p.ParameterType).SelectMany(Expand),
        PropertyInfo property => Expand(property.PropertyType),
        FieldInfo field => Expand(field.FieldType),
        EventInfo evt when evt.EventHandlerType is { } handler => Expand(handler),
        _ => [],
    };

    private static IEnumerable<Type> Expand(Type type)
    {
        var element = type.HasElementType ? type.GetElementType()! : type;
        yield return element;
        if (element.IsGenericType)
        {
            foreach (var argument in element.GetGenericArguments().SelectMany(Expand))
            {
                yield return argument;
            }
        }
    }

    private static bool IsWinUi(Type type) =>
        type.FullName is { } name
        && (name.StartsWith("Microsoft.UI.", StringComparison.Ordinal)
            || name.StartsWith("Windows.UI.", StringComparison.Ordinal));
}
