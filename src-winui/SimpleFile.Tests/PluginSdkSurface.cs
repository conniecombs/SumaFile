using System.Reflection;

namespace SimpleFile.Tests;

internal static class PluginSdkSurface
{
    private const BindingFlags Flags =
        BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    public static string[] Describe(Assembly assembly)
    {
        var lines = new List<string>();
        foreach (var type in assembly.GetExportedTypes())
        {
            lines.Add($"{type.FullName}: <type>");
            foreach (var member in type.GetMembers(Flags))
            {
                lines.Add($"{type.FullName}: {member}");
            }
        }
        lines.Sort(StringComparer.Ordinal);
        return lines.ToArray();
    }
}
