using System;
using System.IO;
using System.Linq;
using System.Text;
using SimpleFile.PluginHost.Discovery;
using SimpleFile.PluginHost.Manifest;
using SimpleFile.PluginHost.Trust;
using Xunit;

namespace SimpleFile.Tests;

public class PluginDiscoveryTests
{
    private static string Manifest(string id, string sdk = "1.0.0") =>
        "{ \"id\": \"" + id + "\", \"name\": \"N\", \"version\": \"1.0.0\", \"sdkVersion\": \"" + sdk + "\", " +
        "\"publisher\": { \"name\": \"P\" }, \"contributions\": {} }";

    private static string NewTemp() =>
        Path.Combine(Path.GetTempPath(), "sumafile-tests", Guid.NewGuid().ToString("N"));

    private static string Plugin(string root, string id, string? json = null)
    {
        var dir = Path.Combine(root, id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "plugin.json"), json ?? Manifest(id));
        return dir;
    }

    [Fact]
    public void Scan_FindsValidPluginsAcrossRoots()
    {
        var tmp = NewTemp();
        try
        {
            var r1 = Path.Combine(tmp, "install");
            var r2 = Path.Combine(tmp, "user");
            Plugin(r2, "com.example.b");
            Plugin(r1, "com.example.a");
            var result = PluginDiscovery.Scan([r1, r2]);
            Assert.Equal(new[] { "com.example.a", "com.example.b" }, result.Select(p => p.Id).ToArray());
            Assert.All(result, p => Assert.Equal(DiscoveryState.Valid, p.State));
            Assert.All(result, p => Assert.NotNull(p.Manifest));
            Assert.Equal(r1, result[0].Root);
            Assert.Equal(Path.Combine(r1, "com.example.a"), result[0].Folder);
            Assert.Null(result[0].ShadowedByFolder);
        }
        finally { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); }
    }

    [Fact]
    public void Scan_SkipsFoldersWithoutManifest()
    {
        var tmp = NewTemp();
        try
        {
            var r = Path.Combine(tmp, "root");
            Directory.CreateDirectory(Path.Combine(r, "empty"));
            Plugin(r, "com.example.a");
            var result = PluginDiscovery.Scan([r]);
            Assert.Equal("com.example.a", Assert.Single(result).Id);
        }
        finally { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); }
    }

    [Fact]
    public void Scan_MarksInvalidManifestWithErrors()
    {
        var tmp = NewTemp();
        try
        {
            var r = Path.Combine(tmp, "root");
            Plugin(r, "com.example.a", "{ not json");
            var p = Assert.Single(PluginDiscovery.Scan([r]));
            Assert.Equal(DiscoveryState.InvalidManifest, p.State);
            Assert.Null(p.Manifest);
            Assert.NotEmpty(p.Errors);
        }
        finally { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); }
    }

    [Fact]
    public void Scan_MarksIncompatibleSdk()
    {
        var tmp = NewTemp();
        try
        {
            var r = Path.Combine(tmp, "root");
            Plugin(r, "com.example.a", Manifest("com.example.a", "99.0.0"));
            var p = Assert.Single(PluginDiscovery.Scan([r]));
            Assert.Equal(DiscoveryState.IncompatibleSdk, p.State);
            Assert.Contains(p.Errors, e => e.Contains("sdkVersion"));
        }
        finally { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); }
    }

    [Fact]
    public void Scan_LaterRootShadowsEarlierRoot()
    {
        var tmp = NewTemp();
        try
        {
            var install = Path.Combine(tmp, "install");
            var user = Path.Combine(tmp, "user");
            Plugin(install, "com.example.a");
            var userFolder = Plugin(user, "com.example.a");
            var result = PluginDiscovery.Scan([install, user]);
            Assert.Equal(2, result.Count);
            var shadowed = result.Single(p => p.Root == install);
            var live = result.Single(p => p.Root == user);
            Assert.Equal(DiscoveryState.Shadowed, shadowed.State);
            Assert.Equal(userFolder, shadowed.ShadowedByFolder);
            Assert.NotNull(shadowed.Manifest);
            Assert.Equal(DiscoveryState.Valid, live.State);
            Assert.Null(live.ShadowedByFolder);
        }
        finally { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); }
    }

    [Fact]
    public void Scan_MissingRootIsNotAnError()
    {
        var tmp = NewTemp();
        Assert.Empty(PluginDiscovery.Scan([Path.Combine(tmp, "does-not-exist")]));
    }

    [Fact]
    public void Scan_SkipsDotPrefixedFolders()
    {
        var tmp = NewTemp();
        try
        {
            Plugin(tmp, "com.example.a");
            // Installer staging, replaced and removing folders carry a real plugin.json.
            Plugin(tmp, ".com.example.a.installing-0123", Manifest("com.example.a"));
            Plugin(tmp, ".com.example.a.replaced-4567", Manifest("com.example.a"));
            Plugin(tmp, ".com.example.b.removing-89ab", Manifest("com.example.b"));

            var result = PluginDiscovery.Scan([tmp]);

            var only = Assert.Single(result);
            Assert.Equal("com.example.a", only.Id);
            Assert.Equal(DiscoveryState.Valid, only.State);
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Scan_RecordsTheManifestHashFromItsOnlyRead()
    {
        var tmp = NewTemp();
        try
        {
            var folder = Plugin(tmp, "com.example.a");
            var p = Assert.Single(PluginDiscovery.Scan([tmp]));
            Assert.Equal(DiscoveryState.Valid, p.State);
            Assert.Equal(PluginPackageHash.ComputeManifestHash(folder), p.ManifestHash);
        }
        finally { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); }
    }

    [Fact]
    public void Scan_ManifestWithUtf8Bom_IsValid()
    {
        var tmp = NewTemp();
        try
        {
            var folder = Path.Combine(tmp, "com.example.a");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "plugin.json");
            File.WriteAllText(path, Manifest("com.example.a"), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, File.ReadAllBytes(path)[..3]);

            var p = Assert.Single(PluginDiscovery.Scan([tmp]));

            Assert.Equal(DiscoveryState.Valid, p.State);
            Assert.Empty(p.Errors);
            Assert.Equal("com.example.a", p.Manifest!.Id);
            // The hash covers the bytes on disk, BOM included, as the approvals and bundled.json record it.
            Assert.Equal(PluginPackageHash.ComputeManifestHash(folder), p.ManifestHash);
        }
        finally { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); }
    }

    /// Parked #28: a plugin.json over the 1 MiB cap is invalid, is read at most cap + 1 bytes and keeps no bytes; one of
    /// exactly the cap is read as usual.
    [Fact]
    public void Discover_ManifestLargerThanTheCap_IsInvalidWithoutReadingItWhole()
    {
        var tmp = NewTemp();
        try
        {
            var manifest = Manifest("com.example.big");
            var atCap = manifest + new string(' ', PluginManifestReader.MaxManifestBytes - Encoding.UTF8.GetByteCount(manifest));
            Plugin(tmp, "com.example.big", atCap + " ");
            Plugin(tmp, "com.example.cap", atCap.Replace("com.example.big", "com.example.cap", StringComparison.Ordinal));
            // Far over the cap: a whole read would allocate 64 MiB for nothing.
            var huge = Plugin(tmp, "com.example.huge", Manifest("com.example.huge"));
            using (var stream = new FileStream(Path.Combine(huge, "plugin.json"), FileMode.Open, FileAccess.Write))
            {
                stream.SetLength(64L * 1024 * 1024);
            }

            var result = PluginDiscovery.Scan([tmp]).ToDictionary(p => p.Id);

            foreach (var id in new[] { "com.example.big", "com.example.huge" })
            {
                var p = result[id];
                Assert.Equal(DiscoveryState.InvalidManifest, p.State);
                Assert.Null(p.Manifest);
                Assert.Equal(["plugin.json is larger than 1 MiB."], p.Errors);
                Assert.Null(p.ManifestHash);
                Assert.Null(p.ManifestBytes);
            }

            var cap = result["com.example.cap"];
            Assert.Equal(DiscoveryState.Valid, cap.State);
            Assert.Equal(PluginManifestReader.MaxManifestBytes, cap.ManifestBytes!.Length);
            Assert.Equal(PluginPackageHash.ComputeManifestHash(Path.Combine(tmp, "com.example.cap")), cap.ManifestHash);
        }
        finally { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); }
    }

    /// The bounded read never takes more than cap + 1 bytes from a stream, however long it is, and returns the exact
    /// bytes of one within the cap; a stream that cannot report its length is read the same way.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReadManifestBytes_ReadsAtMostOneByteOverTheCap(bool seekable)
    {
        using var endless = new CountingStream(long.MaxValue, seekable);
        Assert.False(PluginManifestReader.TryReadManifestBytes(endless, out var none));
        Assert.Empty(none);
        Assert.InRange(endless.BytesRead, 0, PluginManifestReader.MaxManifestBytes + 1L);

        using var small = new CountingStream(1234, seekable);
        Assert.True(PluginManifestReader.TryReadManifestBytes(small, out var bytes));
        Assert.Equal(1234, bytes.Length);
        Assert.All(bytes, b => Assert.Equal((byte)'x', b));

        using var exact = new CountingStream(PluginManifestReader.MaxManifestBytes, seekable);
        Assert.True(PluginManifestReader.TryReadManifestBytes(exact, out var full));
        Assert.Equal(PluginManifestReader.MaxManifestBytes, full.Length);
    }

    /// A read-only stream of <c>length</c> 'x' bytes that counts what it hands out and allocates nothing.
    private sealed class CountingStream(long length, bool seekable) : Stream
    {
        private long _position;

        public long BytesRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => seekable;
        public override bool CanWrite => false;
        public override long Length => seekable ? length : throw new NotSupportedException();

        public override long Position
        {
            get => seekable ? _position : throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = (int)Math.Min(count, length - _position);
            buffer.AsSpan(offset, n).Fill((byte)'x');
            _position += n;
            BytesRead += n;
            return n;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public void InstallRoot_IsThePluginsFolderNextToTheApp() =>
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "plugins"), PluginDiscovery.InstallRoot());
}
