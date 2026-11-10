using SimpleFile.PluginHost.Manifest;

namespace SimpleFile.PluginHost.Discovery;

public enum DiscoveryState { Valid, InvalidManifest, IncompatibleSdk, Shadowed }

/// <param name="ManifestHash">
/// Lowercase hex SHA-256 of the plugin.json bytes discovery parsed; null when the manifest could not be read
/// (or the record was built by hand), in which case the manager hashes the file itself.
/// </param>
/// <param name="ManifestBytes">
/// The plugin.json bytes discovery parsed and hashed, which the signature check verifies, so a scan reads each
/// manifest once; null as for <paramref name="ManifestHash"/>, in which case the verifier reads the file.
/// </param>
public sealed record DiscoveredPlugin(
    string Id,
    string Folder,
    string Root,
    DiscoveryState State,
    PluginManifest? Manifest,
    IReadOnlyList<string> Errors,
    string? ShadowedByFolder,
    string? ManifestHash = null,
    byte[]? ManifestBytes = null);
