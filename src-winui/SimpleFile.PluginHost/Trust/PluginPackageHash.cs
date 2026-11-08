using System.Security.Cryptography;

namespace SimpleFile.PluginHost.Trust;

public static class PluginPackageHash
{
    /// <summary>Lowercase hex SHA-256 of the plugin.json bytes in <paramref name="folder"/>.</summary>
    public static string ComputeManifestHash(string folder) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(folder, "plugin.json"))))
            .ToLowerInvariant();
}
