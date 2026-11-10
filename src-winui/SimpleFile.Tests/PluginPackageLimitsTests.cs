using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using SimpleFile.PluginHost.Install;
using Xunit;

namespace SimpleFile.Tests;

/// <summary>
/// Final fix wave, items 1 and 5: the <c>.sfplugin</c> installer caps the entry count and the total uncompressed
/// size (never trusting an entry's declared length), refuses Windows device names and segments ending in a dot or a
/// space, and calls its approval hook before the new files go into place (Install) or the folder goes (Remove).
/// </summary>
public sealed class PluginPackageLimitsTests : IDisposable
{
    private const string Id = "com.example.pkg";
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "sumafile-tests", Guid.NewGuid().ToString("N"));

    public PluginPackageLimitsTests()
    {
        Directory.CreateDirectory(_temp);
    }

    public void Dispose()
    {
        try { Directory.Delete(_temp, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private string PluginsRoot => Path.Combine(_temp, "plugins");

    private static string Manifest(string version = "1.0.0") =>
        "{ \"id\": \"" + Id + "\", \"name\": \"N\", \"version\": \"" + version + "\", \"sdkVersion\": \"1.0.0\", " +
        "\"publisher\": { \"name\": \"P\" } }";

    private string Package(params (string Name, byte[] Content)[] entries)
    {
        var path = Path.Combine(_temp, Guid.NewGuid().ToString("N") + ".sfplugin");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            using var stream = zip.CreateEntry(name).Open();
            stream.Write(content);
        }

        return path;
    }

    private static (string, byte[]) Text(string name, string content) => (name, System.Text.Encoding.UTF8.GetBytes(content));

    private void AssertNothingInstalled()
    {
        Assert.False(Directory.Exists(Path.Combine(PluginsRoot, Id)));
        Assert.False(Directory.Exists(PluginsRoot) && Directory.EnumerateFileSystemEntries(PluginsRoot).Any());
    }

    [Fact]
    public void Limits_AreTenThousandEntriesAnd512MiB()
    {
        Assert.Equal(10_000, PluginPackageInstaller.MaxEntries);
        Assert.Equal(512L * 1024 * 1024, PluginPackageInstaller.MaxUncompressedBytes);
    }

    [Fact]
    public void Install_MoreEntriesThanTheCap_IsRefusedAndWritesNothing()
    {
        var entries = new List<(string, byte[])> { Text("plugin.json", Manifest()) };
        entries.AddRange(Enumerable.Range(0, 3).Select(i => Text($"f{i}.txt", "x")));
        var package = Package(entries.ToArray());

        var thrown = Assert.Throws<PluginInstallException>(
            () => PluginPackageInstaller.Install(package, PluginsRoot, null, new PackageLimits(MaxEntries: 3, MaxUncompressedBytes: 1 << 20)));

        Assert.Equal("The package has more than 3 entries.", thrown.Message);
        AssertNothingInstalled();

        // At the cap it installs.
        var folder = PluginPackageInstaller.Install(
            package, PluginsRoot, null, new PackageLimits(MaxEntries: 4, MaxUncompressedBytes: 1 << 20));
        Assert.True(File.Exists(Path.Combine(folder, "f2.txt")));
    }

    [Fact]
    public void Install_TheRealEntryCap_IsTenThousand()
    {
        var entries = new List<(string, byte[])> { Text("plugin.json", Manifest()) };
        entries.AddRange(Enumerable.Range(0, PluginPackageInstaller.MaxEntries).Select(i => (Name: $"d/{i}", Content: Array.Empty<byte>())));
        var package = Package(entries.ToArray());

        var thrown = Assert.Throws<PluginInstallException>(() => PluginPackageInstaller.Install(package, PluginsRoot));

        Assert.Equal("The package has more than 10,000 entries.", thrown.Message);
        AssertNothingInstalled();
    }

    [Fact]
    public void Install_MoreUncompressedBytesThanTheCap_IsRefusedAndLeavesNoStaging()
    {
        var limits = new PackageLimits(MaxEntries: 100, MaxUncompressedBytes: 64 * 1024);
        var manifest = Text("plugin.json", Manifest());
        var package = Package(manifest, ("a.bin", new byte[40 * 1024]), ("b.bin", new byte[40 * 1024]));

        var thrown = Assert.Throws<PluginInstallException>(() => PluginPackageInstaller.Install(package, PluginsRoot, null, limits));

        Assert.Equal("The package is larger than 64 KiB uncompressed.", thrown.Message);
        AssertNothingInstalled();
    }

    [Fact]
    public void Install_AnEntryThatUnderstatesItsSize_NeverPutsMoreThanTheCapOnDisk()
    {
        // The declared sizes add up to well under the cap, but the entry's data inflates to more. Today .NET stops a
        // Deflate entry at its declared size; the installer's own bounded copy holds whatever the stream yields.
        var limits = new PackageLimits(MaxEntries: 100, MaxUncompressedBytes: 64 * 1024);
        var package = Package(Text("plugin.json", Manifest()), ("big.bin", new byte[200 * 1024]));
        UnderstateUncompressedSize(package, "big.bin", 10);
        using (var check = ZipFile.OpenRead(package))
        {
            Assert.Equal(10, check.GetEntry("big.bin")!.Length);
        }

        try
        {
            var folder = PluginPackageInstaller.Install(package, PluginsRoot, null, limits);
            Assert.True(new FileInfo(Path.Combine(folder, "big.bin")).Length <= limits.MaxUncompressedBytes);
        }
        catch (PluginInstallException)
        {
            AssertNothingInstalled();
        }
    }

    [Fact]
    public void CopyBounded_AStreamLongerThanTheBudget_StopsOneBytePastIt()
    {
        var input = new CountingStream(new MemoryStream(new byte[1_000_000]));
        using var output = new MemoryStream();

        var thrown = Assert.Throws<PluginInstallException>(
            () => PluginPackageInstaller.CopyBounded(input, output, budget: 1000, tooLarge: "too large"));

        Assert.Equal("too large", thrown.Message);
        Assert.Equal(1001, input.BytesRead);
        Assert.True(output.Length <= 1000);
    }

    [Fact]
    public void CopyBounded_AStreamAtTheBudget_CopiesItAll()
    {
        var data = Enumerable.Range(0, 1000).Select(i => (byte)i).ToArray();
        using var output = new MemoryStream();

        Assert.Equal(1000, PluginPackageInstaller.CopyBounded(new MemoryStream(data), output, budget: 1000, tooLarge: "too large"));
        Assert.Equal(data, output.ToArray());
    }

    private sealed class CountingStream(Stream inner) : Stream
    {
        public long BytesRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            BytesRead += read;
            return read;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("con.txt")]
    [InlineData("lib/NUL")]
    [InlineData("lib/nul.tar.gz")]
    [InlineData("Aux.dll")]
    [InlineData("prn")]
    [InlineData("COM1")]
    [InlineData("com9.log")]
    [InlineData("LPT1")]
    [InlineData("lpt9.x")]
    [InlineData("assets/CON/readme.txt")]
    [InlineData("CON .txt")]
    [InlineData("COM0.txt")]
    [InlineData("conin$")]
    [InlineData("COM\u00B9")]
    public void Install_AWindowsDeviceName_IsRefused(string entry)
    {
        var package = Package(Text("plugin.json", Manifest()), Text(entry, "x"));

        var thrown = Assert.Throws<PluginInstallException>(() => PluginPackageInstaller.Install(package, PluginsRoot));

        Assert.Equal($"The package entry '{entry}' uses a name Windows reserves for a device.", thrown.Message);
        AssertNothingInstalled();
    }

    [Theory]
    [InlineData("COM10.txt")]
    [InlineData("LPT10")]
    [InlineData("console.dll")]
    [InlineData("nul_driver.dll")]
    [InlineData("auxiliary/a.txt")]
    [InlineData("CONFIG.json")]
    public void Install_NamesThatOnlyLookLikeDevices_Install(string entry)
    {
        var folder = PluginPackageInstaller.Install(Package(Text("plugin.json", Manifest()), Text(entry, "x")), PluginsRoot);

        Assert.Equal("x", File.ReadAllText(Path.Combine(folder, entry)));
    }

    [Theory]
    [InlineData("readme.")]
    [InlineData("readme ")]
    [InlineData("lib./a.dll")]
    [InlineData("lib /a.dll")]
    public void Install_ASegmentEndingInADotOrSpace_IsRefused(string entry)
    {
        var package = Package(Text("plugin.json", Manifest()), Text(entry, "x"));

        var thrown = Assert.Throws<PluginInstallException>(() => PluginPackageInstaller.Install(package, PluginsRoot));

        Assert.Equal($"The package entry '{entry}' has a name that ends in a dot or a space, which Windows cannot keep.", thrown.Message);
        AssertNothingInstalled();
    }

    [Fact]
    public void Install_CallsTheHookWithTheIdBeforeReplacingTheFolder()
    {
        var existing = Path.Combine(PluginsRoot, Id);
        Directory.CreateDirectory(existing);
        File.WriteAllText(Path.Combine(existing, "plugin.json"), Manifest("0.9.0"));
        string? seenId = null;
        string? manifestWhenCalled = null;

        var folder = PluginPackageInstaller.Install(
            Package(Text("plugin.json", Manifest("2.0.0"))),
            PluginsRoot,
            id =>
            {
                seenId = id;
                manifestWhenCalled = File.ReadAllText(Path.Combine(existing, "plugin.json"));
            });

        Assert.Equal(Id, seenId);
        Assert.Contains("0.9.0", manifestWhenCalled);
        Assert.Contains("2.0.0", File.ReadAllText(Path.Combine(folder, "plugin.json")));
    }

    [Fact]
    public void Install_AHookThatThrows_LeavesThePreviousVersionAndNoStaging()
    {
        var existing = Path.Combine(PluginsRoot, Id);
        Directory.CreateDirectory(existing);
        File.WriteAllText(Path.Combine(existing, "plugin.json"), Manifest("0.9.0"));

        var thrown = Assert.Throws<PluginInstallException>(() => PluginPackageInstaller.Install(
            Package(Text("plugin.json", Manifest("2.0.0"))),
            PluginsRoot,
            _ => throw new IOException("approvals locked")));

        Assert.Equal("The plugin could not be installed: approvals locked", thrown.Message);
        Assert.Contains("0.9.0", File.ReadAllText(Path.Combine(existing, "plugin.json")));
        Assert.Equal(new[] { Id }, Directory.GetDirectories(PluginsRoot).Select(Path.GetFileName).ToArray());
    }

    [Fact]
    public void Install_AnInvalidPackage_NeverCallsTheHook()
    {
        var called = false;
        var package = Package(Text("plugin.json", Manifest()), Text("CON", "x"));

        Assert.Throws<PluginInstallException>(() => PluginPackageInstaller.Install(package, PluginsRoot, _ => called = true));

        Assert.False(called);
    }

    [Fact]
    public void Remove_CallsTheHookWithTheIdAndDeletesTheFolder()
    {
        var folder = PluginPackageInstaller.Install(Package(Text("plugin.json", Manifest())), PluginsRoot);
        string? seenId = null;

        PluginPackageInstaller.Remove(folder, PluginsRoot, id => seenId = id);

        Assert.Equal(Id, seenId);
        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public void Remove_AHookThatThrows_KeepsTheFolder()
    {
        var folder = PluginPackageInstaller.Install(Package(Text("plugin.json", Manifest())), PluginsRoot);

        var thrown = Assert.Throws<PluginInstallException>(
            () => PluginPackageInstaller.Remove(folder, PluginsRoot, _ => throw new IOException("approvals locked")));

        Assert.Equal($"The plugin {Id} could not be removed: approvals locked", thrown.Message);
        Assert.True(File.Exists(Path.Combine(folder, "plugin.json")));
    }

    /// <summary>
    /// Rewrites the uncompressed size of <paramref name="entryName"/> in its local header and its central directory
    /// record, so the archive declares less than the entry inflates to.
    /// </summary>
    private static void UnderstateUncompressedSize(string zipPath, string entryName, uint declared)
    {
        var bytes = File.ReadAllBytes(zipPath);
        var name = System.Text.Encoding.UTF8.GetBytes(entryName);
        var patched = 0;
        for (var i = 0; i + 46 < bytes.Length; i++)
        {
            var signature = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i));
            if (signature == 0x04034b50 && Matches(bytes, i + 30, name, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(i + 26))))
            {
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i + 22), declared);
                patched++;
            }
            else if (signature == 0x02014b50 && Matches(bytes, i + 46, name, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(i + 28))))
            {
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i + 24), declared);
                patched++;
            }
        }

        Assert.Equal(2, patched);
        File.WriteAllBytes(zipPath, bytes);
    }

    private static bool Matches(byte[] bytes, int offset, byte[] name, int length) =>
        length == name.Length && offset + length <= bytes.Length && bytes.AsSpan(offset, length).SequenceEqual(name);
}
