using SimpleFile.PluginHost.Discovery;
using SimpleFile.PluginHost.Trust;

namespace SimpleFile.PluginHost;

public enum PluginState { NeedsConsent, Enabled, Disabled, IncompatibleSdk, InvalidManifest, LoadFailed, Faulted, Shadowed }

/// <summary>One discovered plugin folder and its current state. Only <see cref="PluginManager"/> changes it.</summary>
public sealed class PluginRecord
{
    internal PluginRecord(DiscoveredPlugin discovered, PluginState state, SignatureState signature, string? error, string? manifestHash)
    {
        Discovered = discovered;
        State = state;
        Signature = signature;
        Error = error;
        ManifestHash = manifestHash;
    }

    public string Id => Discovered.Id;

    public DiscoveredPlugin Discovered { get; internal set; }

    public PluginState State { get; internal set; }

    public SignatureState Signature { get; internal set; }

    public string? Error { get; internal set; }

    public bool BackendRunning { get; internal set; }

    /// <summary>Hash of plugin.json at discovery; null when the manifest could not be read.</summary>
    internal string? ManifestHash { get; set; }

    /// <summary>Activated instance, services and backend; null for records that can never run.</summary>
    internal PluginRuntime? Runtime { get; set; }
}
