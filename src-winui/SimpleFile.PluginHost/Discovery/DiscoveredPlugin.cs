using SimpleFile.PluginHost.Manifest;

namespace SimpleFile.PluginHost.Discovery;

public enum DiscoveryState { Valid, InvalidManifest, IncompatibleSdk, Shadowed }

public sealed record DiscoveredPlugin(
    string Id,
    string Folder,
    string Root,
    DiscoveryState State,
    PluginManifest? Manifest,
    IReadOnlyList<string> Errors,
    string? ShadowedByFolder);
