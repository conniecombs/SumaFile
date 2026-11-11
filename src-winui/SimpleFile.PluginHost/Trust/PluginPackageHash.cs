using System.Security.Cryptography;

namespace SimpleFile.PluginHost.Trust;

public static class PluginPackageHash
{
    /// <summary>Lowercase hex SHA-256 of the plugin.json bytes in <paramref name="folder"/>.</summary>
    public static string ComputeManifestHash(string folder) =>
        ComputeHash(File.ReadAllBytes(Path.Combine(folder, "plugin.json")));

    /// <summary>Lowercase hex SHA-256 of <paramref name="bytes"/>, as <see cref="ComputeManifestHash"/> records it.</summary>
    internal static string ComputeHash(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
