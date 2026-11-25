using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using SimpleFile.Ipc;
using SimpleFile.PluginHost.Backend;
using SimpleFile.Tests.Git;
using Xunit;

namespace SimpleFile.Tests;

/// <summary>
/// The launch token end to end: the real launcher and a bundled backend, built with the helper crate, that reads the
/// token from its standard input.
/// </summary>
[Collection(PluginEnvironmentCollection.Name)]
public class PluginLaunchTokenTests
{
    private const string Token = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public async Task ABundledBackend_AnswersOnlyAHandshakeThatCarriesItsLaunchToken()
    {
        Assert.True(GitBackendHarness.IsStaged, GitBackendHarness.NotStagedMessage);
        var pipeName = "SumaFile.Plugin.Test." + Guid.NewGuid().ToString("N");
        var dataDir = Directory.CreateTempSubdirectory("sumafile-launch-token-").FullName;
        try
        {
            using var process = new ProcessLauncher().Launch(
                GitBackendHarness.BackendExecutable,
                ["--pipe-name", pipeName, "--data-dir", dataDir],
                GitBackendHarness.PluginFolder,
                new Dictionary<string, string>(),
                _ => { },
                Token);
            await using var client = await NamedPipeJsonClient.ConnectAsync(pipeName, TimeSpan.FromSeconds(10));

            var missing = await Assert.ThrowsAsync<IpcException>(
                () => client.InvokeAsync("plugin.handshake", Handshake(null)));
            Assert.Equal(-32600, missing.Code);
            Assert.Contains("authToken is required", missing.Message);

            var wrong = await Assert.ThrowsAsync<IpcException>(
                () => client.InvokeAsync("plugin.handshake", Handshake(Token[..^1] + "0")));
            Assert.Equal(-32600, wrong.Code);
            Assert.Contains("authToken does not match", wrong.Message);

            // The same connection, once it presents the token.
            await client.InvokeAsync("plugin.handshake", Handshake(Token));
        }
        finally
        {
            try { Directory.Delete(dataDir, recursive: true); } catch { }
        }
    }

    /// <summary>A backend started without a token, as by an older host or a script, answers as it always did.</summary>
    [Fact]
    public async Task ABundledBackend_StartedWithoutAToken_AnswersAnyHandshake()
    {
        Assert.True(GitBackendHarness.IsStaged, GitBackendHarness.NotStagedMessage);
        var pipeName = "SumaFile.Plugin.Test." + Guid.NewGuid().ToString("N");
        var dataDir = Directory.CreateTempSubdirectory("sumafile-launch-token-").FullName;
        try
        {
            using var process = new ProcessLauncher().Launch(
                GitBackendHarness.BackendExecutable,
                ["--pipe-name", pipeName, "--data-dir", dataDir],
                GitBackendHarness.PluginFolder,
                new Dictionary<string, string>(),
                _ => { });
            await using var client = await NamedPipeJsonClient.ConnectAsync(pipeName, TimeSpan.FromSeconds(10));

            await client.InvokeAsync("plugin.handshake", Handshake(null));
        }
        finally
        {
            try { Directory.Delete(dataDir, recursive: true); } catch { }
        }
    }

    private static object Handshake(string? authToken) => authToken is null
        ? new { pluginId = GitBackendHarness.PluginId, hostVersion = "1.0.0.0", sdkVersion = "1.0.0" }
        : new { pluginId = GitBackendHarness.PluginId, hostVersion = "1.0.0.0", sdkVersion = "1.0.0", authToken };
}
